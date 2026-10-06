using System.Diagnostics;
using BenchmarkDotNet.Attributes;

namespace Tracepoint.Benchmarks;

[MemoryDiagnoser]
public class SyncBenchmarks
{
    private const string Region = "ap-northeast-1";

    [GlobalSetup(Targets = [nameof(ManualActivity_NotRecorded), nameof(Tracepoint_NotRecorded)])]
    public void SetupNotRecorded() => Bench.Listen(ActivitySamplingResult.PropagationData);

    [GlobalSetup(Targets = [nameof(ManualActivity_Recorded), nameof(Tracepoint_Recorded)])]
    public void SetupRecorded() => Bench.Listen(ActivitySamplingResult.AllDataAndRecorded);

    [GlobalCleanup(Targets = [nameof(ManualActivity_NotRecorded), nameof(Tracepoint_NotRecorded), nameof(ManualActivity_Recorded), nameof(Tracepoint_Recorded)])]
    public void Cleanup() => Bench.Stop();

    [Benchmark(Baseline = true)]
    public void Baseline() => BenchSubjects.Empty();

    [Benchmark]
    public void ManualActivity_NoListener() => BenchSubjects.ManualSync(Region);

    [Benchmark]
    public void Tracepoint_NoListener() => BenchSubjects.TracedSync(Region);

    [Benchmark]
    public void ManualActivity_NotRecorded() => BenchSubjects.ManualSync(Region);

    [Benchmark]
    public void Tracepoint_NotRecorded() => BenchSubjects.TracedSync(Region);

    [Benchmark]
    public void ManualActivity_Recorded() => BenchSubjects.ManualSync(Region);

    [Benchmark]
    public void Tracepoint_Recorded() => BenchSubjects.TracedSync(Region);
}
