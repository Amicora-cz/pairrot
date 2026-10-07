using System.Globalization;
using System.Text.Json;
using Pairrot.Core.Inbound;

namespace Pairrot.Infrastructure.Inbound.Resend;

/// <summary>A verified Resend webhook event. Only <c>email.received</c> carries an <see cref="Email"/>.</summary>
public sealed record ResendEvent(string Type, ResendReceivedEmail? Email);

/// <param name="Data">The event's <c>data</c> object, as received.</param>
public sealed record ResendReceivedEmail(
    string EmailId, List<string> Recipients, string? From, string? Subject, DateTimeOffset? CreatedAt, string Data);

/// <param name="Raw">The list item as received.</param>
public sealed record ResendListedEmail(string Id, List<string> Recipients, string? From, string? Subject, DateTimeOffset? CreatedAt, string Raw);

public sealed record ResendListPage(IReadOnlyList<ResendListedEmail> Items, bool HasMore);

/// <summary>
/// Resend's JSON shapes, mapped into the common contract. Nothing outside this adapter sees them.
/// Signatures are verified on the raw body before anything here runs.
/// </summary>
public static class ResendPayloads
{
    public const string EmailReceived = "email.received";
    public const string RecipientEvidence = "resend:to,cc,bcc";
    public const string AuthenticationOrigin = "resend_receiving_api";

    /// <summary>The event, or <c>null</c> when required data is missing or malformed.</summary>
    public static ResendEvent? ParseEvent(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(type.GetString()) || type.GetString()!.Length > 100)
            {
                return null;
            }

            if (type.GetString() != EmailReceived)
            {
                return new ResendEvent(type.GetString()!, null);
            }

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("email_id", out var emailId) || emailId.ValueKind != JsonValueKind.String
                || emailId.GetString() is not { Length: > 0 and <= InboundIntake.MaxIdentityLength } id
                || !TryReadAddresses(data, out var recipients)
                || !TryGetOptionalString(data, "from", out var from)
                || !TryGetOptionalString(data, "subject", out var subject)
                || !TryGetOptionalString(data, "created_at", out var createdAtText))
            {
                return null;
            }

            DateTimeOffset? createdAt = DateTimeOffset.TryParse(
                createdAtText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed.ToUniversalTime()
                : null;
            return new ResendEvent(EmailReceived, new ResendReceivedEmail(id, recipients, from, subject, createdAt, data.GetRawText()));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A page of <c>GET /emails/receiving</c>, or <c>null</c> when it is malformed.</summary>
    public static ResendListPage? ParseList(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array
                || !root.TryGetProperty("has_more", out var hasMore) || hasMore.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return null;
            }

            var items = new List<ResendListedEmail>();
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
                    || id.GetString() is not { Length: > 0 and <= InboundIntake.MaxIdentityLength } emailId
                    || !TryReadAddresses(item, out var recipients)
                    || !TryGetOptionalString(item, "from", out var from)
                    || !TryGetOptionalString(item, "subject", out var subject)
                    || !TryGetOptionalString(item, "created_at", out var createdAtText))
                {
                    return null;
                }

                DateTimeOffset? createdAt = DateTimeOffset.TryParse(
                    createdAtText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                    ? parsed.ToUniversalTime()
                    : null;
                items.Add(new ResendListedEmail(emailId, recipients, from, subject, createdAt, item.GetRawText()));
            }

            return new ResendListPage(items, hasMore.GetBoolean());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The common snapshot of a Receiving API response for <paramref name="expectedEmailId"/>, or
    /// <c>null</c> when the response is not that email.
    /// </summary>
    public static InboundContentSnapshot? ParseContent(string body, string expectedEmailId)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() != expectedEmailId
                || !TryReadAddresses(root, out var recipients)
                || !TryGetOptionalString(root, "text", out var text)
                || !TryGetOptionalString(root, "html", out var html))
            {
                return null;
            }

            return new InboundContentSnapshot(
                text,
                html,
                RawOf(root, "headers", JsonValueKind.Object),
                recipients.Count > 0 ? recipients : null,
                RawOf(root, "attachments", JsonValueKind.Array),
                Authentication(root),
                body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Resend's provider-computed results; absent or unrecognized values stay <c>unknown</c>.</summary>
    private static SenderAuthentication Authentication(JsonElement root)
    {
        if (!root.TryGetProperty("authentication", out var results) || results.ValueKind != JsonValueKind.Object)
        {
            return SenderAuthentication.Unknown;
        }

        string Result(string name) => results.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? AuthenticationResults.Normalize(value.GetString())
            : AuthenticationResults.Unknown;

        return new SenderAuthentication(Result("spf"), Result("dkim"), Result("dmarc"), AuthenticationOrigin, results.GetRawText());
    }

    /// <summary>
    /// The normalized <c>to</c>, <c>cc</c>, and <c>bcc</c> addresses; <c>false</c> only for malformed lists.
    /// An empty result is missing recipient evidence, which intake quarantines.
    /// </summary>
    private static bool TryReadAddresses(JsonElement container, out List<string> addresses)
    {
        var values = new List<string?>();
        addresses = [];
        foreach (var name in new[] { "to", "cc", "bcc" })
        {
            if (!container.TryGetProperty(name, out var list) || list.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            if (list.ValueKind == JsonValueKind.String)
            {
                values.Add(list.GetString());
                continue;
            }

            if (list.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                values.Add(item.GetString());
            }
        }

        addresses = EmailAddresses.NormalizeAll(values);
        return true;
    }

    private static bool TryGetOptionalString(JsonElement container, string name, out string? value)
    {
        value = null;
        if (!container.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString();
        return true;
    }

    private static string? RawOf(JsonElement container, string name, JsonValueKind kind) =>
        container.TryGetProperty(name, out var element) && element.ValueKind == kind ? element.GetRawText() : null;
}
