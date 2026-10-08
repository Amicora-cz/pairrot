using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pairrot.Core.Banking;
using Pairrot.Core.Payments;

namespace Pairrot.Core.Parsing;

/// <summary>The completed result of one parse attempt. Queue and retry state belongs to the job, not here.</summary>
public enum ParseOutcome
{
    /// <summary>A supported notification yielded a transaction and its source link.</summary>
    Parsed,

    /// <summary>The message supports a transaction already known by its proven bank reference; only a link was added.</summary>
    Duplicate,

    /// <summary>Recognized, but not a booked incoming transfer (a debit, a balance, a pending movement).</summary>
    NotPayment,

    /// <summary>No profile or no verified template; not retried automatically.</summary>
    Unsupported,

    /// <summary>Untrusted, ambiguous, incomplete, conflicting, or a correction. Candidates are kept, nothing is accepted.</summary>
    ReviewRequired,

    /// <summary>An unexpected failure after bounded retries.</summary>
    Failed,
}

/// <param name="Code">A stable machine-readable code from <see cref="ParseReasons"/>.</param>
/// <param name="Message">A readable explanation, safe to show to the tenant.</param>
public sealed record ParseReason(string Code, string Message);

public static class ParseReasons
{
    public const string Parsed = "parsed";
    public const string BankNotSupported = "bank_not_supported";
    public const string NoVerifiedTemplates = "no_verified_templates";
    public const string NoSupportedContent = "no_supported_content";
    public const string TemplateNotRecognized = "template_not_recognized";
    public const string MultipleMovements = "multiple_movements";
    public const string OutgoingTransfer = "outgoing_transfer";
    public const string BalanceSummary = "balance_summary";
    public const string PendingMovement = "pending_movement";
    public const string CorrectionNotice = "correction_notice";
    public const string TextHtmlConflict = "text_html_conflict";

    public const string SenderInvalid = "sender_invalid";
    public const string SenderNotAllowed = "sender_not_allowed";
    public const string NoVerifiedSenders = "no_verified_senders";
    public const string AuthenticationUnknown = "authentication_unknown";
    public const string DmarcNotPass = "dmarc_not_pass";
    public const string ForwarderNotAuthenticated = "forwarder_not_authenticated";

    public const string AmountMissing = "amount_missing";
    public const string AmountInvalid = "amount_invalid";
    public const string AmountPrecision = "amount_precision";
    public const string AmountOverflow = "amount_overflow";
    public const string CurrencyMissing = "currency_missing";
    public const string CurrencyInvalid = "currency_invalid";
    public const string CurrencyMismatch = "currency_mismatch";
    public const string InvalidVariableSymbol = "invalid_variable_symbol";
    public const string BookedOnMissing = "booked_on_missing";
    public const string BookedOnInvalid = "booked_on_invalid";
    public const string OccurredAtInvalid = "occurred_at_invalid";
    public const string OccurredAtAmbiguous = "occurred_at_ambiguous";
    public const string DestinationMissing = "destination_missing";
    public const string DestinationInvalid = "destination_invalid";
    public const string DestinationMismatch = "destination_mismatch";
    public const string DestinationInsufficient = "destination_insufficient";

    public const string Duplicate = "duplicate";
    public const string Unchanged = "unchanged";
    public const string BankReferenceConflict = "bank_reference_conflict";
    public const string ReprocessingChangedFacts = "reprocessing_changed_facts";
    public const string ReprocessingRejected = "reprocessing_rejected";
    public const string PossibleDuplicate = "possible_duplicate";

    /// <summary>Feature 013: another tenant's registration of the account recorded this or a similar movement.</summary>
    public const string AccountMovementConflict = "account_movement_conflict";

    public const string ProcessingFailed = "processing_failed";
}

/// <summary>How the notification identified the destination account.</summary>
public enum DestinationEvidence
{
    Missing,

    /// <summary>A full account that equals the saved connection.</summary>
    FullMatch,

    FullMismatch,

    /// <summary>A masked account the profile documents as sufficient.</summary>
    MaskedSufficient,

    MaskedInsufficient,
    Invalid,
}

/// <summary>
/// Normalized values extracted from one message, with the raw extraction kept as evidence. Under
/// review this is a candidate, not a recorded bank receipt.
/// </summary>
public sealed record TransferCandidate
{
    public long? Amount { get; init; }
    public string? Currency { get; init; }
    public string? VariableSymbol { get; init; }
    public string? VariableSymbolKey { get; init; }
    public string? BankReference { get; init; }
    public DestinationEvidence Destination { get; init; }

    /// <summary>Normalized domestic form when the sender account is complete and valid.</summary>
    public string? SenderAccount { get; init; }

    public string? SenderName { get; init; }
    public string? Message { get; init; }
    public DateOnly? BookedOn { get; init; }
    public DateTimeOffset? OccurredAt { get; init; }
    public required ExtractedFields Raw { get; init; }

