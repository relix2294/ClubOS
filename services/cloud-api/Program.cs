using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using ClubOS.CloudApi.Api;
using ClubOS.CloudApi.Auth;
using ClubOS.CloudApi.Data;
using ClubOS.CloudApi.Edges;
using ClubOS.CloudApi.Infrastructure;
using ClubOS.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Конфигурация из env с префиксом CLUBOS_ (напр. CLUBOS_Auth__SigningKey). Секреты — только так.
builder.Configuration.AddEnvironmentVariables("CLUBOS_");

var connectionString = builder.Configuration.GetConnectionString("ClubOs")
    ?? throw new InvalidOperationException("Нет ConnectionStrings:ClubOs (env CLUBOS_ConnectionStrings__ClubOs).");

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddDbContext<ClubOsDbContext>(o => o.UseNpgsql(connectionString));
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.Section));
builder.Services.Configure<SeedOptions>(builder.Configuration.GetSection(SeedOptions.Section));
builder.Services.AddSingleton<SigningKeyProvider>();
builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<AuditWriter>();
builder.Services.AddScoped<SyncIngestor>();
builder.Services.AddSingleton<CommandExpiryService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CommandExpiryService>());

builder.Services.AddSingleton(sp =>
{
    var path = builder.Configuration["DevCa:Path"] ?? Path.Combine(builder.Environment.ContentRootPath, "data", "dev-ca");
    return DevCertificateAuthority.LoadOrCreate(path, sp.GetRequiredService<TimeProvider>());
});
builder.Services.AddSingleton<CloudTokenValidator>();

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
});

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer()
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, EdgeAuthenticationHandler>(
        EdgeAuthenticationHandler.SchemeName, null);

builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<AuthOptions>, SigningKeyProvider>((o, auth, keys) =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
        {
            ValidIssuer = auth.Value.Issuer,
            ValidAudience = auth.Value.Audience,
            IssuerSigningKey = keys.Key,
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "sub",
            RoleClaimType = "role"
        };
    });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Policies.Staff, p => p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser().RequireClaim(StaffContext.TenantClaim))
    .AddPolicy(Policies.Owner, p => p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser().RequireRole(ClubOS.CloudApi.Domain.Roles.Owner))
    .AddPolicy(Policies.Edge, p => p.AddAuthenticationSchemes(EdgeAuthenticationHandler.SchemeName)
        .RequireAuthenticatedUser().RequireClaim(EdgeContext.EdgeIdClaim));

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(RateLimits.Auth, http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = builder.Configuration.GetValue("RateLimits:AuthPerMinute", 20),
            Window = TimeSpan.FromMinutes(1)
        }));
});

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);

var app = builder.Build();

if (builder.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<ClubOsDbContext>();
    await db.Database.MigrateAsync();

    var seed = scope.ServiceProvider.GetRequiredService<IOptions<SeedOptions>>().Value;
    if (seed.Enabled)
    {
        await DevSeeder.SeedAsync(db, seed, TimeProvider.System, app.Logger);
    }
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapOpenApi();
app.UseSwaggerUI(o =>
{
    o.SwaggerEndpoint("/openapi/v1.json", "ClubOS Cloud API v1");
    o.RoutePrefix = "swagger";
});

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });

app.MapAuthEndpoints();
app.MapStaffEndpoints();
app.MapEdgeEndpoints();

app.Run();

/// <summary>Точка входа (partial — для WebApplicationFactory в интеграционных тестах).</summary>
public partial class Program;

internal sealed class DatabaseHealthCheck(ClubOsDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default) =>
        await db.Database.CanConnectAsync(ct)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("PostgreSQL недоступен");
}
