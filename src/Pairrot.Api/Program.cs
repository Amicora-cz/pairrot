using System.Text.Json;
using System.Text.Json.Serialization;
using Pairrot.Api.Auth;
using Pairrot.Api.Endpoints;
using Pairrot.Api.Http;
using Pairrot.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddPairrotInfrastructure(builder.Configuration);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
});

builder.Services
    .AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
        ApiKeyAuthenticationHandler.SchemeName, null);
builder.Services.AddAuthorization(PairrotClaims.Policies.Configure);

builder.Services.AddOpenApi();

var app = builder.Build();

app.UseExceptionHandler(error => error.Run(context =>
    ApiErrors.WriteAsync(context, StatusCodes.Status500InternalServerError, "internal_error", "An unexpected error occurred.")));

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapTenantEndpoints();
app.MapSubtenantEndpoints();

app.Run();
