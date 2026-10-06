namespace Pairrot.Core.Users;

/// <summary>Changes to a user account that are not tied to one tenant (password, disabled state).</summary>
public class UserAuditEvent
{
    public Guid Id { get; set; }
    public Guid TargetUserId { get; set; }
    public Guid? ActorUserId { get; set; }
    public string? ActorDescription { get; set; }
    public required string Action { get; set; }

    /// <summary>JSON object with change details. Never contains passwords or tokens.</summary>
    public string Details { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; }
}

public static class UserAuditActions
{
    public const string UserCreated = "user.created";
    public const string UserDisabled = "user.disabled";
    public const string UserEnabled = "user.enabled";
    public const string PasswordChanged = "user.password_changed";
    public const string PasswordReset = "user.password_reset";
}
