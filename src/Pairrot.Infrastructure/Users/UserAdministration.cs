using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Pairrot.Core.Tenants;
using Pairrot.Core.Users;
using Pairrot.Infrastructure.Persistence;
using Pairrot.Infrastructure.Tenants;

namespace Pairrot.Infrastructure.Users;

public enum MembershipChange
{
    Added,
    Reactivated,
    Removed,
    Unchanged,
}

public sealed record UserListItem(PairrotUser User, IReadOnlyList<Tenant> Tenants);

/// <summary>
/// Administrative user operations. Not exposed over HTTP; used by Pairrot.Admin. There is no public
/// signup or invitation flow: users and memberships are created here.
/// </summary>
public class UserAdministration(
    PairrotDbContext db,
    UserManager<PairrotUser> users,
    TenantService tenants,
    AuthService auth)
{
    public Task<PairrotUser?> FindUserAsync(string email) => users.FindByEmailAsync(email.Trim());

    /// <summary>Creates a user. Fails with <c>user_exists</c> instead of changing an existing user.</summary>
    public async Task<ServiceResult<PairrotUser>> CreateUserAsync(
        string email, string password, string operatorDescription, CancellationToken ct = default)
    {
        if (UserRules.ValidateLoginEmail(email) is { } error)
        {
            return ServiceError.Validation(error);
        }

        email = email.Trim();
        if (await users.FindByEmailAsync(email) is not null)
        {
            return ServiceError.Conflict("user_exists", $"A user with the email '{email}' already exists.");
        }

        var user = new PairrotUser
        {
            Id = Guid.CreateVersion7(),
            UserName = email,
            Email = email,
            LockoutEnabled = true,
            CreatedAt = tenants.Now(),
        };

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var result = await users.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            return ServiceError.Validation(AuthService.Describe(result));
        }

        auth.AddUserAudit(Actor.Administrator(operatorDescription), user.Id, UserAuditActions.UserCreated, new { });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return user;
    }

    /// <summary>Gives the user an <c>admin</c> membership. Repeating it changes nothing.</summary>
    public async Task<ServiceResult<MembershipChange>> GrantAdminAsync(
        string email, string tenantReference, string operatorDescription, CancellationToken ct = default)
    {
        var (user, tenant, error) = await ResolveAsync(email, tenantReference, ct);
        if (error is not null)
        {
            return error;
        }

        if (tenant!.Status == TenantStatus.Deleted)
        {
            return TenantService.TenantDeleted(tenant, "The tenant is deleted.");
        }

        var now = tenants.Now();
        var membership = await db.Memberships.SingleOrDefaultAsync(m => m.UserId == user!.Id && m.TenantId == tenant.Id, ct);
        MembershipChange change;
        if (membership is null)
        {
            db.Memberships.Add(new TenantMembership
            {
                UserId = user!.Id,
                TenantId = tenant.Id,
                Role = MembershipRoles.Admin,
                CreatedAt = now,
                UpdatedAt = now,
            });
            change = MembershipChange.Added;
        }
        else if (!membership.IsActive)
        {
            membership.RemovedAt = null;
            membership.UpdatedAt = now;
            change = MembershipChange.Reactivated;
        }
        else
        {
            return MembershipChange.Unchanged;
        }

        tenants.AddAudit(Actor.Administrator(operatorDescription), tenant.Id, AuditActions.MembershipAdded,
            new { UserId = user!.Id, Role = MembershipRoles.Admin });
        await db.SaveChangesAsync(ct);
        return change;
    }

    /// <summary>Removes the membership. Access to the tenant ends with the user's next request.</summary>
    public async Task<ServiceResult<MembershipChange>> RemoveAdminAsync(
        string email, string tenantReference, string operatorDescription, CancellationToken ct = default)
    {
        var (user, tenant, error) = await ResolveAsync(email, tenantReference, ct);
        if (error is not null)
        {
            return error;
        }

        var membership = await db.Memberships.SingleOrDefaultAsync(m => m.UserId == user!.Id && m.TenantId == tenant!.Id, ct);
        if (membership is not { IsActive: true })
        {
            return MembershipChange.Unchanged;
        }

        var now = tenants.Now();
        membership.RemovedAt = now;
        membership.UpdatedAt = now;
        tenants.AddAudit(Actor.Administrator(operatorDescription), tenant!.Id, AuditActions.MembershipRemoved,
            new { UserId = user!.Id });
        await db.SaveChangesAsync(ct);
        return MembershipChange.Removed;
    }

    /// <summary>Disables the user and revokes all their sessions. Their tenants' API keys keep working.</summary>
    public async Task<ServiceResult<bool>> DisableAsync(string email, string operatorDescription, CancellationToken ct = default)
    {
        if (await users.FindByEmailAsync(email.Trim()) is not { } user)
        {
            return UserNotFound(email);
        }

        if (user.IsDisabled)
        {
            return false;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        user.DisabledAt = tenants.Now();
        auth.AddUserAudit(Actor.Administrator(operatorDescription), user.Id, UserAuditActions.UserDisabled, new { });
        await db.SaveChangesAsync(ct);
        await auth.RevokeAllSessionsAsync(user.Id, SessionRevocationReasons.UserDisabled, ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task<ServiceResult<bool>> EnableAsync(string email, string operatorDescription, CancellationToken ct = default)
    {
        if (await users.FindByEmailAsync(email.Trim()) is not { } user)
        {
            return UserNotFound(email);
        }

        if (!user.IsDisabled)
        {
            return false;
        }

        user.DisabledAt = null;
        auth.AddUserAudit(Actor.Administrator(operatorDescription), user.Id, UserAuditActions.UserEnabled, new { });
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Sets a new password, clears any lockout, and revokes all sessions of the user.</summary>
    public async Task<ServiceResult<bool>> ResetPasswordAsync(
        string email, string newPassword, string operatorDescription, CancellationToken ct = default)
    {
        if (await users.FindByEmailAsync(email.Trim()) is not { } user)
        {
            return UserNotFound(email);
        }

        foreach (var validator in users.PasswordValidators)
        {
            var validation = await validator.ValidateAsync(users, user, newPassword);
            if (!validation.Succeeded)
            {
                return ServiceError.Validation(AuthService.Describe(validation));
            }
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await users.RemovePasswordAsync(user);
        var result = await users.AddPasswordAsync(user, newPassword);
        if (!result.Succeeded)
        {
            return ServiceError.Validation(AuthService.Describe(result));
        }

        await users.SetLockoutEndDateAsync(user, null);
        await users.ResetAccessFailedCountAsync(user);
        auth.AddUserAudit(Actor.Administrator(operatorDescription), user.Id, UserAuditActions.PasswordReset, new { });
        await db.SaveChangesAsync(ct);
        await auth.RevokeAllSessionsAsync(user.Id, SessionRevocationReasons.PasswordReset, ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<UserListItem>> ListAsync(CancellationToken ct = default)
    {
        var all = await db.Users.AsNoTracking().OrderBy(u => u.NormalizedEmail).ToListAsync(ct);
        var memberships = await db.Memberships.AsNoTracking()
            .Include(m => m.Tenant).ThenInclude(t => t!.ParentTenant)
            .Where(m => m.RemovedAt == null)
            .ToListAsync(ct);

        return all
            .Select(u => new UserListItem(u, memberships.Where(m => m.UserId == u.Id).Select(m => m.Tenant!).ToList()))
            .ToList();
    }

    /// <summary>
    /// Finds a tenant by ID, by root external ID (<c>nalekci.cz</c>), or by
    /// <c>root/child</c> external IDs (<c>nalekci.cz/studio_123</c>).
    /// </summary>
    public async Task<Tenant?> FindTenantAsync(string reference, CancellationToken ct = default)
    {
        if (Guid.TryParse(reference, out var id))
        {
            return await db.Tenants.SingleOrDefaultAsync(t => t.Id == id, ct);
        }

        var parts = reference.Split('/', 2, StringSplitOptions.TrimEntries);
        var root = await db.Tenants.SingleOrDefaultAsync(t => t.ParentTenantId == null && t.ExternalId == parts[0], ct);
        if (root is null || parts.Length == 1)
        {
            return root;
        }

        return await db.Tenants.SingleOrDefaultAsync(t => t.ParentTenantId == root.Id && t.ExternalId == parts[1], ct);
    }

    private async Task<(PairrotUser? User, Tenant? Tenant, ServiceError? Error)> ResolveAsync(
        string email, string tenantReference, CancellationToken ct)
    {
        if (await users.FindByEmailAsync(email.Trim()) is not { } user)
        {
            return (null, null, UserNotFound(email));
        }

        if (await FindTenantAsync(tenantReference, ct) is not { } tenant)
        {
            return (null, null, ServiceError.NotFound($"Tenant '{tenantReference}' does not exist."));
        }

        return (user, tenant, null);
    }

    private static ServiceError UserNotFound(string email) => ServiceError.NotFound($"No user with the email '{email.Trim()}'.");
}
