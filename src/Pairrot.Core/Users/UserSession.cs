namespace Pairrot.Core.Users;

/// <summary>
/// Server-side record of a browser session. The cookie only identifies this row; PostgreSQL
/// decides whether the session is still valid.
/// </summary>
public class UserSession
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid? SelectedTenantId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastActivityAt { get; set; }
    public DateTimeOffset IdleExpiresAt { get; set; }
    public DateTimeOffset AbsoluteExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevocationReason { get; set; }

    public bool IsValidAt(DateTimeOffset now) => RevokedAt is null && now < IdleExpiresAt && now < AbsoluteExpiresAt;
}

public static class SessionRevocationReasons
{
    public const string Logout = "logout";
    public const string LogoutAll = "logout_all";
    public const string Replaced = "replaced";
    public const string PasswordChanged = "password_changed";
    public const string PasswordReset = "password_reset";
    public const string UserDisabled = "user_disabled";
}
