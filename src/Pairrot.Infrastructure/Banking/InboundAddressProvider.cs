using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace Pairrot.Infrastructure.Banking;

/// <summary>
/// Supplies notification addresses for bank connections. The address is persisted by
/// <see cref="BankConnectionService"/>; a provider never holds the only copy.
/// </summary>
public interface IInboundAddressProvider
{
    /// <summary>Stored with each assigned address and reported to clients, e.g. <c>stub</c>.</summary>
    string Name { get; }

    /// <summary>A new, opaque, unused-looking address. Uniqueness is enforced by the database.</summary>
    string CreateAddress();
}

public sealed class InboundEmailOptions
{
    public const string Section = "InboundEmail";
    public const string StubProvider = "stub";

    /// <summary>Only <c>stub</c> exists until real email reception is implemented.</summary>
    public string Provider { get; set; } = "";

    /// <summary>Domain of generated test addresses. Must be a reserved test domain, never a real one.</summary>
    public string Domain { get; set; } = "";

    // RFC 2606 / RFC 6761 names that can never be real customer domains.
    private static readonly string[] ReservedSuffixes = [".test", ".example", ".invalid", ".localhost"];

    public static bool IsReservedTestDomain(string domain) =>
        domain.Length > 0
        && domain == domain.Trim().ToLowerInvariant()
        && !domain.Contains('@')
        && ReservedSuffixes.Any(s => domain.EndsWith(s, StringComparison.Ordinal) && domain.Length > s.Length);
}

/// <summary>
/// Local substitute for the future Resend integration. It only invents addresses: there is no
/// inbox, no delivery, no webhook, and no network call.
/// </summary>
public sealed class StubInboundAddressProvider(IOptions<InboundEmailOptions> options) : IInboundAddressProvider
{
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

    public string Name => InboundEmailOptions.StubProvider;

    // 24 base32 characters = 120 random bits; nothing about the tenant, user, or account.
    public string CreateAddress() => $"p_{RandomNumberGenerator.GetString(Alphabet, 24)}@{options.Value.Domain}";
}
