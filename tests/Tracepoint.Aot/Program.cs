using System.Diagnostics;
using Tracepoint;

internal static class Program
{
    private static async Task<int> Main()
    {
        if (Work.Add(2, 3) != 5)
        {
            Console.Error.WriteLine("no-listener Add returned an unexpected value");
            return 1;
        }

        var names = new List<string>();
        var tags = new List<string>();
        var errors = 0;
        using var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == TracepointSources.Name,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                names.Add(activity.OperationName);
                if (activity.Status == ActivityStatusCode.Error)
                {
                    errors++;
                }

                foreach (var tag in activity.TagObjects)
                {
                    tags.Add(tag.Key + "=" + tag.Value);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        if (Work.Add(4, 6) != 10)
        {
            Console.Error.WriteLine("recorded Add returned an unexpected value");
            return 1;
        }

        if (await Work.DelayAsync("jp").ConfigureAwait(false) != "jp")
        {
            Console.Error.WriteLine("recorded DelayAsync returned an unexpected value");
            return 1;
        }

        try
        {
            Work.Boom();
            Console.Error.WriteLine("Boom did not throw");
            return 1;
        }
        catch (InvalidOperationException exception) when (exception.Message == "aot-boom")
        {
        }

        if (!names.Contains("Work.Add") ||
            !names.Contains("aot.delay") ||
            !names.Contains("Work.Boom"))
        {
            Console.Error.WriteLine("missing Tracepoint spans: " + string.Join(", ", names));
            return 1;
        }

        if (!tags.Contains("left=4") || !tags.Contains("match.region=jp"))
        {
            Console.Error.WriteLine("missing TraceTag values: " + string.Join(", ", tags));
            return 1;
        }

        if (errors != 1)
        {
            Console.Error.WriteLine("expected one error span, found " + errors);
            return 1;
        }

        Console.WriteLine("Tracepoint Native AOT: ok");
        return 0;
    }
}

internal static class Work
{
    [Trace]
    internal static int Add([TraceTag("left")] int left, [TraceTag("right")] int right) => left + right;

    [Trace("aot.delay")]
    internal static async Task<string> DelayAsync([TraceTag("match.region")] string region)
    {
        await Task.Yield();
        return region;
    }

    [Trace]
    internal static void Boom() => throw new InvalidOperationException("aot-boom");
}
