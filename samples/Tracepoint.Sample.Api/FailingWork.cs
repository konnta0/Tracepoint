using Tracepoint;

namespace Tracepoint.Sample.Api;

internal sealed class FailingWork
{
    [Trace("matchmaking.fail")]
    public void Throw()
    {
        throw new InvalidOperationException("sample failure");
    }
}
