using Microsoft.AspNetCore.Identity;

namespace Pairrot.Infrastructure.Users;

/// <summary>
/// A person who signs in with an email and password. ASP.NET Core Identity manages the password
/// hash, lockout, and security stamp; the login email is also the Identity user name.
/// </summary>
public class PairrotUser : IdentityUser<Guid>
{
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DisabledAt { get; set; }

    public bool IsDisabled => DisabledAt is not null;
}
