using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;

namespace Tracepoint;

/// <summary>
/// The shared <see cref="ActivitySource"/> used by methods marked with <see cref="TraceAttribute"/>.
/// </summary>
public static class TracepointSources
{
    /// <summary>
    /// The activity source name. Subscribe with <c>TracerProviderBuilder.AddSource(TracepointSources.Name)</c>.
    /// </summary>
    public const string Name = "Tracepoint";

    /// <summary>
    /// The process-wide activity source for Tracepoint spans.
    /// </summary>
    public static readonly ActivitySource Source = new(Name, ResolveVersion());

    /// <summary>
    /// Records an unhandled exception on <paramref name="activity"/> and marks the span as error.
    /// </summary>
    /// <param name="activity">The active span.</param>
    /// <param name="exception">The exception leaving the traced method.</param>
    [DebuggerHidden]
    [DebuggerStepThrough]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static void RecordException(Activity activity, Exception exception)
    {
        activity.SetStatus(ActivityStatusCode.Error);
        activity.AddException(exception);
    }

    private static string ResolveVersion()
    {
        var informational = typeof(TracepointSources).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (string.IsNullOrEmpty(informational))
        {
            return typeof(TracepointSources).Assembly.GetName().Version?.ToString() ?? "0.1.1";
        }

        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? informational[..plus] : informational;
    }
}