    /// <summary>The facts that identify a movement; a change in any of them is a dispute, not an update.</summary>
    [JsonIgnore]
    public string CriticalFacts => Facts(Amount, Currency, VariableSymbol, BookedOn, BankReference);

    public static string Facts(long? amount, string? currency, string? variableSymbol, DateOnly? bookedOn, string? bankReference) =>
        JsonSerializer.Serialize(new object?[] { amount, currency, variableSymbol, bookedOn?.ToString("yyyy-MM-dd"), bankReference });
}

/// <summary>The result of evaluating one message with one profile; persistence and duplicates come afterwards.</summary>
/// <param name="Outcome">Never <see cref="ParseOutcome.Duplicate"/> or <see cref="ParseOutcome.Failed"/>: those need the database.</param>
/// <param name="Trust"><c>null</c> only when no profile handles the bank.</param>
public sealed record ParseEvaluation(
    ParseOutcome Outcome,
    IReadOnlyList<ParseReason> Reasons,
    TrustAssessment? Trust,
    string? TemplateId,
    SourcePart? Part,
    MessageKind? Kind,
    TransferCandidate? Candidate)
{
    public ParseReason PrimaryReason => Reasons.Count > 0
        ? Reasons[0]
        : new ParseReason(ParseReasons.Parsed, "A booked incoming transfer was recognized.");
}

/// <summary>
/// The common parsing pipeline: trust, template recognition in each verified representation,
/// normalization, and validation. Deterministic and side-effect free; it never reads provider
/// data, so the same snapshot yields the same evaluation whichever adapter delivered it.
/// </summary>
public static class BankMessageEvaluator
{
    public const int MaxNameLength = 200;
    public const int MaxMessageLength = 500;
    public const int MaxReferenceLength = 100;

    /// <summary>Trimmed, NUL-free text of at most <paramref name="maxLength"/> characters; <c>null</c> when blank.</summary>
    public static string? Limit(string? value, int maxLength) => TextLimits.Clean(value, maxLength);

    public static ParseEvaluation Evaluate(IBankProfile? profile, BankMessage message)
    {
        if (profile is null)
        {
            return new ParseEvaluation(ParseOutcome.Unsupported,
                [new ParseReason(ParseReasons.BankNotSupported, $"No bank profile handles bank code {message.Destination.BankCode}.")],
                null, null, null, null, null);
        }

        var trust = SourceTrust.Assess(message, profile.Senders);
        if (profile.Parts.Count == 0)
        {
            return new ParseEvaluation(ParseOutcome.Unsupported,
                [new ParseReason(ParseReasons.NoVerifiedTemplates,
                    $"The {profile.Name} profile has no verified notification templates yet.")],
                trust, null, null, null, null);
        }

        var matches = new List<(SourcePart Part, TemplateMatch Match)>();
        foreach (var part in profile.Parts)
        {
            var content = part == SourcePart.Text
                ? message.Text is { } text ? HtmlText.Lines(text) : null
                : message.Html is { } html ? HtmlText.ToText(html) : null;
            if (content is not null && trust.Evidence.OriginPath == OriginPath.ManualForward)
            {
                // A manual forward quotes the bank's notification, often with "> " markers.
                content = HtmlText.Unquote(content);
            }

            if (!string.IsNullOrEmpty(content))
            {
                matches.Add((part, profile.Recognize(content, part, message.Subject)));
            }
        }

        if (matches.Count == 0)
        {
            return new ParseEvaluation(ParseOutcome.Unsupported,
                [new ParseReason(ParseReasons.NoSupportedContent, "The message has no representation this profile supports.")],
                trust, null, null, null, null);
        }

        var (chosenPart, chosen) = matches[0];
        var normalized = matches.Select(m => (m.Part, m.Match, Normalized: m.Match is { Kind: MessageKind.IncomingTransfer, Fields: { } f }
            ? Normalize(f, profile, message)
            : default((TransferCandidate Candidate, List<ParseReason> Reasons)?))).ToList();
        var conflict = normalized.Skip(1).Any(other =>
            other.Match.Kind != chosen.Kind
            || other.Normalized?.Candidate.CriticalFacts != normalized[0].Normalized?.Candidate.CriticalFacts);

        ParseEvaluation Result(ParseOutcome outcome, List<ParseReason> reasons, TransferCandidate? candidate = null) =>
            new(outcome, reasons, trust, chosen.TemplateId, chosenPart, chosen.Kind, candidate);

        var conflictReason = new ParseReason(ParseReasons.TextHtmlConflict,
            "The text and HTML versions disagree on the notification type or payment-critical fields.");

        switch (chosen.Kind)
        {
            case MessageKind.Unrecognized:
                return Result(ParseOutcome.Unsupported,
                    [new ParseReason(ParseReasons.TemplateNotRecognized, "The message does not match a verified notification template.")]);
            case MessageKind.MultipleMovements:
                return Result(ParseOutcome.Unsupported,
                    [new ParseReason(ParseReasons.MultipleMovements, "Notifications with several movements are not supported.")]);
            case MessageKind.Correction:
                return Result(ParseOutcome.ReviewRequired,
                    [new ParseReason(ParseReasons.CorrectionNotice, "Reversals and corrections are reviewed, never treated as new payments.")]);
            case MessageKind.OutgoingTransfer or MessageKind.BalanceSummary or MessageKind.PendingMovement when conflict:
                return Result(ParseOutcome.ReviewRequired, [conflictReason]);
            case MessageKind.OutgoingTransfer:
                return Result(ParseOutcome.NotPayment, [new ParseReason(ParseReasons.OutgoingTransfer, "The notification describes a debit.")]);
            case MessageKind.BalanceSummary:
                return Result(ParseOutcome.NotPayment, [new ParseReason(ParseReasons.BalanceSummary, "The notification is a balance summary.")]);
            case MessageKind.PendingMovement:
                return Result(ParseOutcome.NotPayment,
                    [new ParseReason(ParseReasons.PendingMovement, "The movement is not booked yet; only booked transfers are recorded.")]);
        }

        if (chosen.Fields is null)
        {
            return Result(ParseOutcome.Unsupported,
                [new ParseReason(ParseReasons.TemplateNotRecognized, "The template matched but extracted no fields.")]);
        }

        var (candidate, reasons) = normalized[0].Normalized!.Value;
        reasons.InsertRange(0, trust.Reasons);
        if (conflict)
        {
            reasons.Add(conflictReason);
        }

        return Result(reasons.Count == 0 ? ParseOutcome.Parsed : ParseOutcome.ReviewRequired, reasons, candidate);
    }

