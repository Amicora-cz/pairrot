using Pairrot.Core.Tenants;

namespace Pairrot.Core.Users;

/// <summary>
/// Gives a user access to one tenant. There is at most one row per user and tenant; removing
/// and re-adding access updates that row.
/// </summary>
public class TenantMembership
{
    public Guid UserId { get; set; }
    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }
    public string Role { get; set; } = MembershipRoles.Admin;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? RemovedAt { get; set; }

    public bool IsActive => RemovedAt is null;
}

public static class MembershipRoles
{
    public const string Admin = "admin";
}
