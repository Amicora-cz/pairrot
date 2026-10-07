using System.Globalization;
using System.Security.Claims;
using System.Text;
using Pairrot.Api.Auth;
using Pairrot.Api.Http;
using Pairrot.Core.Matching;
using Pairrot.Core.Payments;
using Pairrot.Infrastructure.Matching;
using Pairrot.Infrastructure.Tenants;

namespace Pairrot.Api.Endpoints;

/// <summary>
/// Feature 009: matching, manual allocation, review, and payment history, under
/// <c>/v1/tenant</c> and <c>/v1/subtenants/{subtenantId}</c>. Every write is a command with an
/// <c>Idempotency-Key</c> and the versions the caller saw; no endpoint sets a paid amount or
/// edits bank facts.
/// </summary>
public static class MatchingEndpoints
{
    public const string IdempotencyKeyHeader = "Idempotency-Key";
    private const string SubtenantRouteValue = "subtenantId";

    private static readonly HashSet<string> AllocateFields =
        ["payment_request_id", "expected_transaction_version", "expected_request_version", "reason"];

    private static readonly HashSet<string> RevokeFields =
        ["expected_allocation_version", "expected_transaction_version", "expected_request_version", "reason"];

    private static readonly HashSet<string> HandlingFields = ["expected_version", "reason"];

    private static readonly HashSet<string> ResolveFields =
        ["decision", "reason", "expected_version", "expected_transaction_versions", "canonical_transaction_id"];

    private static readonly HashSet<string> ReopenFields = ["reason", "expected_version", "expected_transaction_versions"];

    private static readonly Dictionary<string, ReviewAction> Decisions = new[]
    {
        ReviewAction.ConfirmPayment, ReviewAction.ConfirmDistinct, ReviewAction.MarkDuplicate, ReviewAction.KeepExisting, ReviewAction.Reject,
    }.ToDictionary(InboundMessageResponse.Snake);

    public static void MapTenantMatching(this RouteGroupBuilder tenantGroup) => Map(tenantGroup);

    public static void MapSubtenantMatching(this RouteGroupBuilder subtenantGroup) =>
        Map(subtenantGroup.MapGroup($"/{{{SubtenantRouteValue}:guid}}"));

