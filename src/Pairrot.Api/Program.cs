using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Pairrot.Api.Auth;
using Pairrot.Api.Endpoints;
using Pairrot.Api.Http;
using Pairrot.Infrastructure;
using Pairrot.Infrastructure.Persistence;
using Pairrot.Infrastructure.Users;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddPairrotInfrastructure(builder.Configuration);
builder.Services.AddPairrotBackgroundWorker(builder.Configuration);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
});

// Cookie-protection keys live in PostgreSQL, so sessions survive application restarts.
builder.Services.AddDataProtection()
    .SetApplicationName("Pairrot")
    .PersistKeysToDbContext<PairrotDbContext>();

builder.Services.AddPairrotAuthentication();
builder.Services.AddAuthorization(PairrotClaims.Policies.Configure);

builder.Services.AddRateLimiter(options =>
{
    options.OnRejected = (context, _) => new ValueTask(ApiErrors.WriteAsync(context.HttpContext,
        StatusCodes.Status429TooManyRequests, "too_many_requests", "Too many login attempts; try again later."));

    options.AddPolicy(AuthEndpoints.LoginRateLimitPolicy, http =>
    {
        var auth = http.RequestServices.GetRequiredService<IOptions<PairrotAuthOptions>>().Value;
        return RateLimitPartition.GetFixedWindowLimiter(
            http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = auth.LoginRateLimitPermits,
                Window = auth.LoginRateLimitWindow,
                QueueLimit = 0,
            });
    });
});

builder.Services.AddOpenApi();

var app = builder.Build();

app.UseExceptionHandler(error => error.Run(context =>
    ApiErrors.WriteAsync(context, StatusCodes.Status500InternalServerError, "internal_error", "An unexpected error occurred.")));

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseRouting();
app.UseAuthentication();
app.UseRateLimiter();
app.UseBrowserAntiforgery();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapAuthEndpoints();
app.MapTenantEndpoints();
app.MapSubtenantEndpoints();
app.MapWebhookEndpoints();

app.Run();
