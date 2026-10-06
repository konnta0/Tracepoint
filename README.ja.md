# Tracepoint

[English](README.md) | 日本語

C# メソッドに `[Trace]` を付けると、`System.Diagnostics.Activity` の span をコンパイル時に織り込みます。OpenTelemetry SDK には依存しません。

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

span 名の既定値は `{TypeName}.{MethodName}` です。`[Trace("matchmaking.find")]` で明示できます。`[TraceTag]` を付けた引数だけが span attribute になります。

listener が無いとき、織り込まれた同期メソッドの managed allocation は 0 です。未処理例外は `ActivityStatusCode.Error` と `Activity.AddException` で記録し、例外はそのまま再送出します。

## Diagnostics

| ID | 内容 |
|---|---|
| TP001 | 未対応の `TraceTag` 型。対応は `string`、`bool`、`int`、`long`、`double` |
| TP003 | 空白だけの trace 名または tag 名 |
| TP004 | 未対応メソッド（generic method、iterator、`async void`、本体なし、local function） |
| TP005 | `[Trace]` のないメソッドへの `TraceTag` |

`TracepointWeave` を `false` にすると織り込みを止めます。

## Benchmark

```bash
dotnet run -c Release --project benchmarks/Tracepoint.Benchmarks
```

同期と async のそれぞれで、`Baseline`、`ManualActivity_*`、`Tracepoint_*`（`NoListener` / `NotRecorded` / `Recorded`）を比較します。`NotRecorded` は `ActivitySamplingResult.PropagationData` です。
