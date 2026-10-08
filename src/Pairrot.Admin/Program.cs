using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Pairrot.Core.Tenants;
using Pairrot.Core.Inbound;
using Pairrot.Infrastructure;
using Pairrot.Infrastructure.Erasure;
using Pairrot.Infrastructure.Inbound;
using Pairrot.Infrastructure.Matching;
using Pairrot.Infrastructure.Persistence;
using Pairrot.Infrastructure.Tenants;
using Pairrot.Infrastructure.Transactions;
using Pairrot.Infrastructure.Users;
using Pairrot.Infrastructure.Webhooks;

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

      tenants erase <t> --reason <text>        Erase a tenant (Feature 14), a root included: personal
                                               data is purged in the background, financial facts
                                               stay. A tenant that is not deleted is deleted first;
                                               its subtenants must be deleted. No minimum age.
      tenants erasure-status [<t>]             Queued, running, and done erasures with counts.
      retention run                            Run one payer-data retention pass now and print counts.

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

      inbound list [--filter <f>] [--limit <n>]
                                               List received emails. <f>: attention (default:
                                               quarantined, failed, or held), quarantined, failed,
                                               held, all.
      inbound show <message-id>                Routing evidence, holds, errors, receipts, and retrievals.
      inbound retry-routing <message-id>       Route a quarantined email again against the address
                                               map (after fixing it). Routed emails never move.
      inbound retry-fetch <message-id>         Queue content retrieval again, e.g. after fixing
                                               provider credentials. The API's worker runs it.
      inbound reprocess <message-id>           Queue parsing again with the installed bank profile,
                                               e.g. after a parser fix or after reactivation.
      inbound parse-pending                    Queue parsing of every eligible stored message that
                                               was never parsed (the API also does this periodically).
      inbound paused                           Addresses paused for foreign mail (Feature 8), with
                                               their foreign messages in the current window.
      inbound resume <address>                 Resume a paused address, also one that only an
                                               operator may resume.
      inbound volume [--days <n>]              Received messages per provider instance and day,
                                               foreign mail separately (default 7 days).
      inbound sync                             Recovery-check state per provider instance: last
                                               attempt and success, covered-through, next due,
                                               discovered messages, errors, and coverage gaps.
      inbound sync run <instance>              Queue a recovery check now (the API's worker runs it).
      inbound sync set-from <instance> <time>  Recover from an earlier time (ISO 8601 with offset),
                                               e.g. when importing an existing receiving account.

      payments sweep                           Record missing review cases and queue matching of
                                               eligible transactions that never had a matching job
                                               (the API also does this periodically).
      payments check                           Verify that every paid amount equals its active
                                               allocations and that no disputed, duplicate,
                                               rejected, or reviewed receipt is counted.

      accounts show <iban|account-number>      Every registration of a bank account across tenants,
                                               with status, archived reason, and dates (Feature 13).
      accounts release <connection-id> --reason <text>
                                               Archive a registration with reason operator, e.g. when
                                               the wrong tenant holds the account. Audited on the owner.

      reviews show <case-id>                   A review case with its local evidence and the versions
                                               a decision needs.
      reviews resolve <case-id> --decision <d> --reason <text> --expected-version <n>
                [--expected-transaction-versions <id=v,...>] [--reason-code <code>]
                [--canonical-transaction-id <id>] [--idempotency-key <k>]
                                               Decide a case as the operator. An account movement
                                               conflict takes reject with --reason-code
                                               duplicate_of_foreign_movement, or confirm_distinct.

      webhooks backfill <tenant> --from <time> --to <time> [--apply]
                                               Payment events in [from, to) within the tenant's
                                               webhook scope that its endpoint never received.
                                               Shows the count and destination; --apply queues
                                               them to the endpoint's current destination.

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
var inboundAdmin = services.GetRequiredService<InboundAdministration>();
var discovery = services.GetRequiredService<InboundDiscoveryService>();
var transactions = services.GetRequiredService<IncomingTransactionService>();
var forwarding = services.GetRequiredService<ForwardingService>();

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

    case ["tenants", "erase", var tenantReference, ..]:
    {
        if (await userAdmin.FindTenantAsync(tenantReference) is not { } tenant)
        {
            Console.Error.WriteLine($"Tenant '{tenantReference}' was not found.");
            return 1;
        }

        var result = await services.GetRequiredService<TenantErasureService>()
            .RequestAsOperatorAsync(tenant.Id, options.GetValueOrDefault("reason"), Actor.Administrator(operatorName));
        return Report(result, outcome => outcome.Replayed
            ? $"Erasure of {tenant.Id} was already requested ({Snake(outcome.Erasure.State)}); nothing changed."
            : $"Erasure of {tenant.Id} is queued (job {outcome.Erasure.JobId}); the API's worker runs it. "
                + "Check it with tenants erasure-status.");
    }

    case ["tenants", "erasure-status", ..]:
    {
        Guid? only = null;
        if (args.Length > 2 && !args[2].StartsWith("--", StringComparison.Ordinal))
        {
            if (await userAdmin.FindTenantAsync(args[2]) is not { } tenant)
            {
                Console.Error.WriteLine($"Tenant '{args[2]}' was not found.");
                return 1;
            }

            only = tenant.Id;
        }

        foreach (var (tenant, erasure) in await services.GetRequiredService<TenantErasureService>().ListAsync(only))
        {
            var parent = tenant.ParentTenant is { } p ? $"child of {p.ExternalId}" : "root";
            Console.WriteLine($"{tenant.Id}  {Snake(erasure.State),-7}  {parent}  requested {erasure.RequestedAt:u} by {erasure.RequestedBy}"
                + (erasure.CompletedAt is { } done ? $", done {done:u}" : ""));
            Console.WriteLine($"  counts       {erasure.Counts}");
            if (erasure.Limitations != "[]")
            {
                Console.WriteLine($"  limitations  {erasure.Limitations}");
            }
        }

        return 0;
    }

    case ["retention", "run", ..]:
    {
        var report = await services.GetRequiredService<PayerDataRetention>().RunAsync();
        Console.WriteLine($"Payer data received before {report.Cutoff:u} is purged.");
        foreach (var (instance, count) in report.MessagesByInstance.OrderBy(i => i.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"  {instance,-20} {count,6} message(s) expired");
        }

        Console.WriteLine($"  {report.Messages} message(s), {report.Receipts} delivery payload(s), {report.ParseRuns} parse run(s), "
            + $"{report.ReviewCases} review text(s), {report.JobErrors} job error(s), {report.Transactions} transaction(s).");
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

    case ["inbound", "list", ..]:
    {
        if (!Enum.TryParse<InboundAttention>(options.GetValueOrDefault("filter") ?? "attention", ignoreCase: true, out var filter)
            || !int.TryParse(options.GetValueOrDefault("limit") ?? "50", out var limit) || limit < 1)
        {
            Console.Error.WriteLine("--filter must be attention, quarantined, failed, held, or all; --limit a positive number.");
            return 1;
        }

        foreach (var m in await inboundAdmin.ListAsync(filter, limit))
        {
            var hold = m.HoldReason is { } reason ? $"hold:{Snake(reason)}" : "";
            Console.WriteLine($"{m.Id}  {m.ReceivedAt:u}  {Snake(m.RoutingState),-19}  {Snake(m.ContentState),-9}  {hold,-30}  "
                + $"{m.TenantId?.ToString() ?? "-"}  {string.Join(",", m.EnvelopeRecipients)}");
        }

        return 0;
    }

    case ["inbound", "show", var idText, ..] when Guid.TryParse(idText, out var messageId):
    {
        if (await inboundAdmin.GetAsync(messageId) is not { } view)
        {
            Console.Error.WriteLine("not_found: No such inbound message.");
            return 1;
        }

        var m = view.Message;
        Console.WriteLine($"Message      {m.Id} ({Snake(m.SourceKind)}; {Snake(m.Environment)}; instance {m.ProviderInstanceId}, message {m.SourceMessageId}, {Snake(m.ContentMode)} content)");
        Console.WriteLine($"Received     {m.ReceivedAt:u}; accepted {m.CreatedAt:u}");
        Console.WriteLine($"From         {m.HeaderFrom} (envelope {m.EnvelopeSender ?? "-"})");
        Console.WriteLine($"Subject      {m.Subject}");
        Console.WriteLine($"Recipients   {string.Join(", ", m.EnvelopeRecipients)} ({m.RecipientEvidence})");
        Console.WriteLine($"Routing      {Snake(m.RoutingState)}{(m.TenantId is { } t ? $" -> tenant {t}, connection {m.BankConnectionId}" : "")}");
        if (view.Address is { } address)
        {
            Console.WriteLine($"Address      {address.Address} ({address.ProviderInstanceId}, {Snake(address.Environment)}, {Snake(address.State)})");
        }

        Console.WriteLine($"Hold         {(m.HoldReason is { } hold ? Snake(hold) : "none")}");
        Console.WriteLine($"Sender class {(m.SenderClass is { } senderClass ? Snake(senderClass) : "-")} ({m.SenderDomain ?? "no domain"}){(m.RetainUntil is { } until ? $", deleted after {until:u}" : "")}");
        Console.WriteLine(m.ContentState switch
        {
            InboundContentState.Erased => "Content      content erased (the tenant was erased; only routing facts remain)",
            InboundContentState.Expired => "Content      content expired (purged by the payer-data retention limit)",
            _ => $"Content      {Snake(m.ContentState)}{(m.ContentStoredAt is { } at ? $", stored {at:u}, sha256 {m.ContentSha256}" : "")}",
        });
        Console.WriteLine($"Sender auth  spf={m.SpfResult} dkim={m.DkimResult} dmarc={m.DmarcResult} ({m.AuthenticationOrigin ?? "no evidence"})");
        if (m.FetchErrorCode is not null)
        {
            Console.WriteLine($"Last error   {m.FetchErrorCode} at {m.FetchErrorAt:u}: {m.FetchErrorMessage}");
        }

        foreach (var r in view.Receipts)
        {
            Console.WriteLine($"Receipt      {r.SourceDeliveryId}  {r.TransportKind} ({r.DeliveryEvidence})  {Snake(r.Outcome)}  {r.AcceptedAt:u}");
        }

        foreach (var j in view.Jobs)
        {
            var error = j.LastErrorCode is null ? "" : $"  {j.LastErrorCode}: {j.LastErrorMessage}";
            Console.WriteLine($"Retrieval    #{j.Generation} {Snake(j.State)}, {j.AttemptCount} attempt(s), by {j.RequestedBy}, due {j.DueAt:u}{error}");
        }

        foreach (var run in await services.GetRequiredService<PairrotDbContext>().MessageParseRuns
                     .Where(r => r.InboundMessageId == m.Id).OrderBy(r => r.JobGeneration).ToListAsync())
        {
            Console.WriteLine($"Parse        #{run.JobGeneration} {Snake(run.Outcome)} ({run.ReasonCode}: {run.ReasonMessage}) {run.BankProfile ?? "-"} {run.ParserVersion ?? ""}, trust {(run.TrustDecision is { } trust ? Snake(trust) : "-")}{(run.TransactionId is { } tx ? $", transaction {tx}" : "")}");
        }

        return 0;
    }

    case ["inbound", "retry-routing", var idText, ..] when Guid.TryParse(idText, out var messageId):
        return Report(await inboundAdmin.RetryRoutingAsync(messageId, operatorName), m => m.RoutingState == InboundRoutingState.Routed
            ? $"Routed to tenant {m.TenantId}, connection {m.BankConnectionId}{(m.HoldReason is { } hold ? $" (held: {Snake(hold)})" : "")}."
            : $"Still quarantined: {Snake(m.RoutingState)}.");

    case ["inbound", "retry-fetch", var idText, ..] when Guid.TryParse(idText, out var messageId):
        return Report(await inboundAdmin.RetryFetchAsync(messageId, operatorName), queued => queued.Created
            ? $"Queued retrieval job {queued.Job.Id} (generation {queued.Job.Generation})."
            : $"Retrieval job {queued.Job.Id} is already {Snake(queued.Job.State)}.");

    case ["inbound", "reprocess", var idText, ..] when Guid.TryParse(idText, out var messageId):
        return Report(await transactions.ReprocessAsOperatorAsync(messageId, Actor.Administrator(operatorName)), queued => queued.Created
            ? $"Queued parse job {queued.Job.Id} (generation {queued.Job.Generation})."
            : $"Parse job {queued.Job.Id} is already {Snake(queued.Job.State)}.");

    case ["inbound", "paused", ..]:
        foreach (var view in await forwarding.ListPausedAsync())
        {
            var a = view.Address;
            Console.WriteLine($"{a.Address}  tenant {a.TenantId}, connection {a.BankConnectionId}, paused {a.PausedAt:u}, "
                + $"{view.ForeignMessagesInWindow} foreign in window, resumed {a.ResumeCount}x{(a.OperatorResumeRequired ? ", operator resume required" : "")}"
                + $"{(a.State == InboundAddressState.Retired ? ", retired" : "")}");
        }

        return 0;

    case ["inbound", "resume", var addressText, ..]:
        return Report(await forwarding.ResumeAsOperatorAsync(addressText, operatorName), a => $"{a.Address} resumed.");

    case ["inbound", "volume", ..]:
    {
        var days = int.TryParse(options.GetValueOrDefault("days"), out var n) && n > 0 ? n : 7;
        foreach (var v in await forwarding.DailyVolumeAsync(days))
        {
            Console.WriteLine($"{v.Day:yyyy-MM-dd}  {v.ProviderInstanceId,-20} {v.Messages,6} messages, {v.Foreign,6} foreign");
        }

        return 0;
    }

    case ["inbound", "parse-pending", ..]:
        Console.WriteLine($"Queued {await services.GetRequiredService<ParseScheduling>().QueueUnparsedAsync()} message(s) for parsing.");
        return 0;

    case ["payments", "sweep", ..]:
    {
        var (cases, queued) = await services.GetRequiredService<MatchingScheduling>().SweepAsync();
        Console.WriteLine($"Recorded {cases} review case(s); queued {queued} transaction(s) for matching.");
        return 0;
    }

    case ["payments", "check", ..]:
    {
        var problems = await services.GetRequiredService<MatchingScheduling>().CheckConsistencyAsync();
        foreach (var problem in problems)
        {
            Console.WriteLine(problem);
        }

        Console.WriteLine(problems.Count == 0 ? "Payment totals and allocations are consistent." : $"{problems.Count} problem(s) found.");
        return problems.Count == 0 ? 0 : 1;
    }

    case ["accounts", "show", var accountText, ..]:
    {
        var result = await services.GetRequiredService<Pairrot.Infrastructure.Banking.BankConnectionService>().FindRegistrationsAsync(accountText);
        if (result.Error is { } error)
        {
            return Fail(error);
        }

        var found = result.Value!;
        Console.WriteLine($"Account      {found.Account.Id}  {found.Account.Iban}  {found.Account.AccountNumber}  since {found.Account.CreatedAt:u}");
        foreach (var r in found.Registrations)
        {
            var c = r.Connection;
            var owner = r.ParentExternalId is { } parent ? $"{parent}/{r.Owner.ExternalId}" : r.Owner.ExternalId;
            var archived = c.ArchivedAt is { } at ? $", archived {at:u} ({Snake(c.ArchivedReason!.Value)})" : "";
            Console.WriteLine($"Registration {c.Id}  {Snake(c.Status),-8}  tenant {owner} ({r.Owner.Id}, {Snake(r.Owner.Status)})  created {c.CreatedAt:u}{archived}");
        }

        return 0;
    }

    case ["accounts", "release", var connectionText, ..] when Guid.TryParse(connectionText, out var connectionId):
        return Report(await services.GetRequiredService<Pairrot.Infrastructure.Banking.BankConnectionService>()
                .ReleaseAsOperatorAsync(connectionId, options.GetValueOrDefault("reason"), Actor.Administrator(operatorName)),
            c => $"Bank connection {c.Id} is archived ({Snake(c.ArchivedReason!.Value)}); its account can be registered again.");

    case ["reviews", "show", var caseText, ..] when Guid.TryParse(caseText, out var caseId):
    {
        var result = await services.GetRequiredService<ReviewCaseService>().GetAsOperatorAsync(caseId);
        if (result.Error is { } error)
        {
            return Fail(error);
        }

        var detail = result.Value!;
        var c = detail.Case;
        Console.WriteLine($"Case         {c.Id} ({Snake(c.Issue)}, {c.ReasonCode}) {Snake(c.State)}{(c.Resolution is { } r ? $": {Snake(r)}" : "")}, version {c.Version}");
        Console.WriteLine($"Owner        tenant {c.TenantId}, connection {c.BankConnectionId}, account {c.BankAccountId}");
        Console.WriteLine($"Reason       {c.ReasonMessage}");
        foreach (var t in detail.Transactions.Select(v => v.Transaction))
        {
            Console.WriteLine($"Transaction  {t.Id} version {t.Version}: {t.Amount} {t.Currency}, VS {t.VariableSymbol ?? "-"}, booked {t.BookedOn:yyyy-MM-dd}, "
                + $"ref {t.BankTransactionId ?? "-"}, {Snake(t.IngestionStatus)}, {Snake(t.Handling)}{(t.MovementConflict ? ", movement conflict" : "")}");
        }

        foreach (var run in detail.Runs)
        {
            Console.WriteLine($"Parse run    {run.Id} message {run.InboundMessageId}: {Snake(run.Outcome)} ({run.ReasonCode})");
        }

        foreach (var d in detail.Decisions)
        {
            Console.WriteLine($"Decision     {d.CreatedAt:u} {Snake(d.Action)}{(d.ReasonCode is { } code ? $" [{code}]" : "")} by {d.Actor}: {d.Reason}");
        }

        return 0;
    }

    case ["reviews", "resolve", var caseText, ..] when Guid.TryParse(caseText, out var caseId):
    {
        if (!Enum.TryParse<Pairrot.Core.Matching.ReviewAction>(
                (options.GetValueOrDefault("decision") ?? "").Replace("_", ""), ignoreCase: true, out var decision)
            || decision is Pairrot.Core.Matching.ReviewAction.Reopen or Pairrot.Core.Matching.ReviewAction.Superseded)
        {
            Console.Error.WriteLine("--decision must be confirm_payment, confirm_distinct, mark_duplicate, keep_existing, or reject.");
            return 1;
        }

        if (!int.TryParse(options.GetValueOrDefault("expected-version"), out var version))
        {
            Console.Error.WriteLine("--expected-version is required (see reviews show).");
            return 1;
        }

        var versions = new Dictionary<Guid, int>();
        foreach (var pair in (options.GetValueOrDefault("expected-transaction-versions") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (pair.Split('=') is not [var idText, var versionText] || !Guid.TryParse(idText, out var id) || !int.TryParse(versionText, out var v))
            {
                Console.Error.WriteLine("--expected-transaction-versions must be a list of <transaction-id>=<version>, separated by commas.");
                return 1;
            }

            versions[id] = v;
        }

        Guid? canonical = Guid.TryParse(options.GetValueOrDefault("canonical-transaction-id"), out var canonicalId) ? canonicalId : null;
        var reasonCode = options.GetValueOrDefault("reason-code");
        var key = options.GetValueOrDefault("idempotency-key") ?? Guid.NewGuid().ToString();
        var input = new ResolveInput(decision, options.GetValueOrDefault("reason"), version, versions, canonical, reasonCode);
        var command = new CommandRequest(key, "review_case.resolve",
            ManualCommands.Fingerprint("review_case.resolve", caseId, Snake(decision), input.Reason, version,
                versions.OrderBy(v => v.Key).Select(v => $"{v.Key}={v.Value}").ToArray(), canonical, reasonCode),
            Actor.Administrator(operatorName));
        return Report(await services.GetRequiredService<ReviewCaseService>().ResolveAsOperatorAsync(caseId, input, command,
                detail => new { case_id = detail.Case.Id, state = Snake(detail.Case.State), resolution = detail.Case.Resolution is { } r ? Snake(r) : null }),
            outcome => outcome.Replayed ? $"Already decided with this idempotency key: {outcome.Body}" : $"Decided: {outcome.Body}");
    }

    case ["webhooks", "backfill", var tenantReference, ..]:
    {
        if (await userAdmin.FindTenantAsync(tenantReference) is not { } subscriber)
        {
            Console.Error.WriteLine($"Tenant '{tenantReference}' was not found.");
            return 1;
        }

        if (!Pairrot.Core.Payments.PaymentRequestRules.TryParseDueAt(options.GetValueOrDefault("from") ?? "", out var from)
            || !Pairrot.Core.Payments.PaymentRequestRules.TryParseDueAt(options.GetValueOrDefault("to") ?? "", out var to) || to <= from)
        {
            Console.Error.WriteLine("--from and --to are required ISO 8601 times with an offset, --from before --to.");
            return 1;
        }

        var webhooks = services.GetRequiredService<WebhookService>();
        var (endpoint, eventIds) = await webhooks.BackfillCandidatesAsync(subscriber.Id, from, to);
        if (endpoint is null)
        {
            Console.Error.WriteLine("The tenant has no webhook endpoint.");
            return 1;
        }

        Console.WriteLine($"{eventIds.Count} event(s) for {endpoint.Url} (revision {endpoint.DestinationRevision}, {Snake(endpoint.State)}).");
        if (!args.Contains("--apply"))
        {
            Console.WriteLine("Nothing queued; add --apply to send them.");
            return 0;
        }

        Console.WriteLine($"Queued {await webhooks.BackfillAsync(endpoint.Id, eventIds, Actor.Administrator(operatorName))} delivery(ies).");
        return 0;
    }

    case ["inbound", "sync", "run", var instanceId, ..]:
        return Report(await discovery.RunNowAsync(instanceId, $"operator:{operatorName}"), queued => queued.Created
            ? $"Queued recovery check {queued.Job.Id} for {instanceId}."
            : $"A recovery check for {instanceId} is already {Snake(queued.Job.State)} ({queued.Job.Id}).");

    case ["inbound", "sync", "set-from", var instanceId, var fromText, ..]:
    {
        if (!Pairrot.Core.Payments.PaymentRequestRules.TryParseDueAt(fromText, out var from))
        {
            Console.Error.WriteLine("The time must be ISO 8601 with an offset, e.g. 2026-09-01T00:00:00+02:00.");
            return 1;
        }

        return Report(await discovery.SetSyncFromAsync(instanceId, from),
            state => $"{instanceId} recovers from {state.SyncFrom:u}; the next check scans from there.");
    }

    case ["inbound", "sync", ..]:
    {
        foreach (var (state, job) in await discovery.ListAsync())
        {
            Console.WriteLine($"{state.ProviderInstanceId}");
            Console.WriteLine($"  sync from        {state.SyncFrom:u}");
            Console.WriteLine($"  covered through  {(state.CoveredThrough is { } covered ? covered.ToString("u") : "not yet")}");
            Console.WriteLine($"  last attempt     {state.LastAttemptAt?.ToString("u") ?? "-"}; last success {state.LastSuccessAt?.ToString("u") ?? "-"}");
            Console.WriteLine($"  next due         {state.NextDueAt?.ToString("u") ?? "-"}");
            Console.WriteLine($"  discovered       {state.DiscoveredCount} (last check {state.LastRunDiscoveredCount})");
            if (state.LastErrorCode is not null)
            {
                Console.WriteLine($"  last error       {state.LastErrorCode}: {state.LastErrorMessage}");
            }

            if (state.CoverageGap is not null)
            {
                Console.WriteLine($"  coverage gap     {state.CoverageGap}");
            }

            if (job is not null)
            {
                Console.WriteLine($"  latest check     #{job.Generation} {Snake(job.State)}, {job.AttemptCount} attempt(s), by {job.RequestedBy}");
            }
        }

        return 0;
    }

    default:
        Console.Error.WriteLine(Usage);
        return 1;
}

static string Snake<TEnum>(TEnum value) where TEnum : struct, Enum =>
    System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

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
