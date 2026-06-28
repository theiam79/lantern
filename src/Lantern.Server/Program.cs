var builder = WebApplication.CreateBuilder(args);

// Aspire service defaults: OpenTelemetry, health checks, service discovery, resilience.
builder.AddServiceDefaults();

var app = builder.Build();

// /health + /alive (mapped only in development by the default helper).
app.MapDefaultEndpoints();

app.MapGet("/", () => "Lantern server — OK");

app.Run();
