var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.Lantern_Server>("server");

builder.Build().Run();
