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
}

/// <summary>Refuses <see cref="SandboxOptions.Enabled"/> outside Development and Testing, for the API and the CLI alike.</summary>
public sealed class SandboxOptionsValidator(IHostEnvironment environment) : IValidateOptions<SandboxOptions>
{
    public ValidateOptionsResult Validate(string? name, SandboxOptions options) =>
        options.Enabled && !environment.IsDevelopment() && !environment.IsEnvironment("Testing")
            ? ValidateOptionsResult.Fail("Sandbox:Enabled is only allowed in Development and Testing; sandbox payments are never real.")
            : ValidateOptionsResult.Success;
}
