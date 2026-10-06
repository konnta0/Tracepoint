using System.Diagnostics;
using Tracepoint;

namespace Tracepoint.Benchmarks;

internal static class Bench
{
    internal const string ManualSourceName = "Tracepoint.Bench.Manual";
    internal static readonly ActivitySource Manual = new(ManualSourceName, "0");

    internal static void Listen(ActivitySamplingResult result)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name is TracepointSources.Name or ManualSourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => result,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => result,
            ActivityStopped = static _ => { },
        };
        ActivitySource.AddActivityListener(listener);
        Listener = listener;
    }

    internal static void Stop()
    {
        Listener?.Dispose();
        Listener = null;
    }

    private static ActivityListener? Listener { get; set; }
}

internal static class BenchSubjects
{
    public static void Empty()
    {
    }

    public static void ManualSync(string region)
    {
        using var activity = Bench.Manual.StartActivity("bench.sync");
        if (activity is not null)
        {
            activity.SetTag("region", region);
        }
    }

    [Trace("bench.sync")]
    public static void TracedSync([TraceTag("region")] string region)
    {
        _ = region;
    }

    public static Task EmptyAsync() => Task.CompletedTask;

    public static async Task ManualAsync(string region)
    {
        using var activity = Bench.Manual.StartActivity("bench.async");
        if (activity is not null)
        {
            activity.SetTag("region", region);
        }

        await Task.CompletedTask;
    }

    [Trace("bench.async")]
    public static async Task TracedAsync([TraceTag("region")] string region)
    {
        _ = region;
        await Task.CompletedTask;
    }
}
