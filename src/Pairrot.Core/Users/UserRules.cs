using Pairrot.Core.Tenants;

namespace Pairrot.Core.Users;

public static class UserRules
{
    /// <summary>Validates a login email. Login emails are separate from tenant contact emails.</summary>
    public static string? ValidateLoginEmail(string? email)
    {
        var trimmed = email?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.Length > TenantRules.MaxEmailLength || !TenantRules.IsValidEmail(trimmed)
            ? "email is not a valid email address."
            : null;
    }
}
