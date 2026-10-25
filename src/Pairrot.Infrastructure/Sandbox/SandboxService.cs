using System.Text.Json;
using Microsoft.Extensions.Options;
using Pairrot.Core.Banking;
using Pairrot.Core.Inbound;
using Pairrot.Core.Parsing;
using Pairrot.Core.Payments;
using Pairrot.Core.Tenants;
using Pairrot.Infrastructure.Banking;
using Pairrot.Infrastructure.Inbound;
using Pairrot.Infrastructure.Payments;
using Pairrot.Infrastructure.Persistence;
using Pairrot.Infrastructure.Tenants;

namespace Pairrot.Infrastructure.Sandbox;

/// <summary>A simulated incoming payment as the caller described it; absent fields take their defaults.</summary>
/// <param name="Direction"><c>incoming</c> (default) or <c>outgoing</c>.</param>
/// <param name="IdempotencyKey">When given, the delivery identity derives from it, so a retry creates no second message.</param>
public sealed record SandboxPaymentInput(
    long? Amount,
    string? VariableSymbol,
    string? Message,
    string? PayerName,
    string? PayerAccount,
    DateTimeOffset? BookedAt,
    string? BankReference,
    string? Direction,
    string? IdempotencyKey);

/// <param name="Profile">The bank profile that parses the notification.</param>
public sealed record SandboxSimulation(Guid MessageId, Guid BankConnectionId, string Profile, Guid? PaymentRequestId = null);

