var builder = WebApplication.CreateBuilder(args);

// Aspire service defaults: OpenTelemetry, health checks, service discovery, resilience.
builder.AddServiceDefaults();

// Reverse proxy: in Development, forwards SPA requests to the Vite dev server
// (config in appsettings.Development.json). In prod the SPA is served from wwwroot.
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

var app = builder.Build();

app.MapDefaultEndpoints();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

// TODO(rooms slice): app.MapHub<GameHub>("/hub");

if (app.Environment.IsDevelopment())
{
    // Single origin in dev: proxy everything not handled above to the Vite dev server.
    app.MapReverseProxy();
}
else
{
    // Single origin in prod: serve the built Svelte SPA from wwwroot.
    app.UseDefaultFiles();
    app.UseStaticFiles();
    app.MapFallbackToFile("index.html");
}

app.Run();
