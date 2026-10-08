using OpenTelemetry;
using OpenTelemetry.Trace;
using Tracepoint;
using Tracepoint.Sample.Api;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<MatchService>();
builder.Services.AddSingleton<FailingWork>();

builder.Services
    .AddOpenTelemetry()
    .WithTracing(static tracing =>
    {
        tracing
            .SetSampler(new AlwaysOnSampler())
            .AddSource(TracepointSources.Name)
            .AddAspNetCoreInstrumentation(static options =>
            {
                options.Filter = static context =>
                    !context.Request.Path.StartsWithSegments("/health")
                    && !context.Request.Path.StartsWithSegments("/alive");
            });
    });

if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    builder.Services.AddOpenTelemetry().UseOtlpExporter();
}

var app = builder.Build();

app.MapGet("/health", static () => Results.Ok());
app.MapGet("/alive", static () => Results.Ok());

app.MapGet("/match", static async (string region, MatchService matches) =>
{
    var match = await matches.FindMatchAsync(region).ConfigureAwait(false);
    return Results.Ok(match);
});

app.MapGet("/fail", static (FailingWork work) => work.Throw());

app.Run();
