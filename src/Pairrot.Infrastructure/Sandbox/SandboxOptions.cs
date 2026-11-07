using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Pairrot.Infrastructure.Sandbox;

/// <summary>Feature 021 settings (section <c>Sandbox</c>).</summary>
public sealed class SandboxOptions
{
    public const string Section = "Sandbox";

    /// <summary>
    /// Development and Test only: lets a developer simulate incoming payments over the API. Every
    /// simulated payment is test data and is matched with test evidence. Refused elsewhere.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// With <see cref="Enabled"/>: record simulated payments as <c>live</c> instead of <c>test</c>, so
    /// they pay requests with live evidence and reach live webhook endpoints. For an integrating
    /// application whose receiver accepts live events only and should run its production code path
    /// unchanged against a local sandbox. Safe only because sandbox mode never runs outside
    /// Development and Testing, so this instance holds no real money.
    /// </summary>
    public bool EmitAsLive { get; set; }
}

/// <summary>Refuses <see cref="SandboxOptions.Enabled"/> outside Development and Testing, for the API and the CLI alike.</summary>
public sealed class SandboxOptionsValidator(IHostEnvironment environment) : IValidateOptions<SandboxOptions>
{
    public ValidateOptionsResult Validate(string? name, SandboxOptions options)
    {
        if (options.Enabled && !environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
        {
            return ValidateOptionsResult.Fail("Sandbox:Enabled is only allowed in Development and Testing; sandbox payments are never real.");
        }

        return options.EmitAsLive && !options.Enabled
            ? ValidateOptionsResult.Fail("Sandbox:EmitAsLive requires Sandbox:Enabled.")
            : ValidateOptionsResult.Success;
    }
}