    /// <summary>
    /// Normalizes raw fields. Every problem becomes a review reason; nothing is defaulted from an
    /// expected payment, rounded, or silently dropped.
    /// </summary>
    public static (TransferCandidate Candidate, List<ParseReason> Reasons) Normalize(
        ExtractedFields raw, IBankProfile profile, BankMessage message)
    {
        var reasons = new List<ParseReason>();
        var formats = profile.Formats;

        string? currency = null;
        var currencyText = raw.Currency?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(currencyText))
        {
            reasons.Add(new ParseReason(ParseReasons.CurrencyMissing, "The notification states no currency."));
        }
        else
        {
            currency = formats.CurrencyAliases.GetValueOrDefault(currencyText, currencyText);
            if (!BankAmount.KnownCurrencies.Contains(currency))
            {
                reasons.Add(new ParseReason(ParseReasons.CurrencyInvalid, $"'{raw.Currency}' is not a recognized currency."));
                currency = null;
            }
            else if (currency != message.Currency)
            {
                reasons.Add(new ParseReason(ParseReasons.CurrencyMismatch,
                    $"The notification is in {currency}, the connection in {message.Currency}."));
            }
        }

        long? amount = null;
        var precision = BankAmount.Precision.GetValueOrDefault(currency ?? message.Currency, 2);
        switch (BankAmount.TryParseMinorUnits(raw.Amount, formats.DecimalSeparator, formats.GroupSeparators, precision, out var units))
        {
            case null:
                amount = units;
                break;
            case AmountError.Missing:
                reasons.Add(new ParseReason(ParseReasons.AmountMissing, "The notification states no amount."));
                break;
            case AmountError.Precision:
                reasons.Add(new ParseReason(ParseReasons.AmountPrecision, $"The amount '{raw.Amount}' has more than {precision} decimal places."));
                break;
            case AmountError.Overflow:
                reasons.Add(new ParseReason(ParseReasons.AmountOverflow, $"The amount '{raw.Amount}' is too large."));
                break;
            default:
                reasons.Add(new ParseReason(ParseReasons.AmountInvalid, $"The amount '{raw.Amount}' is not a positive amount."));
                break;
        }

        string? symbol = null;
        if (!string.IsNullOrWhiteSpace(raw.VariableSymbol))
        {
            var text = raw.VariableSymbol.Trim();
            if (text.Length <= SpaydPayment.MaxVariableSymbolLength && text.All(char.IsAsciiDigit))
            {
                symbol = text;
            }
            else
            {
                reasons.Add(new ParseReason(ParseReasons.InvalidVariableSymbol,
                    $"The variable symbol '{TextLimits.Clean(raw.VariableSymbol, 40)}' is not 1 to {SpaydPayment.MaxVariableSymbolLength} digits."));
            }
        }

