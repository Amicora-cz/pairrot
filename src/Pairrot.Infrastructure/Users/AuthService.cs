using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pairrot.Core.Tenants;
using Pairrot.Core.Users;
using Pairrot.Infrastructure.Persistence;
using Pairrot.Infrastructure.Tenants;

namespace Pairrot.Infrastructure.Users;

/// <summary>
/// A session that is valid right now. <see cref="Tenant"/> is the selected tenant only while the
/// user still has an active membership in it and it is not deleted.
/// </summary>
public sealed record ValidatedSession(UserSession Session, string Email, Tenant? Tenant, bool Blocked);

/// <summary>An active membership. <see cref="Blocked"/> means the tenant or its parent is not active.</summary>
public sealed record MembershipView(TenantMembership Membership, Tenant Tenant, bool Blocked);

public sealed record SessionSummary(ValidatedSession Current, IReadOnlyList<MembershipView> Memberships);

/// <summary>
/// Password sign-in and server-side sessions. Every request re-validates the session, the user,
/// and the selected tenant membership against PostgreSQL.
/// </summary>
public class AuthService(
    PairrotDbContext db,
    UserManager<PairrotUser> users,
    TenantService tenants,
    IOptions<PairrotAuthOptions> options,
    ILogger<AuthService> logger)
{
    // Used to spend comparable time hashing when the email is unknown.
    private static readonly PairrotUser TimingUser = new() { Id = Guid.Empty };

    private PairrotAuthOptions Options => options.Value;

    /// <summary>
    /// Verifies credentials and creates a new session. Every failure returns the same generic error.
    /// An existing session of this browser is revoked, so an anonymous or older identifier is never reused.
    /// </summary>
    public async Task<ServiceResult<UserSession>> LoginAsync(
        string email, string password, Guid? previousSessionId, CancellationToken ct = default)
    {
        var user = await users.FindByEmailAsync(email.Trim());
        if (user is null)
        {
            users.PasswordHasher.HashPassword(TimingUser, password);
            logger.LogInformation("Login failed: unknown email.");
            return InvalidCredentials();
        }

        if (user.IsDisabled)
        {
            logger.LogInformation("Login failed for user {UserId}: user is disabled.", user.Id);
            return InvalidCredentials();
        }

        if (await users.IsLockedOutAsync(user))
        {
            logger.LogWarning("Login failed for user {UserId}: user is locked out.", user.Id);
            return InvalidCredentials();
        }

        if (!await users.CheckPasswordAsync(user, password))
        {
            await users.AccessFailedAsync(user);
            logger.LogInformation("Login failed for user {UserId}: wrong password.", user.Id);
            return InvalidCredentials();
        }

        await users.ResetAccessFailedCountAsync(user);

        var now = tenants.Now();
        if (previousSessionId is { } previous)
        {
            await RevokeSessionsAsync(db.Sessions.Where(s => s.Id == previous), SessionRevocationReasons.Replaced, now, ct);
        }

        // With exactly one accessible tenant, select it automatically.
        var accessible = await ActiveMemberships(user.Id).Select(m => m.TenantId).Take(2).ToListAsync(ct);
        var absolute = now + Options.SessionAbsoluteTimeout;
        var session = new UserSession
        {
            Id = Guid.CreateVersion7(),
            UserId = user.Id,
            SelectedTenantId = accessible.Count == 1 ? accessible[0] : null,
            CreatedAt = now,
            LastActivityAt = now,
            AbsoluteExpiresAt = absolute,
            IdleExpiresAt = Min(now + Options.SessionIdleTimeout, absolute),
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("User {UserId} signed in with session {SessionId}.", user.Id, session.Id);
        return session;
    }

    /// <summary>
    /// Returns the session if it is valid now, otherwise <c>null</c>. When <paramref name="recordActivity"/>
    /// is set, the idle expiry moves forward (never past the absolute expiry).
    /// </summary>
    public async Task<ValidatedSession?> ValidateSessionAsync(
        Guid sessionId, Guid userId, bool recordActivity, CancellationToken ct = default)
    {
        var now = tenants.Now();
        var row = await db.Sessions.AsNoTracking()
            .Where(s => s.Id == sessionId && s.UserId == userId)
            .Join(db.Users, s => s.UserId, u => u.Id, (s, u) => new { Session = s, u.Email, u.DisabledAt })
            .SingleOrDefaultAsync(ct);

        if (row is null || row.DisabledAt is not null || !row.Session.IsValidAt(now))
        {
            return null;
        }

        var session = row.Session;
        if (recordActivity)
        {
            var idle = Min(now + Options.SessionIdleTimeout, session.AbsoluteExpiresAt);
            await db.Sessions
                .Where(s => s.Id == sessionId && s.RevokedAt == null)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.LastActivityAt, now).SetProperty(s => s.IdleExpiresAt, idle), ct);
            session.LastActivityAt = now;
            session.IdleExpiresAt = idle;
        }

        Tenant? tenant = null;
        if (session.SelectedTenantId is { } selected)
        {
            tenant = await db.Tenants.AsNoTracking()
                .Include(t => t.ParentTenant)
                .Where(t => t.Id == selected && t.Status != TenantStatus.Deleted)
                .Where(t => db.Memberships.Any(m => m.TenantId == t.Id && m.UserId == userId && m.RemovedAt == null))
                .SingleOrDefaultAsync(ct);
        }

        return new ValidatedSession(session, row.Email!, tenant, tenant is not null && IsBlocked(tenant));
    }

    public async Task<SessionSummary> GetSummaryAsync(ValidatedSession current, CancellationToken ct = default)
    {
        var memberships = await ActiveMemberships(current.Session.UserId)
            .AsNoTracking()
            .Include(m => m.Tenant).ThenInclude(t => t!.ParentTenant)
            .OrderBy(m => m.Tenant!.Name).ThenBy(m => m.TenantId)
            .ToListAsync(ct);

        return new SessionSummary(current, memberships.Select(m => new MembershipView(m, m.Tenant!, IsBlocked(m.Tenant!))).ToList());
    }

    /// <summary>Selects a tenant for the session. Only an active membership in a non-deleted tenant qualifies.</summary>
    public async Task<ServiceResult<bool>> SelectTenantAsync(
        Guid sessionId, Guid userId, Guid tenantId, CancellationToken ct = default)
    {
        if (!await ActiveMemberships(userId).AnyAsync(m => m.TenantId == tenantId, ct))
        {
            return ServiceError.NotFound();
        }

        await db.Sessions
            .Where(s => s.Id == sessionId && s.UserId == userId && s.RevokedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.SelectedTenantId, tenantId), ct);
        logger.LogInformation("Session {SessionId} selected tenant {TenantId}.", sessionId, tenantId);
        return true;
    }

    public async Task LogoutAsync(Guid sessionId, CancellationToken ct = default)
    {
        await RevokeSessionsAsync(db.Sessions.Where(s => s.Id == sessionId), SessionRevocationReasons.Logout, tenants.Now(), ct);
        logger.LogInformation("Session {SessionId} signed out.", sessionId);
    }

    public async Task LogoutEverywhereAsync(Guid userId, CancellationToken ct = default)
    {
        var count = await RevokeAllSessionsAsync(userId, SessionRevocationReasons.LogoutAll, ct);
        logger.LogInformation("User {UserId} signed out everywhere ({Count} sessions).", userId, count);
    }

    /// <summary>Verifies the current password, stores the new one, and revokes every session of the user.</summary>
    public async Task<ServiceResult<bool>> ChangePasswordAsync(
        Guid userId, string currentPassword, string newPassword, CancellationToken ct = default)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null || user.IsDisabled)
        {
            return ServiceError.NotFound();
        }

        if (await users.IsLockedOutAsync(user) || !await users.CheckPasswordAsync(user, currentPassword))
        {
            await users.AccessFailedAsync(user);
            logger.LogInformation("Password change failed for user {UserId}: wrong current password.", userId);
            return new ServiceError(ServiceErrorKind.Validation, "invalid_current_password", "The current password is incorrect.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var result = await users.ChangePasswordAsync(user, currentPassword, newPassword);
        if (!result.Succeeded)
        {
            return ServiceError.Validation(Describe(result));
        }

        AddUserAudit(Actor.User(userId), userId, UserAuditActions.PasswordChanged, new { });
        await db.SaveChangesAsync(ct);
        var count = await RevokeAllSessionsAsync(userId, SessionRevocationReasons.PasswordChanged, ct);
        await transaction.CommitAsync(ct);

        logger.LogInformation("User {UserId} changed their password; {Count} sessions revoked.", userId, count);
        return true;
    }

    internal Task<int> RevokeAllSessionsAsync(Guid userId, string reason, CancellationToken ct) =>
        RevokeSessionsAsync(db.Sessions.Where(s => s.UserId == userId), reason, tenants.Now(), ct);

    internal void AddUserAudit(Actor actor, Guid targetUserId, string action, object details) =>
        db.UserAuditEvents.Add(new UserAuditEvent
        {
            Id = Guid.CreateVersion7(),
            TargetUserId = targetUserId,
            ActorUserId = actor.UserId,
            ActorDescription = actor.Description,
            Action = action,
            Details = JsonSerializer.Serialize(details, TenantService.AuditJson),
            CreatedAt = tenants.Now(),
        });

    internal static string Describe(IdentityResult result) =>
        string.Join(" ", result.Errors.Select(e => e.Description));

    private IQueryable<TenantMembership> ActiveMemberships(Guid userId) =>
        db.Memberships.Where(m => m.UserId == userId && m.RemovedAt == null && m.Tenant!.Status != TenantStatus.Deleted);

    private static Task<int> RevokeSessionsAsync(
        IQueryable<UserSession> sessions, string reason, DateTimeOffset now, CancellationToken ct) =>
        sessions
            .Where(s => s.RevokedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.RevokedAt, now).SetProperty(s => s.RevocationReason, reason), ct);

    private static bool IsBlocked(Tenant tenant) =>
        tenant.Status != TenantStatus.Active || (tenant.ParentTenant is { } parent && parent.Status != TenantStatus.Active);

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private static ServiceError InvalidCredentials() =>
        new(ServiceErrorKind.Unauthorized, "invalid_credentials", "The email or password is incorrect.");
}
