using Pairrot.Core.ApiKeys;

namespace Pairrot.UnitTests;

public class ApiKeySecretTests
{
    [Fact]
    public void Generated_key_parses_and_verifies()
    {
        var keyId = Guid.CreateVersion7();
        var (key, hash) = ApiKeySecret.Generate(keyId);

        Assert.True(ApiKeySecret.TryParse(key, out var parsedId, out var secret));
        Assert.Equal(keyId, parsedId);
        Assert.True(ApiKeySecret.Verify(secret, hash));
        Assert.Equal(32, hash.Length);
    }

    [Fact]
    public void Generated_secrets_are_unique_and_hash_is_not_the_secret()
    {
        var keyId = Guid.CreateVersion7();
        var (first, firstHash) = ApiKeySecret.Generate(keyId);
        var (second, _) = ApiKeySecret.Generate(keyId);

        Assert.NotEqual(first, second);
        ApiKeySecret.TryParse(first, out _, out var secret);
        Assert.NotEqual(System.Text.Encoding.UTF8.GetBytes(secret!), firstHash);
    }

    [Fact]
    public void Tampered_secret_does_not_verify()
    {
        var (key, hash) = ApiKeySecret.Generate(Guid.CreateVersion7());
        ApiKeySecret.TryParse(key, out _, out var secret);

        Assert.False(ApiKeySecret.Verify(secret + "x", hash));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("prt_")]
    [InlineData("prt_not-a-guid_secret")]
    [InlineData("xyz_01a112649dcb7538bb1ede1dec72c96d_secret")]
    [InlineData("prt_01a112649dcb7538bb1ede1dec72c96d_")]
    [InlineData("prt_01a112649dcb7538bb1ede1dec72c96dsecret")]
    public void Malformed_keys_are_rejected(string? key) =>
        Assert.False(ApiKeySecret.TryParse(key, out _, out _));
}
