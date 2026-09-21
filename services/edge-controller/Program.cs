using ClubOS.EdgeController.Config;
using ClubOS.EdgeController.Data;
using ClubOS.EdgeController.Endpoints;
using ClubOS.EdgeController.Sessions;
using ClubOS.EdgeController.Sync;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// --- Конфигурация Edge ---
var section = builder.Configuration.GetSection(EdgeOptions.SectionName);
builder.Services.Configure<EdgeOptions>(section);

var opt = section.Get<EdgeOptions>() ?? new EdgeOptions();
var cloudToken = Environment.GetEnvironmentVariable("EDGE_CLOUD_TOKEN") ?? opt.CloudToken;
var dbPath = Environment.GetEnvironmentVariable("EDGE_DB_PATH") ?? opt.DatabasePath;
var cloudUrl = Environment.GetEnvironmentVariable("EDGE_CLOUD_URL") ?? opt.CloudBaseUrl;

builder.Services.PostConfigure<EdgeOptions>(o =>
{
    o.CloudToken = cloudToken;
    o.DatabasePath = dbPath;
    o.CloudBaseUrl = cloudUrl;
});

// Каталог локальной БД должен существовать до открытия SQLite.
var dbDirectory = Path.GetDirectoryName(Path.GetFullPath(dbPath));
if (!string.IsNullOrEmpty(dbDirectory))
{
    Directory.CreateDirectory(dbDirectory);
}

// --- Сервисы ---
builder.Services.AddDbContext<EdgeDbContext>(options => options.UseSqlite($"Data Source={dbPath}"));
builder.Services.AddScoped<SessionService>();
builder.Services.AddHttpClient<CloudSyncClient>(client => client.BaseAddress = new Uri(cloudUrl));
builder.Services.AddHostedService<OutboxSyncService>();
builder.Services.AddOpenApi();

var app = builder.Build();

// --- Инициализация SQLite (WAL) ---
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<EdgeDbContext>();
    await db.Database.EnsureCreatedAsync();
    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
}

app.MapOpenApi();

app.MapGet("/", () => Results.Ok(new
{
    service = "ClubOS Edge Controller",
    milestone = "M0",
    openapi = "/openapi/v1.json",
})).ExcludeFromDescription();

app.MapEdgeEndpoints();

app.Run();