        DateOnly? bookedOn = null;
        if (string.IsNullOrWhiteSpace(raw.BookedOn))
        {
            reasons.Add(new ParseReason(ParseReasons.BookedOnMissing, "The notification states no booking date."));
        }
        else if (BankDates.TryParseDate(raw.BookedOn, formats.DateFormats, out var date))
        {
            bookedOn = date;
        }
        else
        {
            reasons.Add(new ParseReason(ParseReasons.BookedOnInvalid, $"The booking date '{raw.BookedOn}' is not in a verified format."));
        }

        DateTimeOffset? occurredAt = null;
        switch (BankDates.TryParseTimestamp(raw.OccurredAt, formats.TimestampFormats, formats.TimeZoneId, out var timestamp))
        {
            case BankDates.TimestampResult.Parsed:
                occurredAt = timestamp;
                break;
            case BankDates.TimestampResult.Invalid:
                reasons.Add(new ParseReason(ParseReasons.OccurredAtInvalid, $"The time '{raw.OccurredAt}' is not in a verified format."));
                break;
            case BankDates.TimestampResult.Ambiguous:
                reasons.Add(new ParseReason(ParseReasons.OccurredAtAmbiguous, $"The local time '{raw.OccurredAt}' is ambiguous at a clock change."));
                break;
        }

        var destination = CheckDestination(raw.DestinationAccount, profile, message.Destination);
        switch (destination)
        {
            case DestinationEvidence.Missing:
                reasons.Add(new ParseReason(ParseReasons.DestinationMissing, "The notification does not identify the destination account."));
                break;
            case DestinationEvidence.Invalid:
                reasons.Add(new ParseReason(ParseReasons.DestinationInvalid, "The destination account in the notification is not a valid account."));
                break;
            case DestinationEvidence.FullMismatch:
                reasons.Add(new ParseReason(ParseReasons.DestinationMismatch, "The notification names a different destination account."));
                break;
            case DestinationEvidence.MaskedInsufficient:
                reasons.Add(new ParseReason(ParseReasons.DestinationInsufficient,
                    "The masked destination account is not sufficient evidence for this connection."));
                break;
        }

        var candidate = new TransferCandidate
        {
            Amount = amount,
            Currency = currency,
            VariableSymbol = symbol,
            VariableSymbolKey = symbol is null ? null : PaymentRequestRules.VariableSymbolKey(symbol),
            BankReference = profile.HasStableReference ? TextLimits.Clean(raw.BankReference, MaxReferenceLength) : null,
            Destination = destination,
            SenderAccount = SenderAccount(raw.SenderAccount),
            SenderName = TextLimits.Clean(raw.SenderName, MaxNameLength),
            Message = TextLimits.Clean(raw.Message, MaxMessageLength),
            BookedOn = bookedOn,
            OccurredAt = occurredAt,
            Raw = raw,
        };
        return (candidate, reasons);
    }

    /// <summary>
    /// A review hint, never an identity: SHA-256 of profile, physical bank account (Feature 013,
    /// version <c>v2</c>; <c>v1</c> used the connection), amount, currency, booking date, and the
    /// variable symbol key. Distinct real transfers can share it. The migration of Feature 013
    /// recomputes stored values with the same canonical JSON in SQL; keep both in step.
    /// </summary>
    public static string ComparisonFingerprint(string profile, Guid accountId, TransferCandidate candidate) =>
        ComparisonFingerprint(profile, accountId, candidate.Amount, candidate.Currency, candidate.BookedOn, candidate.VariableSymbolKey);

    public static string ComparisonFingerprint(
        string profile, Guid accountId, long? amount, string? currency, DateOnly? bookedOn, string? variableSymbolKey) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new object?[]
        {
            "v2", profile, accountId.ToString("D"), amount, currency, bookedOn?.ToString("yyyy-MM-dd"), variableSymbolKey,
        }))));

    private static DestinationEvidence CheckDestination(string? raw, IBankProfile profile, CzechBankAccount destination)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return DestinationEvidence.Missing;
        }

        if (raw.IndexOfAny(['*', 'x', 'X', '•', '…']) >= 0)
        {
            return profile.MaskedDestinationMatches(raw.Trim(), destination)
                ? DestinationEvidence.MaskedSufficient
                : DestinationEvidence.MaskedInsufficient;
        }

        var parsed = ParseAccount(raw);
        return parsed is null ? DestinationEvidence.Invalid
            : parsed.Iban == destination.Iban ? DestinationEvidence.FullMatch
            : DestinationEvidence.FullMismatch;
    }

    private static string? SenderAccount(string? raw) => ParseAccount(raw)?.AccountNumber;

    private static CzechBankAccount? ParseAccount(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var text = raw.Trim();
        var result = text.Contains('/') ? CzechBankAccount.ParseDomestic(text) : CzechBankAccount.ParseIban(text);
        return result.Account;
    }
}
