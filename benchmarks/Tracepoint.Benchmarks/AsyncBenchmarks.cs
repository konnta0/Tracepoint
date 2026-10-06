using System.Diagnostics;
using BenchmarkDotNet.Attributes;

namespace Tracepoint.Benchmarks;

[MemoryDiagnoser]
public class AsyncBenchmarks
{
    private const string Region = "ap-northeast-1";

    [GlobalSetup(Targets = [nameof(ManualActivity_NotRecorded), nameof(Tracepoint_NotRecorded)])]
    public void SetupNotRecorded() => Bench.Listen(ActivitySamplingResult.PropagationData);

    [GlobalSetup(Targets = [nameof(ManualActivity_Recorded), nameof(Tracepoint_Recorded)])]
    public void SetupRecorded() => Bench.Listen(ActivitySamplingResult.AllDataAndRecorded);

    [GlobalCleanup(Targets = [nameof(ManualActivity_NotRecorded), nameof(Tracepoint_NotRecorded), nameof(ManualActivity_Recorded), nameof(Tracepoint_Recorded)])]
    public void Cleanup() => Bench.Stop();

    [Benchmark(Baseline = true)]
    public Task Baseline() => BenchSubjects.EmptyAsync();

    [Benchmark]
    public Task ManualActivity_NoListener() => BenchSubjects.ManualAsync(Region);

    [Benchmark]
    public Task Tracepoint_NoListener() => BenchSubjects.TracedAsync(Region);

    [Benchmark]
    public Task ManualActivity_NotRecorded() => BenchSubjects.ManualAsync(Region);

    [Benchmark]
    public Task Tracepoint_NotRecorded() => BenchSubjects.TracedAsync(Region);

    [Benchmark]
    public Task ManualActivity_Recorded() => BenchSubjects.ManualAsync(Region);

    [Benchmark]
    public Task Tracepoint_Recorded() => BenchSubjects.TracedAsync(Region);
}
