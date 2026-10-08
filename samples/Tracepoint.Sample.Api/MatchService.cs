using Tracepoint;

namespace Tracepoint.Sample.Api;

internal sealed class MatchService
{
    [Trace("matchmaking.find")]
    public async Task<Match> FindMatchAsync([TraceTag("match.region")] string region)
    {
        var pool = ResolvePool(region);
        await Task.Delay(15).ConfigureAwait(false);
        return new Match(Guid.NewGuid().ToString("n")[..8], pool);
    }

    [Trace]
    private static string ResolvePool([TraceTag("match.region")] string region)
    {
        return region.Equals("jp", StringComparison.OrdinalIgnoreCase) ? "tokyo" : "global";
    }
}

internal sealed record Match(string Id, string Pool);
