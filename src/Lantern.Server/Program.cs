var builder = WebApplication.CreateBuilder(args);

// Aspire service defaults: OpenTelemetry, health checks, service discovery, resilience.
builder.AddServiceDefaults();

var app = builder.Build();

app.MapDefaultEndpoints();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

// TODO(rooms slice): app.MapHub<GameHub>("/hub");

// In production the server is the single origin and serves the built Svelte SPA.
// In development the Vite dev server serves the SPA and proxies /hub + /api here.
if (!app.Environment.IsDevelopment())
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
    app.MapFallbackToFile("index.html");
}

app.Run();
