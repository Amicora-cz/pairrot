using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Pairrot.Core.Inbound;

namespace Pairrot.Infrastructure.Inbound;

/// <summary>
/// The provider-instance registry: <c>InboundEmail:Instances:{id}</c> with common settings below
/// plus any settings of the instance's adapter kind. Instance IDs are stored on addresses,
/// messages, and receipts; never reuse one for another provider account.
/// </summary>
public sealed partial class InboundEmailOptions
{
    public const string Section = "InboundEmail";
    public const string StubKind = "stub";

    /// <summary>The instance new addresses are assigned on. Changing it never touches existing addresses.</summary>
    public string DefaultInstance { get; set; } = StubKind;

    public Dictionary<string, InboundInstanceOptions> Instances { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Pairrot's limit for one common content snapshot, in both delivery modes (default 5 MiB).</summary>
    public int MaxContentBytes { get; set; } = 5 * 1024 * 1024;

    // RFC 2606 / RFC 6761 names that can never be real customer domains.
    private static readonly string[] ReservedSuffixes = [".test", ".example", ".invalid", ".localhost"];

    public static bool IsReservedTestDomain(string domain) =>
        IsDomain(domain) && ReservedSuffixes.Any(s => domain.EndsWith(s, StringComparison.Ordinal) && domain.Length > s.Length);

    /// <summary>A lowercase DNS name with at least two labels.</summary>
    public static bool IsDomain(string domain) => DomainPattern().IsMatch(domain);

    public static bool IsInstanceId(string id) => InstancePattern().IsMatch(id);

    /// <summary>
    /// The built-in stub instance, added when the default names it but configuration does not
    /// (e.g. tools that configure only additional instances).
    /// </summary>
    public static void AddBuiltInStub(InboundEmailOptions options)
    {
        if (options.DefaultInstance == StubKind && !options.Instances.ContainsKey(StubKind))
        {
            options.Instances[StubKind] = new InboundInstanceOptions { Kind = StubKind, Environment = "test", Domain = "in.pairrot.test" };
        }
    }

    [GeneratedRegex("^(?=.{3,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\\.)+[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex DomainPattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,49}$")]
    private static partial Regex InstancePattern();
}

/// <summary>Settings every provider instance has, whatever its adapter kind.</summary>
public sealed class InboundInstanceOptions
{
    /// <summary>The adapter kind, e.g. <c>stub</c> or <c>resend</c>.</summary>
    public string Kind { get; set; } = "";

    /// <summary><c>test</c> or <c>live</c>.</summary>
    public string Environment { get; set; } = "test";

    /// <summary><c>received</c>, or <c>fixture</c> for a test harness whose messages must never pass for real reception.</summary>
    public string SourceKind { get; set; } = "received";

    /// <summary>The domain new addresses of this instance are generated under.</summary>
    public string Domain { get; set; } = "";

    /// <summary>A disabled instance accepts nothing new but keeps its identity and history.</summary>
    public bool Enabled { get; set; } = true;
}

/// <param name="Settings">The instance's configuration section, from which its adapter binds its own settings.</param>
public sealed record InboundProviderInstance(
    string Id,
    string Kind,
    InboundEnvironment Environment,
    InboundSourceKind SourceKind,
    string Domain,
    bool Enabled,
    IConfigurationSection Settings);

/// <summary>The configured provider instances. Adapters and workers always resolve the instance a record is bound to.</summary>
public sealed class InboundProviderRegistry
{
    private readonly Dictionary<string, InboundProviderInstance> _instances;

    public InboundProviderRegistry(IOptions<InboundEmailOptions> options, IConfiguration configuration)
    {
        var value = options.Value;
        MaxContentBytes = value.MaxContentBytes;
        _instances = value.Instances.ToDictionary(p => p.Key, p => new InboundProviderInstance(
            p.Key,
            p.Value.Kind,
            p.Value.Environment == "live" ? InboundEnvironment.Live : InboundEnvironment.Test,
            p.Value.SourceKind == "fixture" ? InboundSourceKind.Fixture : InboundSourceKind.Received,
            p.Value.Domain,
            p.Value.Enabled,
            configuration.GetSection($"{InboundEmailOptions.Section}:Instances:{p.Key}")));
        DefaultInstanceId = value.DefaultInstance;
    }

    public string DefaultInstanceId { get; }
    public int MaxContentBytes { get; }
    public IReadOnlyCollection<InboundProviderInstance> All => _instances.Values;

    /// <summary>The instance for new addresses; startup validation guarantees it exists and is enabled.</summary>
    public InboundProviderInstance Default => _instances[DefaultInstanceId];

    public InboundProviderInstance? Find(string id) => _instances.GetValueOrDefault(id);
}

/// <summary>Reserves nothing itself: Pairrot owns the address and its owner. Handles any provider-side setup for its kind.</summary>
public interface IInboundAddressProvisioner
{
    string Kind { get; }

    /// <summary>Configuration problems of an instance of this kind; empty when it can run.</summary>
    IEnumerable<string> Validate(InboundProviderInstance instance);

    /// <summary>Whether <see cref="EnsureAddressAsync"/> must contact the provider. If not, a new address is usable at once.</summary>
    bool RequiresRemoteSetup { get; }

