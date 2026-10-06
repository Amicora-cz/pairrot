using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace Pairrot.Core.ApiKeys;

/// <summary>
/// API key format: <c>prt_{keyId:N}_{secret}</c>. The key ID is public and used for lookup;
/// the secret is 32 random bytes (base64url). Only SHA-256 of the secret is persisted.
/// </summary>
public static class ApiKeySecret
{
    private const string Prefix = "prt_";
    private const int SecretBytes = 32;

    public static (string Key, byte[] SecretHash) Generate(Guid keyId)
    {
        var secret = Base64UrlEncode(RandomNumberGenerator.GetBytes(SecretBytes));
        return ($"{Prefix}{keyId:N}_{secret}", Hash(secret));
    }

    public static bool TryParse(string? key, out Guid keyId, [NotNullWhen(true)] out string? secret)
    {
        keyId = Guid.Empty;
        secret = null;

        if (key is null || !key.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = key.AsSpan(Prefix.Length);
        var separator = rest.IndexOf('_');
        if (separator != 32 || !Guid.TryParseExact(rest[..separator], "N", out keyId))
        {
            return false;
        }

        var candidate = rest[(separator + 1)..].ToString();
        if (candidate.Length == 0 || candidate.Length > 128)
        {
            return false;
        }

        secret = candidate;
        return true;
    }

    public static bool Verify(string secret, byte[] expectedHash) =>
        CryptographicOperations.FixedTimeEquals(Hash(secret), expectedHash);

    private static byte[] Hash(string secret) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
