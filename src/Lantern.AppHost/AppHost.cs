var builder = DistributedApplication.CreateBuilder(args);

var server = builder.AddProject<Projects.Lantern_Server>("server");

// Vite dev server (Svelte SPA), via Aspire.Hosting.JavaScript. In dev it serves the
// browser (native HMR) and proxies /hub + /api to the server; for publish it builds
// the static SPA the server serves from wwwroot. Managed port (not pinned).
builder.AddViteApp("web", "../../web")
    .WithPnpm()
    .WithReference(server)
    .WaitFor(server)
    .WithEnvironment("VITE_SERVER_URL", server.GetEndpoint("http"))
    .WithEnvironment("BROWSER", "none");

builder.Build().Run();
