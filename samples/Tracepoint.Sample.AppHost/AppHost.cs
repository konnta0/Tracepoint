var builder = DistributedApplication.CreateBuilder(args);

var collector = builder
    .AddOpenTelemetryCollector("otelcol")
    .WithConfig(Path.Combine(builder.AppHostDirectory, "otel-collector-config.yaml"));

builder.AddProject<Projects.Tracepoint_Sample_Api>("api")
    .WithOpenTelemetryCollectorRouting(collector);

builder.Build().Run();