    private static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/transactions/{id:guid}/matching-candidates", CandidatesAsync);
        group.MapPost("/transactions/{id:guid}/match", MatchAsync);
        group.MapPost("/transactions/{id:guid}/allocations", AllocateAsync);
        group.MapPost("/transactions/{id:guid}/ignore", (Guid id, HttpContext http, ClaimsPrincipal user, PaymentMatchingService s, CancellationToken ct) =>
            HandleAsync(id, http, user, s, "ignore", s.IgnoreAsync, ct));
        group.MapPost("/transactions/{id:guid}/restore", (Guid id, HttpContext http, ClaimsPrincipal user, PaymentMatchingService s, CancellationToken ct) =>
            HandleAsync(id, http, user, s, "restore", s.RestoreAsync, ct));
        group.MapPost("/transactions/{id:guid}/enable-auto-matching", (Guid id, HttpContext http, ClaimsPrincipal user, PaymentMatchingService s, CancellationToken ct) =>
            HandleAsync(id, http, user, s, "enable_auto_matching", s.EnableAutoMatchingAsync, ct));
        group.MapPost("/allocations/{id:guid}/revoke", RevokeAsync);
        group.MapGet("/review-cases", ListCasesAsync);
        group.MapGet("/review-cases/{id:guid}", GetCaseAsync);
        group.MapPost("/review-cases/{id:guid}/resolve", ResolveAsync);
        group.MapPost("/review-cases/{id:guid}/reopen", ReopenAsync);
        group.MapGet("/payment-requests/{id:guid}/allocations", AllocationsAsync);
        group.MapGet("/payment-requests/{id:guid}/changes", ChangesAsync);
    }

    private static async Task<IResult> CandidatesAsync(
        Guid id, HttpContext http, ClaimsPrincipal user, PaymentMatchingService matching, CancellationToken ct)
    {
        var query = http.Request.Query;
        var search = new CandidateSearch();
        if (query.TryGetValue("external_id", out var externalId))
        {
            search = search with { ExternalId = externalId.ToString() };
        }

        if (query.TryGetValue("variable_symbol", out var symbolValue))
        {
            var symbol = symbolValue.ToString();
            if (symbol.Length is 0 or > SpaydPayment.MaxVariableSymbolLength || !symbol.All(char.IsAsciiDigit))
            {
                return ApiErrors.Validation($"variable_symbol must be 1 to {SpaydPayment.MaxVariableSymbolLength} digits.");
            }

            search = search with { VariableSymbol = symbol };
        }

        if (query.TryGetValue("amount", out var amountValue))
        {
            if (!long.TryParse(amountValue, NumberStyles.None, CultureInfo.InvariantCulture, out var amount) || amount <= 0)
            {
                return ApiErrors.Validation("amount must be a positive integer in minor units.");
            }

            search = search with { Amount = amount };
        }

        var result = await matching.GetCandidatesAsync(user.GetTenantId(), Subtenant(http), id, search, ct);
        return result.Error is { } failure
            ? ApiErrors.From(failure)
            : Results.Ok(MatchingCandidatesResponse.From(result.Value!, matching.Now()));
    }

    private static async Task<IResult> MatchAsync(
        Guid id, HttpContext http, ClaimsPrincipal user, PaymentMatchingService matching, CancellationToken ct)
    {
        var result = await matching.QueueMatchAsync(user.GetTenantId(), Subtenant(http), id, user.GetActor(), ct);
        if (result.Error is { } failure)
        {
            return ApiErrors.From(failure);
        }

        var (job, created) = result.Value;
        return Results.Accepted(null, QueuedParseResponse.From(job, created));
    }

    private static async Task<IResult> AllocateAsync(
        Guid id, HttpContext http, ClaimsPrincipal user, PaymentMatchingService matching, CancellationToken ct)
    {
        var (body, error) = await JsonBody.ReadAsync(http.Request, AllocateFields);
        if (body is null)
        {
            return error!;
        }

        if (!body.TryGetString("payment_request_id", out var requestValue, out error)
            || !body.TryGetVersion("expected_transaction_version", out var transactionVersion, out error)
            || !body.TryGetVersion("expected_request_version", out var requestVersion, out error)
            || !body.TryGetString("reason", out var reason, out error))
        {
            return error!;
        }

        if (requestValue.Value is not { } requestText || !Guid.TryParse(requestText, out var requestId))
        {
            return ApiErrors.Result(StatusCodes.Status400BadRequest, "invalid_request", "payment_request_id must be the ID of a payment request.",
                "payment_request_id");
        }

        if (!TryCommand(http, user, "allocate", out var command, out error, Subtenant(http), id, requestId, transactionVersion, requestVersion, reason.Value))
        {
            return error!;
        }

        var now = matching.Now();
        return ToResult(http, await matching.AllocateAsync(user.GetTenantId(), Subtenant(http), id,
            new AllocateInput(requestId, transactionVersion, requestVersion, reason.Value), command!,
            outcome => AllocationOutcomeResponse.From(outcome, now), ct));
    }

    private static async Task<IResult> RevokeAsync(
        Guid id, HttpContext http, ClaimsPrincipal user, PaymentMatchingService matching, CancellationToken ct)
    {
        var (body, error) = await JsonBody.ReadAsync(http.Request, RevokeFields);
        if (body is null)
        {
            return error!;
        }

        if (!body.TryGetVersion("expected_allocation_version", out var allocationVersion, out error)
            || !body.TryGetVersion("expected_transaction_version", out var transactionVersion, out error)
            || !body.TryGetVersion("expected_request_version", out var requestVersion, out error)
            || !body.TryGetString("reason", out var reason, out error))
        {
            return error!;
        }

        if (!TryCommand(http, user, "revoke", out var command, out error, Subtenant(http), id, allocationVersion, transactionVersion, requestVersion, reason.Value))
        {
            return error!;
        }

        var now = matching.Now();
        return ToResult(http, await matching.RevokeAsync(user.GetTenantId(), Subtenant(http), id,
            new RevokeInput(allocationVersion, transactionVersion, requestVersion, reason.Value), command!,
            outcome => AllocationOutcomeResponse.From(outcome, now), ct));
    }

    private delegate Task<ServiceResult<CommandOutcome>> HandlingCommand(
        Guid callerTenantId, Guid? subtenantId, Guid transactionId, HandlingInput input, CommandRequest command,
        Func<TransactionView, object> render, CancellationToken ct);

    private static async Task<IResult> HandleAsync(
        Guid id, HttpContext http, ClaimsPrincipal user, PaymentMatchingService matching, string action, HandlingCommand run, CancellationToken ct)
    {
        var (body, error) = await JsonBody.ReadAsync(http.Request, HandlingFields);
        if (body is null)
        {
            return error!;
        }

        if (!body.TryGetVersion("expected_version", out var version, out error) || !body.TryGetString("reason", out var reason, out error))
        {
            return error!;
        }

        if (!TryCommand(http, user, action, out var command, out error, Subtenant(http), id, version, reason.Value))
        {
            return error!;
        }

        return ToResult(http, await run(user.GetTenantId(), Subtenant(http), id, new HandlingInput(version, reason.Value), command!,
            view => IncomingTransactionResponse.From(view), ct));
    }

    private static async Task<IResult> ListCasesAsync(HttpContext http, ClaimsPrincipal user, ReviewCaseService reviews, CancellationToken ct)
    {
        var query = http.Request.Query;
        if (!Paging.TryRead(query, TenantService.DefaultPageSize, TenantService.MaxPageSize, out var limit, out var cursor, out var error))
        {
            return error!;
        }

        ReviewCaseState? state = null;
        if (query.TryGetValue("state", out var stateValue))
        {
            if (stateValue.ToString() is not ("open" or "resolved"))
            {
                return ApiErrors.Validation("state must be 'open' or 'resolved'.");
            }

            state = stateValue.ToString() == "open" ? ReviewCaseState.Open : ReviewCaseState.Resolved;
        }

        var result = await reviews.ListAsync(user.GetTenantId(), Subtenant(http), state, limit, cursor, ct);
        return result.Error is { } failure
            ? ApiErrors.From(failure)
            : Results.Ok(new ListResponse<ReviewCaseResponse>(result.Value!.Items.Select(ReviewCaseResponse.From).ToList(), result.Value.NextCursor));
    }

    private static async Task<IResult> GetCaseAsync(Guid id, HttpContext http, ClaimsPrincipal user, ReviewCaseService reviews, CancellationToken ct)
    {
        var result = await reviews.GetAsync(user.GetTenantId(), Subtenant(http), id, ct);
        return result.Error is { } failure ? ApiErrors.From(failure) : Results.Ok(ReviewCaseDetailResponse.From(result.Value!));
    }

    private static async Task<IResult> ResolveAsync(Guid id, HttpContext http, ClaimsPrincipal user, ReviewCaseService reviews, CancellationToken ct)
    {
        var (body, error) = await JsonBody.ReadAsync(http.Request, ResolveFields);
        if (body is null)
        {
            return error!;
        }

        if (!body.TryGetString("decision", out var decisionValue, out error)
            || !body.TryGetString("reason", out var reason, out error)
            || !body.TryGetVersion("expected_version", out var version, out error)
            || !body.TryGetVersionMap("expected_transaction_versions", out var versions, out error)
            || !body.TryGetString("canonical_transaction_id", out var canonicalValue, out error))
        {
            return error!;
        }

        if (decisionValue.Value is not { } decisionText || !Decisions.TryGetValue(decisionText, out var decision))
        {
            return ApiErrors.Result(StatusCodes.Status400BadRequest, "invalid_decision",
                $"decision must be one of: {string.Join(", ", Decisions.Keys)}.", "decision");
        }

        Guid? canonical = null;
        if (canonicalValue.Value is { } canonicalText)
        {
            if (!Guid.TryParse(canonicalText, out var parsed))
            {
                return ApiErrors.Result(StatusCodes.Status400BadRequest, "invalid_canonical_transaction",
                    "canonical_transaction_id must be a transaction ID.", "canonical_transaction_id");
            }

            canonical = parsed;
        }

        if (!TryCommand(http, user, "resolve_review_case", out var command, out error, Subtenant(http), id, decisionText, reason.Value, version,
                versions?.OrderBy(v => v.Key).Select(v => $"{v.Key}:{v.Value}"), canonical))
        {
            return error!;
        }

        return ToResult(http, await reviews.ResolveAsync(user.GetTenantId(), Subtenant(http), id,
            new ResolveInput(decision, reason.Value, version, versions, canonical), command!, d => ReviewCaseDetailResponse.From(d), ct));
    }

    private static async Task<IResult> ReopenAsync(Guid id, HttpContext http, ClaimsPrincipal user, ReviewCaseService reviews, CancellationToken ct)
    {
        var (body, error) = await JsonBody.ReadAsync(http.Request, ReopenFields);
        if (body is null)
        {
            return error!;
        }

        if (!body.TryGetString("reason", out var reason, out error)
            || !body.TryGetVersion("expected_version", out var version, out error)
            || !body.TryGetVersionMap("expected_transaction_versions", out var versions, out error))
        {
            return error!;
        }

        if (!TryCommand(http, user, "reopen_review_case", out var command, out error, Subtenant(http), id, reason.Value, version,
                versions?.OrderBy(v => v.Key).Select(v => $"{v.Key}:{v.Value}")))
        {
            return error!;
        }

        return ToResult(http, await reviews.ReopenAsync(user.GetTenantId(), Subtenant(http), id,
            new ReopenInput(reason.Value, version, versions), command!, d => ReviewCaseDetailResponse.From(d), ct));
    }

    private static async Task<IResult> AllocationsAsync(
        Guid id, HttpContext http, ClaimsPrincipal user, PaymentMatchingService matching, CancellationToken ct)
    {
        var result = await matching.ListAllocationsAsync(user.GetTenantId(), Subtenant(http), id, ct);
        return result.Error is { } failure
            ? ApiErrors.From(failure)
            : Results.Ok(new { items = result.Value!.Select(AllocationResponse.From).ToList() });
    }

    private static async Task<IResult> ChangesAsync(
        Guid id, HttpContext http, ClaimsPrincipal user, PaymentMatchingService matching, CancellationToken ct)
    {
        var query = http.Request.Query;
        if (!Paging.TryRead(query, TenantService.DefaultPageSize, TenantService.MaxPageSize, out var limit, out _, out var error))
        {
            return error!;
        }

        var after = 0;
        if (query.TryGetValue("after", out var afterValue)
            && (!int.TryParse(afterValue, NumberStyles.None, CultureInfo.InvariantCulture, out after) || after < 0))
        {
            return ApiErrors.Validation("after must be a non-negative sequence number.");
        }

        var result = await matching.ListChangesAsync(user.GetTenantId(), Subtenant(http), id, limit, after, ct);
        return result.Error is { } failure
            ? ApiErrors.From(failure)
            : Results.Ok(new PaymentChangeListResponse(result.Value.Items.Select(PaymentChangeResponse.From).ToList(), result.Value.Next));
    }

    /// <summary>Reads the required <c>Idempotency-Key</c> and fingerprints the command's input.</summary>
    internal static bool TryCommand(
        HttpContext http, ClaimsPrincipal user, string action, out CommandRequest? command, out IResult? error, params object?[] input) =>
        TryCommand(http, user.GetActor(), action, out command, out error, input);

    /// <summary>Reads the required <c>Idempotency-Key</c> and fingerprints the command's input.</summary>
    internal static bool TryCommand(
        HttpContext http, Actor actor, string action, out CommandRequest? command, out IResult? error, params object?[] input)
    {
        command = null;
        error = null;
        var key = http.Request.Headers[IdempotencyKeyHeader].ToString().Trim();
        if (key.Length == 0)
        {
            error = ApiErrors.Result(StatusCodes.Status400BadRequest, "idempotency_key_required",
                $"Send a unique {IdempotencyKeyHeader} header with this request, and the same one when retrying it.");
            return false;
        }

        if (key.Length > ManualCommands.MaxKeyLength || key.Any(c => c is < '!' or > '~'))
        {
            error = ApiErrors.Result(StatusCodes.Status400BadRequest, "invalid_idempotency_key",
                $"{IdempotencyKeyHeader} must be 1 to {ManualCommands.MaxKeyLength} visible ASCII characters.");
            return false;
        }

        command = new CommandRequest(key, action, ManualCommands.Fingerprint(action, input), actor);
        return true;
    }

    internal static IResult ToResult(HttpContext http, ServiceResult<CommandOutcome> result)
    {
        if (result.Error is { } failure)
        {
            return ApiErrors.From(failure);
        }

        var outcome = result.Value!;
        http.Response.Headers["Idempotent-Replayed"] = outcome.Replayed ? "true" : "false";
        return Results.Content(outcome.Body, "application/json", Encoding.UTF8, outcome.Status);
    }

    private static Guid? Subtenant(HttpContext http) =>
        http.GetRouteValue(SubtenantRouteValue) is string value && Guid.TryParse(value, out var id) ? id : null;
}
