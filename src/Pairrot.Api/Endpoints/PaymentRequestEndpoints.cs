using System.Globalization;
using System.Security.Claims;
using Microsoft.Net.Http.Headers;
using Pairrot.Api.Auth;
using Pairrot.Api.Http;
using Pairrot.Core.Payments;
using Pairrot.Infrastructure.Matching;
using Pairrot.Infrastructure.Payments;
using Pairrot.Infrastructure.Tenants;

namespace Pairrot.Api.Endpoints;

/// <summary>
/// Expected payments, mapped like bank connections: under <c>/v1/tenant</c> for the caller's own
/// tenant and under <c>/v1/subtenants/{subtenantId}</c> for a direct child. The ETag is the
/// record's <c>version</c>; edits and cancellation of an open request require it in <c>If-Match</c>.
/// </summary>
public static class PaymentRequestEndpoints
{
    private const string SubtenantRouteValue = "subtenantId";

    private static readonly HashSet<string> CreateFields =
        ["bank_connection_id", "external_id", "amount", "currency", "variable_symbol", "message", "description", "due_at"];

    /// <summary>Fields that appear in responses but can never be changed through PATCH.</summary>
    private static readonly HashSet<string> ImmutableFields =
    [
        "id", "tenant_id", "bank_connection_id", "external_id", "account_number", "iban", "amount", "currency",
        "variable_symbol", "message", "status", "is_overdue", "paid_amount", "remaining_amount", "overpaid_amount", "payment_change_sequence", "cancellation_reason",
        "created_at", "updated_at", "cancelled_at", "version", "payment_instructions", "open_review_case_ids",
    ];

    private static readonly HashSet<string> UpdateFields = ["description", "due_at", .. ImmutableFields];

    private static readonly HashSet<string> CancelFields = ["reason"];

    private static readonly Dictionary<string, PaymentRequestStatus> Statuses = Enum.GetValues<PaymentRequestStatus>()
        .ToDictionary(InboundMessageResponse.Snake);

    /// <summary>Maps <c>/payment-requests</c> under the current tenant.</summary>
    public static void MapTenantPaymentRequests(this RouteGroupBuilder tenantGroup) =>
        Map(tenantGroup.MapGroup("/payment-requests"));

    /// <summary>Maps <c>/{subtenantId}/payment-requests</c> under the subtenant group.</summary>
    public static void MapSubtenantPaymentRequests(this RouteGroupBuilder subtenantGroup) =>
        Map(subtenantGroup.MapGroup($"/{{{SubtenantRouteValue}:guid}}/payment-requests"));

    private static void Map(RouteGroupBuilder group)
    {
        group.MapGet("", ListAsync);
        group.MapPost("", CreateAsync);
        group.MapGet("/{id:guid}", GetAsync);
        group.MapPatch("/{id:guid}", UpdateAsync);
        group.MapPost("/{id:guid}/cancel", CancelAsync);
        group.MapGet("/{id:guid}/payment-instructions", InstructionsAsync);
        group.MapGet("/{id:guid}/qr.png", QrPngAsync);
    }

    private static async Task<IResult> ListAsync(
        HttpContext http, ClaimsPrincipal user, PaymentRequestService requests, CancellationToken ct)
    {
        var query = http.Request.Query;
        if (!Paging.TryRead(query, TenantService.DefaultPageSize, TenantService.MaxPageSize, out var limit, out var cursor, out var error))
        {
            return error!;
        }

        PaymentRequestStatus? status = null;
        if (query.TryGetValue("status", out var statusValue))
        {
            if (!Statuses.TryGetValue(statusValue.ToString(), out var parsedStatus))
            {
                return ApiErrors.Validation($"status must be one of: {string.Join(", ", Statuses.Keys)}.");
            }

            status = parsedStatus;
        }

        Guid? connectionId = null;
        if (query.TryGetValue("bank_connection_id", out var connectionValue))
        {
            if (!Guid.TryParse(connectionValue, out var parsed))
            {
                return ApiErrors.Validation("bank_connection_id must be a UUID.");
            }

            connectionId = parsed;
        }

        string? symbol = null;
        if (query.TryGetValue("variable_symbol", out var symbolValue))
        {
            symbol = symbolValue.ToString();
            if (symbol.Length is 0 or > SpaydPayment.MaxVariableSymbolLength || !symbol.All(char.IsAsciiDigit))
            {
                return ApiErrors.Validation($"variable_symbol must be 1 to {SpaydPayment.MaxVariableSymbolLength} digits.");
            }
        }

        bool? overdue = null;
        if (query.TryGetValue("overdue", out var overdueValue))
        {
            if (!bool.TryParse(overdueValue, out var parsed))
            {
                return ApiErrors.Validation("overdue must be 'true' or 'false'.");
            }

            overdue = parsed;
        }

        string? externalId = query.TryGetValue("external_id", out var externalIdValue) ? externalIdValue.ToString() : null;
        var filter = new PaymentRequestFilter(externalId, status, connectionId, symbol, overdue);
        var result = await requests.ListAsync(user.GetTenantId(), Subtenant(http), filter, limit, cursor, ct);
        if (result.Error is { } failure)
        {
            return ApiErrors.From(failure);
        }

        var now = requests.Now();
        return Results.Ok(new ListResponse<PaymentRequestResponse>(
            result.Value!.Items.Select(p => PaymentRequestResponse.From(p, now)).ToList(), result.Value.NextCursor));
    }

