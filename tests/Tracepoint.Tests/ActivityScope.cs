using System.Diagnostics;

namespace Tracepoint.Tests;

internal sealed class ActivityScope : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly List<Activity> _stopped = [];

    private ActivityScope(ActivityListener listener)
    {
        _listener = listener;
    }

    internal IReadOnlyList<Activity> Stopped
    {
        get
        {
            lock (_stopped)
            {
                return _stopped.ToArray();
            }
        }
    }

    internal static ActivityScope Start(ActivitySamplingResult result, bool capture, params string[] sourceNames)
    {
        var names = new HashSet<string>(sourceNames, StringComparer.Ordinal);
        var scope = new ActivityScope(new ActivityListener());
        scope._listener.ShouldListenTo = source => names.Contains(source.Name);
        scope._listener.Sample = (ref ActivityCreationOptions<ActivityContext> _) => result;
        scope._listener.SampleUsingParentId = (ref ActivityCreationOptions<string> _) => result;
        scope._listener.ActivityStopped = capture
            ? activity =>
            {
                lock (scope._stopped)
                {
                    scope._stopped.Add(activity);
                }
            }
            : static _ => { };
        ActivitySource.AddActivityListener(scope._listener);
        return scope;
    }

    internal Activity Single(string name)
    {
        var matches = Stopped.Where(activity => activity.OperationName == name || activity.DisplayName == name).ToArray();
        Assert.Single(matches);
        return matches[0];
    }

    public void Dispose() => _listener.Dispose();
}
