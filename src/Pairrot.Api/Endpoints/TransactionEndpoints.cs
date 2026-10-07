using System.Globalization;
using System.Security.Claims;
using Pairrot.Api.Auth;
using Pairrot.Api.Http;
using Pairrot.Core.Transactions;
using Pairrot.Infrastructure.Tenants;
using Pairrot.Infrastructure.Transactions;

namespace Pairrot.Api.Endpoints;

/// <summary>
/// Incoming bank transactions recorded from notifications (Feature 007), under <c>/v1/tenant</c>
/// and <c>/v1/subtenants/{subtenantId}</c>. Read-only: no endpoint trusts a source, merges
/// transactions, or marks a payment paid.
/// </summary>
public static class TransactionEndpoints
{
    private const string SubtenantRouteValue = "subtenantId";

    private static readonly Dictionary<string, IngestionStatus> Statuses = Enum.GetValues<IngestionStatus>()
        .ToDictionary(InboundMessageResponse.Snake);

    public static void MapTenantTransactions(this RouteGroupBuilder tenantGroup) =>
        Map(tenantGroup.MapGroup("/transactions"));

    public static void MapSubtenantTransactions(this RouteGroupBuilder subtenantGroup) =>
        Map(subtenantGroup.MapGroup($"/{{{SubtenantRouteValue}:guid}}/transactions"));

    private static void Map(RouteGroupBuilder group)
    {
        group.MapGet("", ListAsync);
        group.MapGet("/{id:guid}", GetAsync);
    }

    private static async Task<IResult> ListAsync(
        HttpContext http, ClaimsPrincipal user, IncomingTransactionService transactions, CancellationToken ct)
    {
        var query = http.Request.Query;
        if (!Paging.TryRead(query, TenantService.DefaultPageSize, TenantService.MaxPageSize, out var limit, out var cursor, out var error))
        {
            return error!;
        }

        var filter = new TransactionFilter();
        if (query.TryGetValue("bank_connection_id", out var connectionValue))
        {
            if (!Guid.TryParse(connectionValue, out var connectionId))
            {
                return ApiErrors.Validation("bank_connection_id must be a UUID.");
            }

            filter = filter with { BankConnectionId = connectionId };
        }

        if (!TryGetDate(query, "booked_from", out var from, out error) || !TryGetDate(query, "booked_to", out var to, out error))
        {
            return error!;
        }

        filter = filter with { BookedFrom = from, BookedTo = to };

        if (query.TryGetValue("amount", out var amountValue))
        {
            if (!long.TryParse(amountValue, NumberStyles.None, CultureInfo.InvariantCulture, out var amount) || amount <= 0)
            {
                return ApiErrors.Validation("amount must be a positive integer in minor units.");
            }

            filter = filter with { Amount = amount };
        }

        if (query.TryGetValue("variable_symbol", out var symbolValue))
        {
            var symbol = symbolValue.ToString();
            if (symbol.Length is < 1 or > 10 || !symbol.All(char.IsAsciiDigit))
            {
                return ApiErrors.Validation("variable_symbol must be 1 to 10 digits.");
            }

            filter = filter with { VariableSymbol = symbol };
        }

        if (query.TryGetValue("ingestion_status", out var statusValue))
        {
            if (!Statuses.TryGetValue(statusValue.ToString(), out var status))
            {
                return ApiErrors.Validation($"ingestion_status must be one of: {string.Join(", ", Statuses.Keys)}.");
            }

            filter = filter with { IngestionStatus = status };
        }

        var result = await transactions.ListAsync(user.GetTenantId(), Subtenant(http), filter, limit, cursor, ct);
        return result.Error is { } failure
            ? ApiErrors.From(failure)
            : Results.Ok(new ListResponse<IncomingTransactionResponse>(
                result.Value!.Items.Select(t => IncomingTransactionResponse.From(t)).ToList(), result.Value.NextCursor));
    }

    private static async Task<IResult> GetAsync(
        Guid id, HttpContext http, ClaimsPrincipal user, IncomingTransactionService transactions, CancellationToken ct)
    {
        var result = await transactions.GetAsync(user.GetTenantId(), Subtenant(http), id, ct);
        return result.Error is { } failure
            ? ApiErrors.From(failure)
            : Results.Ok(IncomingTransactionResponse.From(result.Value!.Transaction, result.Value.Sources));
    }

    private static bool TryGetDate(IQueryCollection query, string name, out DateOnly? value, out IResult? error)
    {
        value = null;
        error = null;
        if (!query.TryGetValue(name, out var text))
        {
            return true;
        }

        if (!DateOnly.TryParseExact(text.ToString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            error = ApiErrors.Validation($"{name} must be a date in the form YYYY-MM-DD.");
            return false;
        }

        value = date;
        return true;
    }

    private static Guid? Subtenant(HttpContext http) =>
        http.GetRouteValue(SubtenantRouteValue) is string value && Guid.TryParse(value, out var id) ? id : null;
}
