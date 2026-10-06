using System.Diagnostics;
using Tracepoint;

namespace Tracepoint.Smoke;

/// <summary>
/// Signals read by behavioral tests while a traced method is running.
/// </summary>
public static class Probe
{
    /// <summary>Child source started inside a traced method.</summary>
    public static readonly ActivitySource Child = new("Tracepoint.Test.Child", "0");

    /// <summary>Parent source that stands in for an ASP.NET Core request.</summary>
    public static readonly ActivitySource Parent = new("Tracepoint.Test.Parent", "0");

    /// <summary>Hand-written activity source used as the allocation baseline.</summary>
    public static readonly ActivitySource Manual = new("Tracepoint.Test.Manual", "0");

    /// <summary>Gets or sets the current activity at the start of an async method.</summary>
    public static Activity? During { get; set; }

    /// <summary>Gets or sets the current activity after an await.</summary>
    public static Activity? AfterAwait { get; set; }

    /// <summary>Gets or sets the parent of a child activity created inside a traced method.</summary>
    public static Activity? InnerParent { get; set; }

    /// <summary>Gets or sets how many times a user finally block ran.</summary>
    public static int FinallyCount { get; set; }

    /// <summary>Gets or sets a value observed inside a traced method.</summary>
    public static string? RegionSeen { get; set; }

    /// <summary>Clears signals between tests.</summary>
    public static void Reset()
    {
        During = null;
        AfterAwait = null;
        InnerParent = null;
        FinallyCount = 0;
        RegionSeen = null;
    }
}

/// <summary>Methods exercised by the Tracepoint tests.</summary>
public static class SmokeSubjects
{
    /// <summary>Untraced empty method.</summary>
    public static void Empty()
    {
    }

    /// <summary>Traced method with no tags.</summary>
    [Trace]
    public static void SyncNoTags()
    {
    }

    /// <summary>Traced method with an integer tag, used to prove the no-listener path does not box.</summary>
    [Trace]
    public static void TaggedInt([TraceTag] int value)
    {
        _ = value;
    }

    /// <summary>Records only parameters marked with <see cref="TraceTagAttribute"/>.</summary>
    [Trace]
    public static void Tags([TraceTag] string region, [TraceTag("match.mode")] string mode, Guid userId)
    {
        _ = userId;
        _ = mode;
        _ = region;
    }

    /// <summary>Returns the input unchanged.</summary>
    [Trace]
    public static int SyncReturn(int value) => value;

    /// <summary>Throws an unhandled exception.</summary>
    [Trace]
    public static void SyncThrow() => throw new InvalidOperationException("boom");

    /// <summary>Swallows an exception inside the method.</summary>
    [Trace]
    public static void Swallow()
    {
        try
        {
            throw new InvalidOperationException("swallowed");
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>Rethrows an exception from a nested catch.</summary>
    [Trace]
    public static void Rethrow()
    {
        try
        {
            throw new InvalidOperationException("rethrow");
        }
        catch (InvalidOperationException)
        {
            throw;
        }
    }

    /// <summary>Returns from a catch after handling the exception.</summary>
    [Trace]
    public static int CatchReturn()
    {
        try
        {
            throw new InvalidOperationException("caught");
        }
        catch (InvalidOperationException)
        {
            return 4;
        }
    }

    /// <summary>Runs a user finally block and then returns.</summary>
    [Trace]
    public static int WithFinally()
    {
        try
        {
            return 1;
        }
        finally
        {
            Probe.FinallyCount++;
        }
    }

    /// <summary>Application span that stays current across an await.</summary>
    [Trace("matchmaking.find")]
    public static async Task<string> FindMatchAsync([TraceTag("match.region")] string region)
    {
        Probe.RegionSeen = region;
        Probe.During = Activity.Current;
        await Task.Yield();
        Probe.AfterAwait = Activity.Current;
        using var child = Probe.Child.StartActivity("child");
        Probe.InnerParent = child?.Parent;
        return region;
    }

    /// <summary>Throws after an await.</summary>
    [Trace]
    public static async Task AsyncThrow()
    {
        await Task.Yield();
        throw new InvalidOperationException("async-boom");
    }

    /// <summary>Completes on a yielded continuation.</summary>
    [Trace]
    public static async Task TaskAsync()
    {
        Probe.During = Activity.Current;
        await Task.Yield();
        Probe.AfterAwait = Activity.Current;
    }

    /// <summary>Returns a value from an async method.</summary>
    [Trace]
    public static async Task<int> TaskOfTAsync(int value)
    {
        await Task.CompletedTask;
        return value;
    }

    /// <summary>ValueTask method that yields.</summary>
    [Trace]
    public static async ValueTask ValueAsync()
    {
        Probe.During = Activity.Current;
        await Task.Yield();
        Probe.AfterAwait = Activity.Current;
    }

    /// <summary>ValueTask method that returns a tagged integer.</summary>
    [Trace]
    public static async ValueTask<int> ValueOfTAsync([TraceTag] int value)
    {
        await Task.CompletedTask;
        return value;
    }

    /// <summary>Non-async method that returns a task.</summary>
    [Trace]
    public static Task<int> NonAsyncTask() => Task.FromResult(3);

    /// <summary>Non-async method that returns a value task.</summary>
    [Trace]
    public static ValueTask<int> NonAsyncValue(int value) => new(value);

    /// <summary>Hand-written activity used as the recorded-path baseline.</summary>
    public static void ManualSync(string region)
    {
        using var activity = Probe.Manual.StartActivity("bench.sync");
        if (activity is not null)
        {
            activity.SetTag("region", region);
        }
    }

    /// <summary>Woven equivalent of <see cref="ManualSync"/>.</summary>
    [Trace("bench.sync")]
    public static void TracedSync([TraceTag("region")] string region)
    {
        _ = region;
    }
}

/// <summary>Instance service used to check the default span name.</summary>
public sealed class MatchService
{
    /// <summary>Async instance method.</summary>
    [Trace]
    public async Task<int> FindMatchAsync([TraceTag("match.region")] string region)
    {
        Probe.During = Activity.Current;
        await Task.Yield();
        Probe.AfterAwait = Activity.Current;
        return region.Length;
    }
}

/// <summary>Nested type used to check span names.</summary>
public static class Outer
{
    /// <summary>Nested type.</summary>
    public static class Inner
    {
        /// <summary>Traced nested method.</summary>
        [Trace]
        public static void Run()
        {
        }
    }
}

/// <summary>Generic declaring type with a non-generic traced method.</summary>
/// <typeparam name="T">The value type.</typeparam>
public sealed class Box<T>
{
    /// <summary>Returns <paramref name="value"/> and tags <paramref name="label"/>.</summary>
    [Trace]
    public T Pass([TraceTag] string label, T value)
    {
        _ = label;
        return value;
    }

    /// <summary>Returns <paramref name="value"/> after an await.</summary>
    [Trace]
    public async Task<T> PassAsync(T value)
    {
        await Task.Yield();
        return value;
    }
}

/// <summary>Struct instance method used to check argument indexes.</summary>
public struct Point
{
    /// <summary>Gets or sets the point position.</summary>
    public int X { get; set; }

    /// <summary>Adds <paramref name="delta"/> to <see cref="X"/>.</summary>
    [Trace]
    public int Offset([TraceTag("delta")] int delta) => X + delta;
}
