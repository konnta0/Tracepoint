using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Tracepoint;

namespace Tracepoint.Tests;

public sealed class AnalyzerTests
{
    [Fact]
    public async Task UnsupportedTagType_ReportsTP001()
    {
        var diagnostics = await Analyze("""
            using System;
            using Tracepoint;
            class C { [Trace] void M([TraceTag] Guid value) { _ = value; } }
            """);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "TP001");
    }

    [Fact]
    public async Task EmptyTraceName_ReportsTP003()
    {
        var diagnostics = await Analyze("""
            using Tracepoint;
            class C { [Trace(" ")] void M() { } }
            """);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "TP003");
    }

    [Fact]
    public async Task GenericMethod_ReportsTP004()
    {
        var diagnostics = await Analyze("""
            using Tracepoint;
            class C { [Trace] T M<T>(T value) => value; }
            """);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "TP004");
    }

    [Fact]
    public async Task AsyncVoid_ReportsTP004()
    {
        var diagnostics = await Analyze("""
            using System.Threading.Tasks;
            using Tracepoint;
            class C { [Trace] async void M() { await Task.CompletedTask; } }
            """);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "TP004");
    }

    [Fact]
    public async Task Iterator_ReportsTP004()
    {
        var diagnostics = await Analyze("""
            using System.Collections.Generic;
            using Tracepoint;
            class C { [Trace] IEnumerable<int> M() { yield return 1; } }
            """);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "TP004");
    }

    [Fact]
    public async Task TraceTagWithoutTrace_ReportsTP005()
    {
        var diagnostics = await Analyze("""
            using Tracepoint;
            class C { void M([TraceTag] string region) { _ = region; } }
            """);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "TP005");
    }

    [Fact]
    public async Task SupportedMethod_ReportsNothing()
    {
        var diagnostics = await Analyze("""
            using System.Threading.Tasks;
            using Tracepoint;
            class MatchService
            {
                [Trace("matchmaking.find")]
                public async Task<int> FindMatchAsync([TraceTag("match.region")] string region)
                {
                    await Task.CompletedTask;
                    return region.Length;
                }
            }
            """);

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id.StartsWith("TP", StringComparison.Ordinal));
    }

    private static async Task<ImmutableArray<Diagnostic>> Analyze(string source)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(
            "analyzer-subject",
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var withAnalyzers = compilation.WithAnalyzers([new TracepointAnalyzer()]);
        var diagnostics = await withAnalyzers.GetAnalyzerDiagnosticsAsync();
        return diagnostics;
    }
}
