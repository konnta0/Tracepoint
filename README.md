# Tracepoint

English | [日本語](README.ja.md)

Adding `[Trace]` to a C# method weaves a `System.Diagnostics.Activity` span at compile time. Tracepoint does not depend on the OpenTelemetry SDK.

```csharp
using Tracepoint;

public sealed class MatchService
{
    [Trace]
    public async Task<Match> FindMatchAsync([TraceTag("match.region")] string region)
    {
        ...
    }
}
```

```csharp
services
    .AddOpenTelemetry()
    .WithTracing(builder => builder.AddSource(TracepointSources.Name));
```

The default span name is `{TypeName}.{MethodName}`. Pass an explicit name with `[Trace("matchmaking.find")]`. Only parameters marked with `[TraceTag]` are recorded as span attributes.

A woven synchronous method allocates 0 managed bytes when no listener is subscribed. An unhandled exception is recorded with `ActivityStatusCode.Error` and `Activity.AddException`, then rethrown unchanged.

## Diagnostics

| ID | Description |
|---|---|
| TP001 | Unsupported `TraceTag` type. Supported types are `string`, `bool`, `int`, `long`, and `double` |
| TP003 | Trace or tag name that is empty or whitespace |
| TP004 | Unsupported method (generic method, iterator, `async void`, method without a body, local function) |
| TP005 | `TraceTag` on a method that does not have `Trace` |

Set `TracepointWeave` to `false` to disable weaving.

## Benchmark

```bash
dotnet run -c Release --project benchmarks/Tracepoint.Benchmarks
```

Sync and async benchmarks compare `Baseline`, `ManualActivity_*`, and `Tracepoint_*` for `NoListener`, `NotRecorded`, and `Recorded`. `NotRecorded` uses `ActivitySamplingResult.PropagationData`.
