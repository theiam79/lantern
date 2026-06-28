using System.Text.Json;
using Lantern.Server.Content;
using Lantern.Server.Persistence;
using Lantern.Server.Rooms;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Aspire service defaults: OpenTelemetry, health checks, service discovery, resilience.
builder.AddServiceDefaults();

// Persistence: SQLite (WAL set at startup). Single file; one writer per room via RoomActor.
var dbPath = builder.Configuration["Lantern:DbPath"] ?? "lantern.db";
builder.Services.AddDbContext<LanternDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));

builder.Services.AddSingleton<ContentService>();
builder.Services.AddSingleton<IGameRoomService, GameRoomService>();
builder
    .Services.AddSignalR()
    .AddJsonProtocol(o =>
    {
        o.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        o.PayloadSerializerOptions.PropertyNameCaseInsensitive = true;
        o.PayloadSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    });

var app = builder.Build();

// Schema + WAL, then warm content at startup.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<LanternDbContext>();
    await db.Database.EnsureCreatedAsync();
    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
}
_ = app.Services.GetRequiredService<ContentService>();

app.MapDefaultEndpoints();
app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
app.MapHub<GameHubV1>("/hub/v1");

// In production the server is the single origin and serves the built Svelte SPA.
// In development the Vite dev server serves the SPA and proxies /hub + /api here.
if (!app.Environment.IsDevelopment())
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
    app.MapFallbackToFile("index.html");
}

app.Run();
