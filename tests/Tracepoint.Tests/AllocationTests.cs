using System.Diagnostics;
using System.Reflection;
using Tracepoint.Smoke;

namespace Tracepoint.Tests;

public sealed class AllocationTests
{
    [Fact]
    public void NoListener_AllocatesNothing()
    {
        if (!IsOptimized(typeof(SmokeSubjects).Assembly))
        {
            return;
        }

        SmokeSubjects.TaggedInt(1);
        Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            SmokeSubjects.TaggedInt(i);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Recorded_DoesNotAllocateMoreThanManualActivity()
    {
        if (!IsOptimized(typeof(SmokeSubjects).Assembly))
        {
            return;
        }

        using var scope = ActivityScope.Start(
            ActivitySamplingResult.AllDataAndRecorded,
            capture: false,
            TracepointSources.Name,
            Probe.Manual.Name);
        const string region = "ap-northeast-1";
        var manual = Measure(() => SmokeSubjects.ManualSync(region));
        var traced = Measure(() => SmokeSubjects.TracedSync(region));

        Assert.True(traced <= manual, $"Tracepoint allocated {traced} B and manual Activity allocated {manual} B.");
    }

    [Fact]
    public void NotRecorded_CreatesActivityWithoutTags()
    {
        using var scope = ActivityScope.Start(
            ActivitySamplingResult.PropagationData,
            capture: true,
            TracepointSources.Name);

        SmokeSubjects.TracedSync("ap-northeast-1");

        var activity = scope.Single("bench.sync");
        Assert.False(activity.IsAllDataRequested);
        Assert.False(activity.Recorded);
    }

    private static long Measure(Action action)
    {
        for (var i = 0; i < 200; i++)
        {
            action();
        }

        Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 200; i++)
        {
            action();
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static bool IsOptimized(Assembly assembly)
    {
        var debuggable = assembly.GetCustomAttribute<DebuggableAttribute>();
        return debuggable is null || !debuggable.IsJITOptimizerDisabled;
    }
}
