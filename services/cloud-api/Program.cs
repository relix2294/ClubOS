using System.Text;
using ClubOS.CloudApi.Auth;
using ClubOS.CloudApi.Data;
using ClubOS.CloudApi.Endpoints;
using ClubOS.CloudApi.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// --- Конфигурация ---
var authSection = builder.Configuration.GetSection(AuthOptions.SectionName);
builder.Services.Configure<AuthOptions>(authSection);

var authOptions = authSection.Get<AuthOptions>() ?? new AuthOptions();
// Секрет подписи — из окружения, приоритетнее appsettings (ТЗ §27.3).
var jwtKey = Environment.GetEnvironmentVariable("CLUBOS_JWT_KEY");
if (!string.IsNullOrWhiteSpace(jwtKey))
{
    var signingKeyOverride = jwtKey; // локальная не-null копия для замыкания
    authOptions.SigningKey = signingKeyOverride;
    builder.Services.PostConfigure<AuthOptions>(o => o.SigningKey = signingKeyOverride);
}

var connectionString = Environment.GetEnvironmentVariable("CLUBOS_DB_CONNECTION")
    ?? builder.Configuration.GetConnectionString("ClubOs")
    ?? "Host=localhost;Port=5432;Database=clubos;Username=clubos;Password=clubos_dev";

// --- Сервисы ---
builder.Services.AddDbContext<ClubOsDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddSingleton<DevCertificateAuthority>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Не переименовывать claim-типы на входе (JsonWebTokenHandler по умолчанию
        // мапит "sub" → nameidentifier и т.д.): оставляем "sub"/"org"/"role" как есть,
        // чтобы Caller.From находил их по исходным именам.
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = authOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = authOptions.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(string.IsNullOrWhiteSpace(authOptions.SigningKey)
                    ? new string('0', 32)
                    : authOptions.SigningKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddOpenApi();

var app = builder.Build();

// --- Миграции и dev-seed при старте (ТЗ §25.2.3) ---
if (!IsEfDesignTime())
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ClubOsDbContext>();
    var ownerPassword = Environment.GetEnvironmentVariable("CLUBOS_DEV_OWNER_PASSWORD") ?? "ChangeMe123!";
    await DatabaseBootstrapper.MigrateAndSeedAsync(db, ownerPassword);
}

app.UseAuthentication();
app.UseAuthorization();

app.MapOpenApi();

app.MapGet("/", () => Results.Ok(new
{
    service = "ClubOS Cloud API",
    milestone = "M0",
    openapi = "/openapi/v1.json",
})).AllowAnonymous().ExcludeFromDescription();

app.MapHealthEndpoints();
app.MapAuthEndpoints();
app.MapLocationEndpoints();
app.MapEnrollmentEndpoints();
app.MapSyncEndpoints();
app.MapDeviceEndpoints();
app.MapSessionEndpoints();
app.MapAuditEndpoints();

app.Run();

static bool IsEfDesignTime() =>
    AppDomain.CurrentDomain.GetAssemblies()
        .Any(a => a.GetName().Name == "Microsoft.EntityFrameworkCore.Design");

// Открываем Program для интеграционных тестов (WebApplicationFactory<Program>).
public partial class Program;
