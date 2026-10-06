using System.Diagnostics;
using Tracepoint;
using Tracepoint.Smoke;

namespace Tracepoint.Tests;

public sealed class BehaviorTests
{
    public BehaviorTests()
    {
        Probe.Reset();
    }

    [Fact]
    public void SyncNoTags_WhenRecorded_UsesTypeAndMethodName()
    {
        using var scope = Listen();

        SmokeSubjects.SyncNoTags();

        var activity = scope.Single("SmokeSubjects.SyncNoTags");
        Assert.Equal(ActivityStatusCode.Unset, activity.Status);
        Assert.Empty(activity.TagObjects);
    }

    [Fact]
    public void Tags_WhenRecorded_RecordsOnlyOptInParameters()
    {
        using var scope = Listen();

        SmokeSubjects.Tags("ap-northeast-1", "ranked", Guid.NewGuid());

        var activity = scope.Single("SmokeSubjects.Tags");
        Assert.Equal("ap-northeast-1", Tag(activity, "region"));
        Assert.Equal("ranked", Tag(activity, "match.mode"));
        Assert.DoesNotContain(activity.TagObjects, tag => tag.Key == "userId");
    }

    [Fact]
    public void SyncReturn_WhenCalled_ReturnsOriginalValue()
    {
        using var scope = Listen();

        var result = SmokeSubjects.SyncReturn(11);

        Assert.Equal(11, result);
        Assert.Equal("SmokeSubjects.SyncReturn", scope.Single("SmokeSubjects.SyncReturn").OperationName);
    }

    [Fact]
    public void SyncThrow_WhenUnhandled_RecordsExceptionAndPreservesIt()
    {
        using var scope = Listen();

        var exception = Assert.Throws<InvalidOperationException>(SmokeSubjects.SyncThrow);

        Assert.Equal("boom", exception.Message);
        Assert.Contains("SyncThrow", exception.StackTrace, StringComparison.Ordinal);
        Assert.DoesNotContain("TracepointSources.RecordException", exception.StackTrace, StringComparison.Ordinal);
        var activity = scope.Single("SmokeSubjects.SyncThrow");
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        var recorded = activity.Events.Single(candidate => candidate.Name == "exception");
        Assert.Equal(typeof(InvalidOperationException).FullName, EventTag(recorded, "exception.type"));
        Assert.Equal("boom", EventTag(recorded, "exception.message"));
        Assert.Contains("SyncThrow", (string)EventTag(recorded, "exception.stacktrace")!, StringComparison.Ordinal);
    }

    [Fact]
    public void Swallow_WhenExceptionIsHandled_DoesNotMarkError()
    {
        using var scope = Listen();

        SmokeSubjects.Swallow();

        Assert.Equal(ActivityStatusCode.Unset, scope.Single("SmokeSubjects.Swallow").Status);
    }

    [Fact]
    public void Rethrow_WhenNestedCatchRethrows_RecordsOriginalException()
    {
        using var scope = Listen();

        var exception = Assert.Throws<InvalidOperationException>(SmokeSubjects.Rethrow);

        Assert.Equal("rethrow", exception.Message);
        Assert.Contains("Rethrow", exception.StackTrace, StringComparison.Ordinal);
        Assert.Equal(ActivityStatusCode.Error, scope.Single("SmokeSubjects.Rethrow").Status);
    }

    [Fact]
    public void CatchReturn_WhenExceptionIsHandled_ReturnsValueWithoutErrorStatus()
    {
        using var scope = Listen();

        var result = SmokeSubjects.CatchReturn();

        Assert.Equal(4, result);
        Assert.Equal(ActivityStatusCode.Unset, scope.Single("SmokeSubjects.CatchReturn").Status);
    }

    [Fact]
    public void WithFinally_WhenMethodReturns_RunsUserFinallyAndStopsActivity()
    {
        using var scope = Listen();

        var result = SmokeSubjects.WithFinally();

        Assert.Equal(1, result);
        Assert.Equal(1, Probe.FinallyCount);
        Assert.Null(Activity.Current);
        Assert.NotNull(scope.Single("SmokeSubjects.WithFinally").Id);
    }

    [Fact]
    public async Task FindMatchAsync_WhenAwaited_KeepsActivityCurrentAndLinksParentAndChild()
    {
        using var scope = Listen(Probe.Parent.Name, Probe.Child.Name);
        using var parent = Probe.Parent.StartActivity("aspnet");

        var result = await SmokeSubjects.FindMatchAsync("ap-northeast-1");

        Assert.Equal("ap-northeast-1", result);
        Assert.Equal("ap-northeast-1", Probe.RegionSeen);
        Assert.Equal("matchmaking.find", Probe.During!.OperationName);
        Assert.Same(Probe.During, Probe.AfterAwait);
        Assert.Equal("matchmaking.find", Probe.InnerParent!.OperationName);
        var activity = scope.Single("matchmaking.find");
        Assert.Equal("ap-northeast-1", Tag(activity, "match.region"));
        Assert.Equal(parent!.SpanId, activity.ParentSpanId);
        Assert.Equal(activity.SpanId, scope.Single("child").ParentSpanId);
    }