    private static async Task<IResult> CreateAsync(
        HttpContext http, ClaimsPrincipal user, PaymentRequestService requests, CancellationToken ct)
    {
        var (body, error) = await JsonBody.ReadAsync(http.Request, CreateFields);
        if (body is null)
        {
            return error!;
        }

        if (!body.TryGetString("bank_connection_id", out var connectionValue, out error)
            || !body.TryGetString("external_id", out var externalId, out error)
            || !body.TryGetInteger("amount", out var amount, out error)
            || !body.TryGetString("currency", out var currency, out error)
            || !body.TryGetString("variable_symbol", out var variableSymbol, out error)
            || !body.TryGetString("message", out var message, out error)
            || !body.TryGetString("description", out var description, out error)
            || !TryGetDueAt(body, out var dueAt, out error))
        {
            return error!;
        }

        Guid? connectionId = null;
        if (connectionValue.Value is { } connectionText)
        {
            if (!Guid.TryParse(connectionText, out var parsed))
            {
                return ApiErrors.Result(StatusCodes.Status400BadRequest, PaymentRequestErrors.InvalidBankConnection,
                    "bank_connection_id must be the ID of a bank connection.", "bank_connection_id");
            }

            connectionId = parsed;
        }

        var input = new PaymentRequestInput(connectionId, externalId.Value, amount.Value, currency.Value,
            variableSymbol.Value, message.Value, description.Value, dueAt.Value);
        var result = await requests.CreateAsync(user.GetTenantId(), Subtenant(http), input, user.GetActor(), ct);
        if (result.Error is { } failure)
        {
            return ApiErrors.From(failure);
        }

        var created = result.Value!;
        var response = PaymentRequestResponse.From(created.Request, requests.Now()) with
        {
            Replayed = created.Replayed,
            PaymentInstructions = created.Payable ? PaymentInstructionsResponse.From(created.Request) : null,
        };
        SetETag(http, created.Request);
        http.Response.Headers.CacheControl = "no-store";
        return created.Replayed
            ? Results.Ok(response)
            : Results.Created($"{http.Request.Path}/{created.Request.Id}", response);
    }

    /// <summary>The request with its payment result and any open review case about money allocated to it.</summary>
    private static async Task<IResult> GetAsync(
        Guid id, HttpContext http, ClaimsPrincipal user, PaymentRequestService requests, PaymentMatchingService matching, CancellationToken ct)
    {
        var result = await requests.GetAsync(user.GetTenantId(), Subtenant(http), id, ct);
        if (result.Error is { } failure)
        {
            return ApiErrors.From(failure);
        }

        SetETag(http, result.Value!);
        return Results.Ok(PaymentRequestResponse.From(result.Value!, requests.Now()) with
        {
            OpenReviewCaseIds = await matching.OpenReviewCasesOfRequestAsync(result.Value!.Id, ct),
        });
    }

    private static async Task<IResult> UpdateAsync(
        Guid id, HttpContext http, ClaimsPrincipal user, PaymentRequestService requests, CancellationToken ct)
    {
        var (body, error) = await JsonBody.ReadAsync(http.Request, UpdateFields);
        if (body is null)
        {
            return error!;
        }

        if (ImmutableFields.FirstOrDefault(body.Has) is { } immutable)
        {
            return ApiErrors.Result(StatusCodes.Status400BadRequest, "immutable_field",
                $"{immutable} cannot be changed. Only description and due_at can be edited; to ask for a different payment, "
                + "cancel this request and create a new one.",
                immutable);
        }

        if (!body.TryGetString("description", out var description, out error) || !TryGetDueAt(body, out var dueAt, out error))
        {
            return error!;
        }

        if (!TryReadIfMatch(http, out var version))
        {
            return ApiErrors.From(ServiceError.PreconditionRequired("Send the request's current version in If-Match (the ETag from GET)."));
        }

        var change = new PaymentRequestChange(description, dueAt);
        return ToResponse(http,
            await requests.UpdateAsync(user.GetTenantId(), Subtenant(http), id, version!.Value, change, user.GetActor(), ct), requests);
    }

