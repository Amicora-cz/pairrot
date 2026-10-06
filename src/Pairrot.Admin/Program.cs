using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Pairrot.Core.Tenants;
using Pairrot.Infrastructure;
using Pairrot.Infrastructure.Persistence;
using Pairrot.Infrastructure.Tenants;
using Pairrot.Infrastructure.Users;

// Administrative CLI. Root tenant setup, root keys, and user accounts are deliberately not HTTP endpoints.

const string Usage = """
    Usage: Pairrot.Admin <command> [options]

    Commands:
      migrate                                  Apply pending database migrations.
      setup [--name <n>] [--contact-email <e>] [--admin-email <e>]
                                               Apply migrations, then create the root tenant with
                                               one tenant key and one management key. Idempotent:
                                               an existing root is left unchanged and no keys are issued.
                                               --admin-email also creates the first administrator
                                               (password prompted) unless the user already exists.
      keys list                                List the root tenant's keys (never secrets).
      keys issue --purpose <tenant|management> [--label <l>]
                                               Issue an additional root key; the secret is shown once.
      keys revoke <key-id>                     Revoke a root key.

      users list                               List users and their tenant memberships.
      users create --email <e> [--tenant <t>]  Create a user (password prompted) and optionally make
                                               them an admin of a tenant. An existing user's password
                                               is never changed.
      users grant --email <e> --tenant <t>     Add or reactivate an admin membership.
      users revoke --email <e> --tenant <t>    Remove an admin membership; access ends immediately.
      users disable --email <e>                Disable a user and revoke all their sessions.
      users enable --email <e>                 Re-enable a disabled user.
      users reset-password --email <e>         Set a new password (prompted), clear any lockout,
                                               and revoke all sessions.

    Common options:
      --root <external-id>   Root tenant for setup and keys (default: nalekci.cz)
      --operator <name>      Recorded in the audit log (default: current OS user)

    Tenants (<t>) are given by ID, by root external ID (nalekci.cz), or as root/child
    (nalekci.cz/studio_123). Passwords are read from the terminal without echo, or as one line
    from standard input when it is redirected. They are never printed or logged.

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
var userAdmin = services.GetRequiredService<UserAdministration>();

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
        }
        else
        {
            Console.WriteLine($"Created root tenant '{setup.Tenant.ExternalId}' ({setup.Tenant.Id}).");
            Console.WriteLine("Store these keys now; they cannot be shown again.");
            PrintKey(setup.TenantKey!);
            PrintKey(setup.ManagementKey!);
        }

        return options.TryGetValue("admin-email", out var adminEmail)
            ? await EnsureUserAsync(adminEmail, setup.Tenant.ExternalId)
            : 0;
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

    case ["users", "list", ..]:
    {
        foreach (var item in await userAdmin.ListAsync())
        {
            var state = item.User.IsDisabled ? "disabled" : "active";
            var tenants = string.Join(", ", item.Tenants.Select(TenantReference));
            Console.WriteLine($"{item.User.Id}  {item.User.Email,-40}  {state,-8}  {tenants}");
        }

        return 0;
    }

    case ["users", "create", ..]:
        return RequireOption("email") is { } createEmail
            ? await EnsureUserAsync(createEmail, options.GetValueOrDefault("tenant"))
            : 1;

    case ["users", "grant", ..]:
    {
        if (RequireOption("email") is not { } email || RequireOption("tenant") is not { } tenant)
        {
            return 1;
        }

        return Report(await userAdmin.GrantAdminAsync(email, tenant, operatorName), change => change == MembershipChange.Unchanged
            ? $"{email} is already an admin of {tenant}."
            : $"{email} is now an admin of {tenant}.");
    }

    case ["users", "revoke", ..]:
    {
        if (RequireOption("email") is not { } email || RequireOption("tenant") is not { } tenant)
        {
            return 1;
        }

        return Report(await userAdmin.RemoveAdminAsync(email, tenant, operatorName), change => change == MembershipChange.Removed
            ? $"{email} no longer has access to {tenant}."
            : $"{email} had no active membership in {tenant}.");
    }

    case ["users", "disable", ..]:
    {
        if (RequireOption("email") is not { } email)
        {
            return 1;
        }

        return Report(await userAdmin.DisableAsync(email, operatorName), changed => changed
            ? $"{email} is disabled and all their sessions are revoked. Tenant API keys are not affected."
            : $"{email} was already disabled.");
    }

    case ["users", "enable", ..]:
    {
        if (RequireOption("email") is not { } email)
        {
            return 1;
        }

        return Report(await userAdmin.EnableAsync(email, operatorName), changed => changed
            ? $"{email} is enabled."
            : $"{email} was not disabled.");
    }

    case ["users", "reset-password", ..]:
    {
        if (RequireOption("email") is not { } email)
        {
            return 1;
        }

        if (await userAdmin.FindUserAsync(email) is null)
        {
            Console.Error.WriteLine($"not_found: No user with the email '{email}'.");
            return 1;
        }

        var password = ReadNewPassword($"New password for {email}");
        return Report(await userAdmin.ResetPasswordAsync(email, password, operatorName),
            _ => $"The password of {email} was reset and all their sessions are revoked.");
    }

    default:
        Console.Error.WriteLine(Usage);
        return 1;
}

// Creates the user if missing (prompting for a password) and optionally grants admin access.
// An existing user's password is never changed.
async Task<int> EnsureUserAsync(string email, string? tenant)
{
    if (await userAdmin.FindUserAsync(email) is { } existing)
    {
        Console.WriteLine($"User {existing.Email} already exists ({existing.Id}); the password was not changed.");
    }
    else
    {
        var password = ReadNewPassword($"Password for {email}");
        var created = await userAdmin.CreateUserAsync(email, password, operatorName);
        if (created.Error is { } error)
        {
            return Fail(error);
        }

        Console.WriteLine($"Created user {created.Value!.Email} ({created.Value.Id}).");
    }

    if (tenant is null)
    {
        return 0;
    }

    return Report(await userAdmin.GrantAdminAsync(email, tenant, operatorName), change => change == MembershipChange.Unchanged
        ? $"{email} is already an admin of {tenant}."
        : $"{email} is now an admin of {tenant}.");
}

string? RequireOption(string name)
{
    if (options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
    {
        return value;
    }

    Console.Error.WriteLine($"--{name} is required.");
    return null;
}

async Task MigrateAsync()
{
    var db = services.GetRequiredService<PairrotDbContext>();
    await db.Database.MigrateAsync();
    Console.WriteLine("Database migrations are up to date.");
}

static void PrintKey(IssuedApiKey issued) =>
    Console.WriteLine($"  {issued.Key.Purpose.ToString().ToLowerInvariant(),-10} key {issued.Key.Id}: {issued.Secret}");

static string TenantReference(Tenant tenant) =>
    tenant.ParentTenant is { } parent ? $"{parent.ExternalId}/{tenant.ExternalId}" : tenant.ExternalId;

static int Report<T>(ServiceResult<T> result, Func<T, string> success)
{
    if (result.Error is { } error)
    {
        return Fail(error);
    }

    Console.WriteLine(success(result.Value!));
    return 0;
}

// Reads a password without echoing it. With redirected input, the first line is used.
static string ReadNewPassword(string prompt)
{
    if (Console.IsInputRedirected)
    {
        return Console.In.ReadLine() ?? string.Empty;
    }

    while (true)
    {
        var first = ReadHidden($"{prompt}: ");
        if (first == ReadHidden("Repeat the password: "))
        {
            return first;
        }

        Console.Error.WriteLine("The passwords do not match; try again.");
    }
}

static string ReadHidden(string prompt)
{
    Console.Write(prompt);
    var buffer = new StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter)
        {
            Console.WriteLine();
            return buffer.ToString();
        }

        if (key.Key == ConsoleKey.Backspace)
        {
            if (buffer.Length > 0)
            {
                buffer.Length--;
            }
        }
        else if (!char.IsControl(key.KeyChar))
        {
            buffer.Append(key.KeyChar);
        }
    }
}

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