    /// <summary>Completes provider-side setup of an already reserved address. Safe to repeat for the same reservation.</summary>
    Task<ProvisioningResult> EnsureAddressAsync(InboundProviderInstance instance, string address, CancellationToken ct);
}

public sealed record ProvisioningResult(bool Ready, string? Error = null);

/// <summary>Retrieves deferred content for its kind and translates it into the common snapshot.</summary>
public interface IInboundContentSource
{
    string Kind { get; }
    Task<ContentRetrieval> RetrieveAsync(InboundProviderInstance instance, string reference, CancellationToken ct);
}

/// <summary>Normalized outcomes, so workers never see provider exceptions or HTTP responses.</summary>
public enum ContentRetrievalOutcome
{
    Retrieved,

    /// <summary>Timeout, rate limit, or provider outage; <see cref="ContentRetrieval.RetryAfter"/> may say how long to wait.</summary>
    TemporaryFailure,

    /// <summary>Possibly not retrievable yet; retried before being classified as unavailable.</summary>
    NotYetAvailable,

    /// <summary>The provider says the content is gone.</summary>
    Unavailable,

    /// <summary>Credentials or configuration are wrong; retrying will not help until they are fixed.</summary>
    ConfigurationError,

    /// <summary>Larger than the adapter's or Pairrot's limit. Never truncated.</summary>
    TooLarge,

    /// <summary>The provider answered with something that is not the requested email.</summary>
    Invalid,
}

/// <param name="Error">A safe description: never a response body, credential, or signed URL.</param>
public sealed record ContentRetrieval(
    ContentRetrievalOutcome Outcome, InboundContentSnapshot? Snapshot = null, string? Error = null, TimeSpan? RetryAfter = null);

/// <summary>Reports every configuration problem of every instance at startup.</summary>
public sealed class InboundEmailOptionsValidator(IEnumerable<IInboundAddressProvisioner> provisioners, IConfiguration configuration)
    : IValidateOptions<InboundEmailOptions>
{
    public ValidateOptionsResult Validate(string? name, InboundEmailOptions options)
    {
        var errors = new List<string>();
        var kinds = provisioners.ToDictionary(p => p.Kind);
        var registry = new InboundProviderRegistry(Options.Create(options), configuration);
        if (registry.Find(options.DefaultInstance) is not { Enabled: true })
        {
            errors.Add($"{InboundEmailOptions.Section}:DefaultInstance must name an enabled instance.");
        }

        if (options.MaxContentBytes < 1024)
        {
            errors.Add($"{InboundEmailOptions.Section}:MaxContentBytes must be at least 1024.");
        }

        foreach (var (id, settings) in options.Instances)
        {
            var prefix = $"{InboundEmailOptions.Section}:Instances:{id}";
            if (!InboundEmailOptions.IsInstanceId(id))
            {
                errors.Add($"{prefix}: instance IDs are 1-50 lowercase letters, digits, '-' or '_'.");
            }

            if (settings.Environment is not ("test" or "live"))
            {
                errors.Add($"{prefix}:Environment must be 'test' or 'live'.");
            }

            if (settings.SourceKind is not ("received" or "fixture"))
            {
                errors.Add($"{prefix}:SourceKind must be 'received' or 'fixture'.");
            }

            if (settings.Environment == "live" && !(InboundEmailOptions.IsDomain(settings.Domain) && !InboundEmailOptions.IsReservedTestDomain(settings.Domain)))
            {
                errors.Add($"{prefix}:Domain must be the instance's real receiving domain (not a reserved test domain).");
            }
            else if (settings.Environment == "test" && !InboundEmailOptions.IsDomain(settings.Domain))
            {
                errors.Add($"{prefix}:Domain must be a lowercase domain such as in.pairrot.test.");
            }

            if (!kinds.TryGetValue(settings.Kind, out var provisioner))
            {
                errors.Add($"{prefix}:Kind '{settings.Kind}' has no registered adapter (known: {string.Join(", ", kinds.Keys)}).");
                continue;
            }

            errors.AddRange(provisioner.Validate(registry.Find(id)!).Select(e => $"{prefix}: {e}"));
        }

        return errors.Count > 0 ? ValidateOptionsResult.Fail(errors) : ValidateOptionsResult.Success;
    }
}

/// <summary>
/// Local test addresses on a reserved test domain. There is no inbox and no network call: nothing
/// is ever delivered to them.
/// </summary>
public sealed class StubAddressProvisioner : IInboundAddressProvisioner
{
    public string Kind => InboundEmailOptions.StubKind;
    public bool RequiresRemoteSetup => false;

    public IEnumerable<string> Validate(InboundProviderInstance instance)
    {
        if (instance.Environment != InboundEnvironment.Test || !InboundEmailOptions.IsReservedTestDomain(instance.Domain))
        {
            yield return "a stub instance must be a test instance on a reserved test domain such as in.pairrot.test.";
        }
    }

    public Task<ProvisioningResult> EnsureAddressAsync(InboundProviderInstance instance, string address, CancellationToken ct) =>
        Task.FromResult(new ProvisioningResult(true));
}

internal static class RandomAddress
{
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

    // 24 base32 characters = 120 random bits; nothing about the tenant, user, or account.
    public static string Create(string domain) => $"p_{RandomNumberGenerator.GetString(Alphabet, 24)}@{domain}";
}
