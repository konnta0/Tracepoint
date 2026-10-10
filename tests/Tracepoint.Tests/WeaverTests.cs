using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Tracepoint.Smoke;
using Tracepoint.Weaving;

namespace Tracepoint.Tests;

public sealed class WeaverTests
{
    [Fact]
    public void GenericMethod_IsRejectedWithoutRewriting()
    {
        var assemblyPath = Emit("""
            using Tracepoint;
            public static class Subjects
            {
                [Trace]
                public static T Id<T>(T value) => value;
            }
            """);

        var diagnostics = AssemblyWeaver.Weave(assemblyPath, ReferencePaths());

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "TP004");
    }

    [Fact]
    public void WeavesWhenConsumerDoesNotReferenceActivitySource()
    {
        var assemblyPath = Emit("""
            using Tracepoint;
            public static class Subjects
            {
                [Trace]
                public static int Add(int value) => value + 1;
            }
            """);

        var diagnostics = AssemblyWeaver.Weave(assemblyPath, ReferencePaths());

        Assert.Empty(diagnostics);
        using var assembly = AssemblyDefinition.ReadAssembly(assemblyPath);
        var method = assembly.MainModule.GetType("Subjects").Methods.Single(candidate => candidate.Name == "Add");
        Assert.Contains(method.CustomAttributes, attribute => attribute.AttributeType.Name == "WovenAttribute");
    }

    [Fact]
    public void Sync_KeepsASingleUserBody()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(typeof(SmokeSubjects).Assembly.Location);
        var method = assembly.MainModule.Types
            .SelectMany(type => type.Methods)
            .Single(candidate => candidate.Name == "SyncReturn");

        Assert.Equal(1, method.Body.Instructions.Count(static instruction =>
            instruction.Operand is MethodReference called && called.Name == "HasListeners"));
        Assert.Equal(1, CountArgumentLoads(method, index: 0));
    }

    [Fact]
    public void IntegerTag_BoxesOnlyAfterActivityNullCheck()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(typeof(SmokeSubjects).Assembly.Location);
        var method = assembly.MainModule.Types
            .SelectMany(type => type.Methods)
            .Single(candidate => candidate.Name == "TaggedInt");
        var box = method.Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Box);
        var nullCheck = method.Body.Instructions.Last(instruction =>
            instruction.OpCode == OpCodes.Brfalse && instruction.Offset < box.Offset);

        Assert.NotNull(nullCheck);
        Assert.True(nullCheck.Offset < box.Offset);
    }

    private static string Emit(string source)
    {
        var compilation = CSharpCompilation.Create(
            "weaver-subject-" + Guid.NewGuid().ToString("n"),
            [CSharpSyntaxTree.ParseText(source)],
            ReferencePaths().Select(path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));
        var directory = Path.Combine(Path.GetTempPath(), "tracepoint-tests-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "subject.dll");
        var result = compilation.Emit(path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return path;
    }

    private static int CountArgumentLoads(MethodDefinition method, int index)
    {
        var count = 0;
        foreach (var instruction in method.Body.Instructions)
        {
            if (instruction.OpCode == OpCodes.Ldarg_0 && index == 0)
            {
                count++;
                continue;
            }

            if (instruction.OpCode == OpCodes.Ldarg_1 && index == 1)
            {
                count++;
                continue;
            }

            if (instruction.OpCode == OpCodes.Ldarg_2 && index == 2)
            {
                count++;
                continue;
            }

            if (instruction.OpCode == OpCodes.Ldarg_3 && index == 3)
            {
                count++;
                continue;
            }

            if ((instruction.OpCode == OpCodes.Ldarg || instruction.OpCode == OpCodes.Ldarg_S) &&
                instruction.Operand is ParameterDefinition parameter &&
                parameter.Index == index)
            {
                count++;
            }
        }

        return count;
    }

    private static IEnumerable<string> ReferencePaths()
    {
        var trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        return trusted.Append(typeof(TraceAttribute).Assembly.Location);
    }
}
