using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pairrot.Core.Payments;

public static class PaymentRequestErrors
{
    public const string InvalidBankConnection = "invalid_bank_connection";
    public const string InvalidExternalId = "invalid_external_id";
    public const string InvalidDescription = "invalid_description";
    public const string InvalidDueAt = "invalid_due_at";
    public const string InvalidReason = "invalid_reason";
}

/// <summary>Creation input as received; <c>null</c> means omitted or explicitly null.</summary>
public sealed record PaymentRequestInput(
    Guid? BankConnectionId,
    string? ExternalId,
    long? Amount,
    string? Currency,
    string? VariableSymbol,
    string? Message,
    string? Description,
    DateTimeOffset? DueAt);

/// <summary>
/// Validated creation input. <see cref="Payment"/> has no account yet: the IBAN comes from the
/// saved bank connection.
/// </summary>
public sealed record PaymentRequestDraft(
    Guid BankConnectionId,
    string ExternalId,
    SpaydPayment Payment,
    string VariableSymbolKey,
    string? Description,
    DateTimeOffset? DueAt,
    string Fingerprint);

public sealed record PaymentRequestParseResult(PaymentRequestDraft? Draft, PaymentError? Error);

public static partial class PaymentRequestRules
{
    public const int MaxExternalIdLength = 200;
    public const int MaxDescriptionLength = 1000;
    public const int MaxCancellationReasonLength = 500;

    /// <summary>
    /// Identifies how <see cref="PaymentRequest.QrPayload"/> was produced: SPAYD 1.0, fields ACC,
    /// AM, CC, X-VS, MSG in this order, message as raw UTF-8 (Feature 004 after the George check).
    /// A renderer change gets a new version; saved payloads are never regenerated.
    /// </summary>
    public const string QrGeneratorVersion = "pairrot-spayd-1";

    /// <summary>Validates creation input in the order of the request fields; nothing here reads the database.</summary>
    public static PaymentRequestParseResult Validate(PaymentRequestInput input)
    {
        if (input.BankConnectionId is not { } connectionId)
        {
            return Fail(PaymentRequestErrors.InvalidBankConnection, "bank_connection_id", "bank_connection_id is required.");
        }

        if (ValidateExternalId(input.ExternalId) is { } externalIdError)
        {
            return Fail(PaymentRequestErrors.InvalidExternalId, "external_id", externalIdError);
        }

        if (input.VariableSymbol is null)
        {
            return Fail(PaymentErrors.InvalidVariableSymbol, "variable_symbol",
                $"variable_symbol is required: 1 to {SpaydPayment.MaxVariableSymbolLength} digits.");
        }

        var payment = SpaydPayment.Create("", input.Amount, input.Currency, input.VariableSymbol, input.Message);
        if (payment.Error is { } paymentError)
        {
            return new PaymentRequestParseResult(null, paymentError);
        }

        if (ValidateDescription(input.Description) is { } descriptionError)
        {
            return Fail(PaymentRequestErrors.InvalidDescription, "description", descriptionError);
        }

        var externalId = input.ExternalId!.Trim();
        var description = NormalizeText(input.Description);
        var dueAt = NormalizeDueAt(input.DueAt);
        var spayd = payment.Payment!;
        return new PaymentRequestParseResult(new PaymentRequestDraft(
            connectionId,
            externalId,
            spayd,
            VariableSymbolKey(spayd.VariableSymbol!),
            description,
            dueAt,
            Fingerprint(connectionId, externalId, spayd, description, dueAt)), null);
    }

    public static string? ValidateExternalId(string? externalId) => externalId switch
    {
        null => "external_id is required.",
        _ when string.IsNullOrWhiteSpace(externalId) => "external_id must not be blank.",
        _ when externalId.Trim().Length > MaxExternalIdLength => $"external_id must be at most {MaxExternalIdLength} characters.",
        _ => null,
    };

    public static string? ValidateDescription(string? description) =>
        description is not null && description.Trim().Length > MaxDescriptionLength
            ? $"description must be at most {MaxDescriptionLength} characters."
            : null;

    public static string? ValidateCancellationReason(string? reason) =>
        reason is not null && reason.Trim().Length > MaxCancellationReasonLength
            ? $"reason must be at most {MaxCancellationReasonLength} characters."
            : null;

    /// <summary>Trimmed text, or <c>null</c> for blank or omitted text.</summary>
    public static string? NormalizeText(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>UTC, truncated to the microseconds PostgreSQL stores, so saved and returned values are identical.</summary>
    public static DateTimeOffset? NormalizeDueAt(DateTimeOffset? dueAt) => dueAt is { } value
        ? value.ToUniversalTime().AddTicks(-(value.UtcTicks % TimeSpan.TicksPerMicrosecond))
        : null;

    /// <summary>
    /// The symbol for comparison: leading zeros removed, all zeros become <c>0</c>. A bank may drop
    /// leading zeros, so <c>123</c> and <c>000123</c> must not identify two different requests.
    /// </summary>
    public static string VariableSymbolKey(string variableSymbol)
    {
        var key = variableSymbol.TrimStart('0');
        return key.Length == 0 ? "0" : key;
    }

    /// <summary>Overdue is computed: an open request whose due time has passed. It never changes the status.</summary>
    public static bool IsOverdue(PaymentRequest request, DateTimeOffset now) =>
        request.Status == PaymentRequestStatus.Open && request.DueAt is { } due && due < now;

    /// <summary>
    /// Parses an ISO 8601 timestamp that states its offset, e.g. <c>2026-10-15T18:00:00+02:00</c>
    /// or <c>...Z</c>. A timestamp without an offset is ambiguous and rejected.
    /// </summary>
    public static bool TryParseDueAt(string value, out DateTimeOffset dueAt)
    {
        dueAt = default;
        return IsoTimestampWithOffset().IsMatch(value)
            && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out dueAt);
    }

    /// <summary>
    /// SHA-256 over a canonical JSON array of the validated creation input. Omitted and null
    /// optional fields are both <c>null</c>; the symbol and message are kept exactly as they go
    /// into the payment, so <c>000123</c> and <c>123</c> are different input.
    /// </summary>
    public static string Fingerprint(
        Guid connectionId, string externalId, SpaydPayment payment, string? description, DateTimeOffset? dueAt)
    {
        object?[] canonical =
        [
            "v1",
            connectionId.ToString("D"),
            externalId,
            payment.Amount,
            payment.Currency,
            payment.VariableSymbol,
            payment.Message,
            description,
            dueAt?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture),
        ];
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(canonical)));
        return Convert.ToHexStringLower(bytes);
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d{1,7})?)?(Z|[+-]\d{2}:\d{2})$")]
    private static partial Regex IsoTimestampWithOffset();

    private static PaymentRequestParseResult Fail(string code, string field, string message) =>
        new(null, new PaymentError(code, field, message));
}
