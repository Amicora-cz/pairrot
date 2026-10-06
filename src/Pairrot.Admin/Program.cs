using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Pairrot.Core.Tenants;
using Pairrot.Infrastructure;
using Pairrot.Infrastructure.Persistence;
using Pairrot.Infrastructure.Tenants;

// Administrative CLI. Root tenant setup and root key management are deliberately not HTTP endpoints.

const string Usage = """
    Usage: Pairrot.Admin <command> [options]

    Commands:
      migrate                                  Apply pending database migrations.
      setup [--name <n>] [--contact-email <e>] Apply migrations, then create the root tenant with
                                               one tenant key and one management key. Idempotent:
                                               an existing root is left unchanged and no keys are issued.
      keys list                                List the root tenant's keys (never secrets).
      keys issue --purpose <tenant|management> [--label <l>]
                                               Issue an additional root key; the secret is shown once.
      keys revoke <key-id>                     Revoke a root key.

    Common options:
      --root <external-id>   Root tenant (default: nalekci.cz)
      --operator <name>      Recorded in the audit log (default: current OS user)

    The database is taken from ConnectionStrings:Pairrot (env: ConnectionStrings__Pairrot).
    """;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine(Usage);
    return args.Length == 0 ? 1 : 0;
}

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Services.AddPairrotInfrastructure(builder.Configuration);

using var host = builder.Build();
using var scope = host.Services.CreateScope();
var services = scope.ServiceProvider;

var options = ParseOptions(args);
var root = options.GetValueOrDefault("root") ?? RootTenantAdministration.NaLekciExternalId;
var operatorName = options.GetValueOrDefault("operator") ?? $"admin-cli:{Environment.UserName}";
var admin = services.GetRequiredService<RootTenantAdministration>();

switch (args)
{
    case ["migrate", ..]:
        await MigrateAsync();
        return 0;

    case ["setup", ..]:
    {
        await MigrateAsync();
        var result = await admin.SetupAsync(
            root, options.GetValueOrDefault("name") ?? root, options.GetValueOrDefault("contact-email"), operatorName);
        if (result.Error is { } error)
        {
            return Fail(error);
        }

        var setup = result.Value!;
        if (!setup.Created)
        {
            Console.WriteLine($"Root tenant '{setup.Tenant.ExternalId}' already exists ({setup.Tenant.Id}). Nothing changed; no keys issued.");
            if (setup.ContactEmailIgnored)
            {
                Console.WriteLine("The supplied --contact-email was ignored; change it through PATCH /v1/tenant/configuration.");
            }

            return 0;
        }

        Console.WriteLine($"Created root tenant '{setup.Tenant.ExternalId}' ({setup.Tenant.Id}).");
        Console.WriteLine("Store these keys now; they cannot be shown again.");
        PrintKey(setup.TenantKey!);
        PrintKey(setup.ManagementKey!);
        return 0;
    }

    case ["keys", "list", ..]:
    {
        var result = await admin.ListRootKeysAsync(root);
        if (result.Error is { } error)
        {
            return Fail(error);
        }

        foreach (var key in result.Value!)
        {
            var state = key.RevokedAt is { } revoked ? $"revoked {revoked:u}" : "active";
            Console.WriteLine($"{key.Id}  {key.Purpose.ToString().ToLowerInvariant(),-10}  {key.CreatedAt:u}  {state,-28}  {key.Label}");
        }

        return 0;
    }

    case ["keys", "issue", ..]:
    {
        if (!Enum.TryParse<ApiKeyPurpose>(options.GetValueOrDefault("purpose"), ignoreCase: true, out var purpose)
            || options["purpose"] is not ("tenant" or "management"))
        {
            Console.Error.WriteLine("--purpose must be 'tenant' or 'management'.");
            return 1;
        }

        var result = await admin.IssueRootKeyAsync(root, purpose, options.GetValueOrDefault("label"), operatorName);
        if (result.Error is { } error)
        {
            return Fail(error);
        }

        Console.WriteLine("Store this key now; it cannot be shown again.");
        PrintKey(result.Value!);
        return 0;
    }

    case ["keys", "revoke", var keyIdText, ..] when Guid.TryParse(keyIdText, out var keyId):
    {
        var result = await admin.RevokeRootKeyAsync(root, keyId, operatorName);
        if (result.Error is { } error)
        {
            return Fail(error);
        }

        Console.WriteLine($"Key {keyId} is revoked.");
        return 0;
    }

    default:
        Console.Error.WriteLine(Usage);
        return 1;
}

async Task MigrateAsync()
{
    var db = services.GetRequiredService<PairrotDbContext>();
    await db.Database.MigrateAsync();
    Console.WriteLine("Database migrations are up to date.");
}

static void PrintKey(IssuedApiKey issued) =>
    Console.WriteLine($"  {issued.Key.Purpose.ToString().ToLowerInvariant(),-10} key {issued.Key.Id}: {issued.Secret}");

static int Fail(ServiceError error)
{
    Console.Error.WriteLine($"{error.Code}: {error.Message}");
    return 1;
}

static Dictionary<string, string> ParseOptions(string[] args)
{
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Length)
        {
            options[args[i][2..]] = args[++i];
        }
    }

    return options;
}
