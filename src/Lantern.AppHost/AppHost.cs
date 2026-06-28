var builder = DistributedApplication.CreateBuilder(args);

// Vite dev server (Svelte SPA). In development the server reverse-proxies to it;
// `aspire run` launches it alongside the server.
var web = builder.AddNpmApp("web", "../../web", "dev")
    .WithHttpEndpoint(port: 5173, targetPort: 5173, isProxied: false);

builder.AddProject<Projects.Lantern_Server>("server")
    .WaitFor(web);

builder.Build().Run();