    private static async Task<IResult> CancelAsync(
        Guid id, HttpContext http, ClaimsPrincipal user, PaymentRequestService requests, CancellationToken ct)
    {
        var (body, error) = await JsonBody.ReadAsync(http.Request, CancelFields, allowEmpty: true);
        if (body is null)
        {
            return error!;
        }

        if (!body.TryGetString("reason", out var reason, out error))
        {
            return error!;
        }

        TryReadIfMatch(http, out var version);
        return ToResponse(http,
            await requests.CancelAsync(user.GetTenantId(), Subtenant(http), id, version, reason.Value, user.GetActor(), ct), requests);
    }

    private static async Task<IResult> InstructionsAsync(
        Guid id, HttpContext http, ClaimsPrincipal user, PaymentRequestService requests, CancellationToken ct)
    {
        // Payment details are private to the payer and payee; keep them out of shared caches.
        http.Response.Headers.CacheControl = "no-store";
        var result = await requests.GetPayableAsync(user.GetTenantId(), Subtenant(http), id, ct);
        return result.Error is { } failure ? ApiErrors.From(failure) : Results.Ok(PaymentInstructionsResponse.From(result.Value!));
    }

    private static async Task<IResult> QrPngAsync(
        Guid id, HttpContext http, ClaimsPrincipal user, PaymentRequestService requests, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        var result = await requests.GetPayableAsync(user.GetTenantId(), Subtenant(http), id, ct);
        if (result.Error is { } failure)
        {
            return ApiErrors.From(failure);
        }

        var request = result.Value!;
        return Results.File(PaymentQrService.RenderPng(request.QrPayload), "image/png", $"payment-qr-{request.VariableSymbol}.png");
    }

    /// <summary>Reads an optional ISO 8601 timestamp with an offset; <c>null</c> clears it.</summary>
    private static bool TryGetDueAt(JsonBody body, out Optional<DateTimeOffset?> value, out IResult? error)
    {
        value = Optional<DateTimeOffset?>.Unset;
        if (!body.TryGetString("due_at", out var text, out error))
        {
            return false;
        }

        if (!text.HasValue)
        {
            return true;
        }

        if (text.Value is null)
        {
            value = new Optional<DateTimeOffset?>(true, null);
            return true;
        }

        if (!PaymentRequestRules.TryParseDueAt(text.Value, out var dueAt))
        {
            error = ApiErrors.Result(StatusCodes.Status400BadRequest, PaymentRequestErrors.InvalidDueAt,
                "due_at must be an ISO 8601 timestamp with an offset, e.g. 2026-10-15T18:00:00+02:00.", "due_at");
            return false;
        }

        value = new Optional<DateTimeOffset?>(true, dueAt);
        return true;
    }

    /// <summary>
    /// <c>false</c> when <c>If-Match</c> is missing. A value other than one of our ETags is read as
    /// version 0, which never matches, so it fails as stale rather than as missing.
    /// </summary>
    private static bool TryReadIfMatch(HttpContext http, out int? version)
    {
        version = null;
        var header = http.Request.Headers[HeaderNames.IfMatch].ToString().Trim();
        if (header.Length == 0)
        {
            return false;
        }

        version = header is ['"', .. var digits, '"']
            && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0;
        return true;
    }

    private static void SetETag(HttpContext http, PaymentRequest request) =>
        http.Response.Headers.ETag = $"\"{request.Version.ToString(CultureInfo.InvariantCulture)}\"";

    private static IResult ToResponse(HttpContext http, ServiceResult<PaymentRequest> result, PaymentRequestService requests)
    {
        if (result.Error is { } failure)
        {
            return ApiErrors.From(failure);
        }

        SetETag(http, result.Value!);
        return Results.Ok(PaymentRequestResponse.From(result.Value!, requests.Now()));
    }

    /// <summary>The child named in the route, or <c>null</c> for the caller's own tenant.</summary>
    private static Guid? Subtenant(HttpContext http) =>
        http.GetRouteValue(SubtenantRouteValue) is string value && Guid.TryParse(value, out var id) ? id : null;
}
