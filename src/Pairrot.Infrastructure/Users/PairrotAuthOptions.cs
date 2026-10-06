namespace Pairrot.Infrastructure.Users;

/// <summary>Application configuration for browser sign-in (section <c>Auth</c>). Not a tenant setting.</summary>
public sealed class PairrotAuthOptions
{
    public const string Section = "Auth";

    /// <summary>A session expires after this long without authenticated user activity.</summary>
    public TimeSpan SessionIdleTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>A session expires this long after login, even if the user stays active.</summary>
    public TimeSpan SessionAbsoluteTimeout { get; set; } = TimeSpan.FromHours(8);

    /// <summary>Failed password attempts before the account is temporarily locked.</summary>
    public int LockoutMaxFailedAttempts { get; set; } = 5;

    public TimeSpan LockoutDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Login requests allowed per client IP address within <see cref="LoginRateLimitWindow"/>.</summary>
    public int LoginRateLimitPermits { get; set; } = 10;

    public TimeSpan LoginRateLimitWindow { get; set; } = TimeSpan.FromMinutes(1);

    public int PasswordMinLength { get; set; } = 12;
}
