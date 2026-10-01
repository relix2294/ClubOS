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
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using ClubOS.CloudApi.Live;
using ClubOS.CloudApi.Security;

var builder = WebApplication.CreateBuilder(args);

// Конфигурация из env с префиксом CLUBOS_ (напр. CLUBOS_Auth__SigningKey). Секреты — только так.
builder.Configuration.AddEnvironmentVariables("CLUBOS_");

var connectionString = builder.Configuration.GetConnectionString("ClubOs")
    ?? throw new InvalidOperationException("Нет ConnectionStrings:ClubOs (env CLUBOS_ConnectionStrings__ClubOs).");

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<LiveBroker>();
builder.Services.AddSingleton<LiveChangeInterceptor>();
builder.Services.AddHostedService<LivePresenceMonitor>();
builder.Services.AddDbContext<ClubOsDbContext>((sp, o) => o.UseNpgsql(connectionString)
    .AddInterceptors(sp.GetRequiredService<LiveChangeInterceptor>()));
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.Section));
builder.Services.Configure<SeedOptions>(builder.Configuration.GetSection(SeedOptions.Section));
builder.Services.Configure<PkiOptions>(builder.Configuration.GetSection(PkiOptions.Section));
builder.Services.AddSingleton<SigningKeyProvider>();
builder.Services.AddScoped<TokenService>();
builder.Services.AddSingleton<SecretProtector>();
builder.Services.AddScoped<MfaService>();
builder.Services.AddScoped<LocationScope>();
builder.Services.AddHttpContextAccessor();
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
        o.Events = new JwtBearerEvents { OnTokenValidated = StaffTokenValidation.OnTokenValidated };
    });

var authorization = builder.Services.AddAuthorizationBuilder()
    // Любой вошедший сотрудник (в т.ч. с временным паролем) — только /me и смена пароля.
    .AddPolicy(Policies.Staff, p => p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser().RequireClaim(StaffContext.TenantClaim))
    .AddPolicy(Policies.Edge, p => p.AddAuthenticationSchemes(EdgeAuthenticationHandler.SchemeName)
        .RequireAuthenticatedUser().RequireClaim(EdgeContext.EdgeIdClaim));

// Политика на каждое право (ТЗ §8): роль из JWT (сверена с БД в OnTokenValidated) и не временный пароль.
foreach (var permission in ClubOS.CloudApi.Security.Permissions.All)
{
    authorization.AddPolicy(ClubOS.CloudApi.Security.Permissions.Policy(permission), p => p
        .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser()
        .RequireClaim(StaffContext.TenantClaim)
        .RequireAssertion(ctx =>
            !StaffContext.IsRestricted(ctx.User) &&
            ClubOS.CloudApi.Security.Permissions.Has(ctx.User.FindFirst("role")?.Value ?? string.Empty, permission)));
}

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

// За reverse proxy (Caddy на VPS): реальный IP клиента для rate limit и схема https.
// Включать только когда Cloud API недоступен напрямую из интернета (см. infrastructure/vps).
var trustForwardedHeaders = builder.Configuration.GetValue("Proxy:TrustForwardedHeaders", false);
if (trustForwardedHeaders)
{
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
    });
}

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

// Серверные команды обслуживания: dotnet ClubOS.CloudApi.dll admin reset-password <email>
if (AdminCli.IsAdminCommand(args))
{
    return await AdminCli.RunAsync(app.Services, args);
}

if (trustForwardedHeaders)
{
    app.UseForwardedHeaders();
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
app.MapStaffManagementEndpoints();
app.MapEdgeEndpoints();
app.MapLiveEndpoints();
app.MapMfaEndpoints();
app.MapLocationEndpoints();
app.MapRevocationEndpoints();
app.MapCashEndpoints();
app.MapDisklessEndpoints();
app.MapClientEndpoints();
app.MapBookingEndpoints();

await app.RunAsync();
return 0;

/// <summary>Точка входа (partial — для WebApplicationFactory в интеграционных тестах).</summary>
public partial class Program;

internal sealed class DatabaseHealthCheck(ClubOsDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default) =>
        await db.Database.CanConnectAsync(ct)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("PostgreSQL недоступен");
}