    [Fact]
    public async Task AsyncThrow_WhenUnhandled_FaultsTaskAndRecordsException()
    {
        using var scope = Listen();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(SmokeSubjects.AsyncThrow);

        Assert.Equal("async-boom", exception.Message);
        Assert.DoesNotContain("TracepointSources.RecordException", exception.ToString(), StringComparison.Ordinal);
        Assert.Equal(ActivityStatusCode.Error, scope.Single("SmokeSubjects.AsyncThrow").Status);
    }

    [Fact]
    public async Task TaskAsync_WhenYielded_RestoresPreviousActivity()
    {
        using var scope = Listen();

        await SmokeSubjects.TaskAsync();

        Assert.Equal("SmokeSubjects.TaskAsync", Probe.During!.OperationName);
        Assert.Same(Probe.During, Probe.AfterAwait);
        Assert.NotNull(scope.Single("SmokeSubjects.TaskAsync"));
    }

    [Fact]
    public async Task TaskOfTAsync_WhenCompleted_ReturnsValue()
    {
        using var scope = Listen();

        var result = await SmokeSubjects.TaskOfTAsync(9);

        Assert.Equal(9, result);
        Assert.Null(Activity.Current);
        Assert.Equal(ActivityStatusCode.Unset, scope.Single("SmokeSubjects.TaskOfTAsync").Status);
    }

    [Fact]
    public async Task ValueAsync_WhenYielded_StaysCurrentAcrossAwait()
    {
        using var scope = Listen();

        await SmokeSubjects.ValueAsync();

        Assert.Equal("SmokeSubjects.ValueAsync", Probe.During!.OperationName);
        Assert.Same(Probe.During, Probe.AfterAwait);
        Assert.NotNull(scope.Single("SmokeSubjects.ValueAsync"));
    }

    [Fact]
    public async Task ValueOfTAsync_WhenCompleted_TagsIntegerAndReturnsIt()
    {
        using var scope = Listen();

        var result = await SmokeSubjects.ValueOfTAsync(6);

        Assert.Equal(6, result);
        Assert.Null(Activity.Current);
        Assert.Equal(6, Tag(scope.Single("SmokeSubjects.ValueOfTAsync"), "value"));
    }

    [Fact]
    public async Task NonAsyncTask_WhenCalled_StopsActivityBeforeReturning()
    {
        using var scope = Listen();

        var task = SmokeSubjects.NonAsyncTask();

        Assert.Equal(3, await task);
        Assert.Null(Activity.Current);
        Assert.NotNull(scope.Single("SmokeSubjects.NonAsyncTask"));
    }

    [Fact]
    public async Task NonAsyncValue_WhenCalled_ReturnsValue()
    {
        using var scope = Listen();

        var result = await SmokeSubjects.NonAsyncValue(8);

        Assert.Equal(8, result);
        Assert.NotNull(scope.Single("SmokeSubjects.NonAsyncValue"));
    }

    [Fact]
    public async Task MatchService_FindMatchAsync_UsesDeclaringTypeName()
    {
        using var scope = Listen();

        var result = await new MatchService().FindMatchAsync("ap-northeast-1");

        Assert.Equal(14, result);
        Assert.Equal("MatchService.FindMatchAsync", Probe.During!.OperationName);
        Assert.Same(Probe.During, Probe.AfterAwait);
        Assert.Equal("ap-northeast-1", Tag(scope.Single("MatchService.FindMatchAsync"), "match.region"));
    }

    [Fact]
    public void NestedType_UsesDottedTypeName()
    {
        using var scope = Listen();

        Outer.Inner.Run();

        Assert.NotNull(scope.Single("Outer.Inner.Run"));
    }

    [Fact]
    public void Box_Pass_OnGenericType_ReturnsValueAndTagsLabel()
    {
        using var scope = Listen();

        var result = new Box<int>().Pass("label", 3);

        Assert.Equal(3, result);
        Assert.Equal("label", Tag(scope.Single("Box.Pass"), "label"));
    }

    [Fact]
    public async Task Box_PassAsync_OnGenericType_ReturnsValueAfterAwait()
    {
        using var scope = Listen();

        var result = await new Box<string>().PassAsync("ranked");

        Assert.Equal("ranked", result);
        Assert.NotNull(scope.Single("Box.PassAsync"));
    }

    [Fact]
    public void Point_Offset_TagsTheParameterRatherThanThis()
    {
        using var scope = Listen();
        var point = new Point { X = 2 };

        var result = point.Offset(5);

        Assert.Equal(7, result);
        Assert.Equal(5, Tag(scope.Single("Point.Offset"), "delta"));
    }

    [Fact]
    public void TracepointAssembly_DoesNotReferenceOpenTelemetry()
    {
        var references = typeof(TraceAttribute).Assembly.GetReferencedAssemblies();

        Assert.DoesNotContain(references, reference => reference.Name?.Contains("OpenTelemetry", StringComparison.Ordinal) == true);
    }

    private static ActivityScope Listen(params string[] extra) =>
        ActivityScope.Start(
            ActivitySamplingResult.AllDataAndRecorded,
            capture: true,
            extra.Prepend(TracepointSources.Name).ToArray());

    private static object? Tag(Activity activity, string key) =>
        activity.TagObjects.Single(tag => tag.Key == key).Value;

    private static object? EventTag(ActivityEvent activityEvent, string key) =>
        activityEvent.Tags.Single(tag => tag.Key == key).Value;
}
