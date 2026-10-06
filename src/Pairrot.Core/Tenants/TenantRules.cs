using System.Net.Mail;

namespace Pairrot.Core.Tenants;

/// <summary>Input rules shared by the API and the administrative CLI.</summary>
public static class TenantRules
{
    public const int MaxExternalIdLength = 200;
    public const int MaxNameLength = 200;
    public const int MaxEmailLength = 254;
    public const int MaxLabelLength = 200;

    public static string? ValidateName(string? name) => name switch
    {
        null => "name is required.",
        _ when string.IsNullOrWhiteSpace(name) => "name must not be blank.",
        _ when name.Trim().Length > MaxNameLength => $"name must be at most {MaxNameLength} characters.",
        _ => null,
    };

    public static string? ValidateExternalId(string? externalId) => externalId switch
    {
        null => "external_id is required.",
        _ when string.IsNullOrWhiteSpace(externalId) => "external_id must not be blank.",
        _ when externalId.Trim().Length > MaxExternalIdLength => $"external_id must be at most {MaxExternalIdLength} characters.",
        _ => null,
    };

    public static string? ValidateLabel(string? label) =>
        label is not null && label.Trim().Length > MaxLabelLength
            ? $"label must be at most {MaxLabelLength} characters."
            : null;

    /// <summary>Validates an optional email. <c>null</c> means "not set" and is valid.</summary>
    public static string? ValidateContactEmail(string? email)
    {
        if (email is null)
        {
            return null;
        }

        var trimmed = email.Trim();
        if (trimmed.Length == 0 || trimmed.Length > MaxEmailLength || !IsEmail(trimmed))
        {
            return "contact_email is not a valid email address.";
        }

        return null;
    }

    private static bool IsEmail(string value)
    {
        if (value.Any(char.IsWhiteSpace) || value.Count(c => c == '@') != 1)
        {
            return false;
        }

        var at = value.IndexOf('@');
        var domain = value[(at + 1)..];
        if (at == 0 || !domain.Contains('.') || domain.StartsWith('.') || domain.EndsWith('.'))
        {
            return false;
        }

        // MailAddress also accepts display-name forms ("A <a@b.c>"), so require an exact round trip.
        return MailAddress.TryCreate(value, out var address) && address.Address == value;
    }
}