/// <summary>
/// Feature 021: renders a simulated payment as the bank's own notification and hands it to the
/// real inbound intake through the connection's test address, so everything after that point is
/// the production pipeline. Only mapped and registered for use in sandbox mode.
/// </summary>
public class SandboxService(
    PairrotDbContext db,
    TenantService tenants,
    BankConnectionService banks,
    PaymentRequestService requests,
    BankProfileRegistry profiles,
    IEnumerable<ISandboxNoticeRenderer> renderers,
    IInboundEmailIntake intake,
    IOptions<SandboxOptions> options,
    TimeProvider time)
{
    public const string TransportKind = "sandbox";
    public const string DefaultPayerName = "Sandbox Plátce";
    public const string DefaultPayerAccount = "19-2000145399/0100";
    public const int MaxMessageLength = 140;
    public const int MaxKeyLength = 100;

    public bool Enabled => options.Value.Enabled;

    /// <summary>The profiles sandbox notifications can be rendered for.</summary>
    public IReadOnlyList<string> ProfileNames => profiles.Installed.Select(p => p.Name).Where(n => renderers.Any(r => r.ProfileName == n)).ToList();

    public async Task<ServiceResult<SandboxSimulation>> SimulateAsync(
        Guid callerTenantId, Guid? subtenantId, Guid connectionId, SandboxPaymentInput input, Actor actor, CancellationToken ct = default)
    {
        if (Validate(input) is { } invalid)
        {
            return invalid;
        }

        var found = await banks.GetAsync(callerTenantId, subtenantId, connectionId, ct);
        if (found.Error is { } notFound)
        {
            return notFound;
        }

        var connection = found.Value!.Connection;
        if (connection.Status == BankConnectionStatus.Archived)
        {
            return ServiceError.Conflict("bank_connection_archived", "An archived bank connection cannot receive simulated payments.");
        }

        var destination = CzechBankAccount.ParseIban(connection.Iban).Account!;
        var profile = profiles.For(destination);
        var renderer = profile is null ? null : renderers.FirstOrDefault(r => r.ProfileName == profile.Name);
        if (renderer is null)
        {
            return ServiceError.Conflict("sandbox_bank_not_supported", $"No sandbox notification exists for bank code {destination.BankCode}.");
        }

        if (input.BankReference is not null && !renderer.SupportsBankReference)
        {
            return ServiceError.Validation("field_not_supported_by_bank", "bank_reference",
                $"The {renderer.ProfileName} notification has no bank reference; bank_reference is supported by the sandbox profile only.");
        }

        var address = found.Value.Address;
        if (address is null)
        {
            var assigned = await banks.AssignAddressAsync(callerTenantId, subtenantId, connectionId, actor, ct);
            if (assigned.Error is { } assignFailure)
            {
                return assignFailure;
            }

            address = assigned.Value!.Address;
        }

        if (address is not { ProvisioningState: InboundProvisioningState.Ready, Environment: InboundEnvironment.Test })
        {
            return ServiceError.Conflict("sandbox_requires_test_address",
                "The connection's inbound address is not a ready test address; sandbox payments are delivered through test instances only.");
        }

        var now = time.GetUtcNow();
        var payment = new SandboxPayment(
            destination, connection.Currency, input.Amount!.Value, input.BookedAt ?? now,
            PayerAccountOf(input.PayerAccount)!, input.PayerName ?? DefaultPayerName,
            input.VariableSymbol, input.Message, input.BankReference, input.Direction != "outgoing");
        var notice = renderer.Render(payment);
        var identity = $"sandbox:{connection.TenantId}:{input.IdempotencyKey ?? Guid.NewGuid().ToString("N")}";
        var accepted = await intake.AcceptAsync(new InboundDelivery
        {
            ProviderInstanceId = address.ProviderInstanceId,
            SourceDeliveryId = identity,
            SourceMessageId = identity,
            ReceivedAt = now,
            EnvelopeRecipients = [address.Address],
            RecipientEvidence = TransportKind,
            EnvelopeSender = notice.EnvelopeSender,
            HeaderFrom = notice.HeaderFrom,
            Subject = notice.Subject,
            TransportKind = TransportKind,
            DeliveryEvidence = TransportKind,
            // The caller's input, so a retry with the same key is the same delivery and a changed one is a conflict.
            RawTransportPayload = JsonSerializer.Serialize(input with { IdempotencyKey = null }),
            SenderAuthentication = Authentication,
            Content = new InboundContentSnapshot(notice.Text, notice.Html, null, [address.Address], null, Authentication),
        }, ct);

        switch (accepted.Status)
        {
            case IntakeStatus.Accepted or IntakeStatus.Duplicate:
                break;
            case IntakeStatus.Conflict:
                return ServiceError.Conflict("idempotency_key_conflict", "This Idempotency-Key was already used for a different simulated payment.");
            default:
                throw new InvalidOperationException($"Intake did not accept the sandbox delivery: {accepted.Status} {accepted.Error}");
        }

        if (accepted.Status == IntakeStatus.Accepted)
        {
            tenants.AddAudit(actor, connection.TenantId, AuditActions.SandboxPaymentSimulated, new
            {
                ConnectionId = connection.Id,
                MessageId = accepted.MessageId,
                input.Amount,
                input.VariableSymbol,
            });
            await db.SaveChangesAsync(ct);
        }

        return new SandboxSimulation(accepted.MessageId!.Value, connection.Id, renderer.ProfileName);
    }

    /// <summary>
    /// Simulates a payment of one payment request: by default its unpaid remainder with its own
    /// variable symbol, through its own connection. A cancelled request is allowed on purpose.
    /// </summary>
    public async Task<ServiceResult<SandboxSimulation>> PayAsync(
        Guid callerTenantId, Guid? subtenantId, Guid requestId, SandboxPaymentInput input, Actor actor, CancellationToken ct = default)
    {
        var found = await requests.GetAsync(callerTenantId, subtenantId, requestId, ct);
        if (found.Error is { } notFound)
        {
            return notFound;
        }

        var request = found.Value!;
        var amount = input.Amount ?? request.Amount - request.PaidAmount;
        if (input.Amount is null && amount < SpaydPayment.MinAmount)
        {
            return ServiceError.Conflict("payment_request_already_paid", "The payment request is already paid in full; send an amount to simulate a further payment.");
        }

        var result = await SimulateAsync(callerTenantId, subtenantId, request.BankConnectionId,
            input with { Amount = amount, VariableSymbol = input.VariableSymbol ?? request.VariableSymbol }, actor, ct);
        return result.Error is { } failure ? failure : result.Value! with { PaymentRequestId = request.Id };
    }

    private static readonly SenderAuthentication Authentication =
        new("pass", "pass", "pass", "sandbox", """{"spf":"pass","dkim":"pass","dmarc":"pass"}""");

    private static CzechBankAccount? PayerAccountOf(string? text)
    {
        var value = (text ?? DefaultPayerAccount).Trim();
        return (value.Contains('/') ? CzechBankAccount.ParseDomestic(value) : CzechBankAccount.ParseIban(value)).Account;
    }

    private static ServiceError? Validate(SandboxPaymentInput input)
    {
        if (input.Amount is not { } amount || amount is < SpaydPayment.MinAmount or > SpaydPayment.MaxAmount)
        {
            return ServiceError.Validation("invalid_amount", "amount",
                $"amount is required: a whole number of minor units from {SpaydPayment.MinAmount} to {SpaydPayment.MaxAmount} (45000 means CZK 450.00).");
        }

        if (input.VariableSymbol is { } symbol
            && (symbol.Length is 0 or > SpaydPayment.MaxVariableSymbolLength || !symbol.All(char.IsAsciiDigit)))
        {
            return ServiceError.Validation("invalid_variable_symbol", "variable_symbol",
                $"variable_symbol must be 1 to {SpaydPayment.MaxVariableSymbolLength} digits.");
        }

        if (input.Message is { Length: > MaxMessageLength })
        {
            return ServiceError.Validation("invalid_message", "message", $"message can have at most {MaxMessageLength} characters.");
        }

        if (input.PayerName is { Length: 0 or > BankMessageEvaluator.MaxNameLength })
        {
            return ServiceError.Validation("invalid_payer_name", "payer_name", $"payer_name must be 1 to {BankMessageEvaluator.MaxNameLength} characters.");
        }

        if (input.PayerAccount is not null && PayerAccountOf(input.PayerAccount) is null)
        {
            return ServiceError.Validation("invalid_payer_account", "payer_account", "payer_account must be a valid Czech account number, e.g. 19-2000145399/0100.");
        }

        if (input.BankReference is { Length: 0 or > BankMessageEvaluator.MaxReferenceLength })
        {
            return ServiceError.Validation("invalid_bank_reference", "bank_reference",
                $"bank_reference must be 1 to {BankMessageEvaluator.MaxReferenceLength} characters.");
        }

        if (input.Direction is not (null or "incoming" or "outgoing"))
        {
            return ServiceError.Validation("invalid_direction", "direction", "direction must be 'incoming' or 'outgoing'.");
        }

        if (input.IdempotencyKey is { } key && (key.Length is 0 or > MaxKeyLength || key.Any(c => c is < '!' or > '~')))
        {
            return ServiceError.Validation("invalid_idempotency_key", "Idempotency-Key",
                $"Idempotency-Key must be 1 to {MaxKeyLength} visible ASCII characters.");
        }

        return null;
    }
}
