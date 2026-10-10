using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Tracepoint.Weaving;

/// <summary>
/// Rewrites an assembly so methods marked with <c>TraceAttribute</c> create an <c>Activity</c>.
/// </summary>
public static class AssemblyWeaver
{
    private static readonly string[] RuntimeAssemblies =
    [
        "System.Diagnostics.DiagnosticSource",
        "System.Runtime",
        "System.Private.CoreLib",
        "netstandard",
        "mscorlib",
    ];

    /// <summary>
    /// Weaves <paramref name="assemblyPath"/> in place.
    /// </summary>
    /// <param name="assemblyPath">The compiled assembly.</param>
    /// <param name="references">Reference assemblies used to resolve types.</param>
    /// <returns>Diagnostics that should fail the build. The assembly is left unchanged when this is not empty.</returns>
    public static IReadOnlyList<WeaveDiagnostic> Weave(string assemblyPath, IEnumerable<string> references)
    {
        var diagnostics = new List<WeaveDiagnostic>();
        var pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
        var readSymbols = File.Exists(pdbPath);
        using var assembly = ReadAssembly(assemblyPath, references, readSymbols);
        if (!TryCreateContext(assembly.MainModule, out var context, out var contextError))
        {
            if (HasTraceAttribute(assembly))
            {
                diagnostics.Add(new WeaveDiagnostic("TP004", contextError, null, 0, 0));
            }

            return diagnostics;
        }

        var changed = false;
        foreach (var type in EnumerateTypes(assembly.MainModule.Types))
        {
            if (!type.HasMethods)
            {
                continue;
            }

            foreach (var method in type.Methods)
            {
                if (!HasAttribute(method, "Tracepoint.TraceAttribute"))
                {
                    continue;
                }

                if (HasAttribute(method, "Tracepoint.WovenAttribute"))
                {
                    continue;
                }

                var before = diagnostics.Count;
                MethodWeaver.Weave(method, context, diagnostics);
                if (diagnostics.Count == before)
                {
                    changed = true;
                }
            }
        }

        if (diagnostics.Count > 0 || !changed)
        {
            return diagnostics;
        }

        WriteAssembly(assembly, assemblyPath, readSymbols && assembly.MainModule.HasSymbols);
        return diagnostics;
    }

    private static AssemblyDefinition ReadAssembly(string assemblyPath, IEnumerable<string> references, bool readSymbols)
    {
        var resolver = new DefaultAssemblyResolver();
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddDirectory(directories, resolver, assemblyPath);
        foreach (var reference in references)
        {
            AddDirectory(directories, resolver, reference);
        }

        try
        {
            return AssemblyDefinition.ReadAssembly(assemblyPath, CreateReader(resolver, readSymbols));
        }
        catch (Exception) when (readSymbols)
        {
            return AssemblyDefinition.ReadAssembly(assemblyPath, CreateReader(resolver, symbols: false));
        }
    }

    private static ReaderParameters CreateReader(DefaultAssemblyResolver resolver, bool symbols) =>
        new()
        {
            AssemblyResolver = resolver,
            InMemory = true,
            ReadingMode = ReadingMode.Deferred,
            ReadSymbols = symbols,
            SymbolReaderProvider = symbols ? new DefaultSymbolReaderProvider(throwIfNoSymbol: false) : null,
        };

    private static void WriteAssembly(AssemblyDefinition assembly, string assemblyPath, bool writeSymbols)
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "tracepoint-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempDirectory);
        var tempAssembly = Path.Combine(tempDirectory, Path.GetFileName(assemblyPath));
        try
        {
            assembly.Write(tempAssembly, new WriterParameters
            {
                WriteSymbols = writeSymbols,
                SymbolWriterProvider = writeSymbols ? new DefaultSymbolWriterProvider() : null,
            });
            File.Copy(tempAssembly, assemblyPath, overwrite: true);
            if (writeSymbols)
            {
                var tempPdb = Path.ChangeExtension(tempAssembly, ".pdb");
                var pdb = Path.ChangeExtension(assemblyPath, ".pdb");
                if (File.Exists(tempPdb))
                {
                    File.Copy(tempPdb, pdb, overwrite: true);
                }
            }
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static bool TryCreateContext(ModuleDefinition module, out WeaveContext context, out string error)
    {
        context = null!;
        var activitySource = FindType(module, "System.Diagnostics.ActivitySource");
        var activity = FindType(module, "System.Diagnostics.Activity");
        var exception = FindType(module, "System.Exception");
        var sources = FindType(module, "Tracepoint.TracepointSources");
        var woven = FindType(module, "Tracepoint.WovenAttribute");
        if (activitySource is null || activity is null || exception is null || sources is null || woven is null)
        {
            error = "Tracepoint could not resolve Activity or TracepointSources. Reference the Tracepoint package from this project.";
            return false;
        }

        var sourceField = sources.Fields.FirstOrDefault(field => field.Name == "Source" && field.IsStatic);
        var record = sources.Methods.FirstOrDefault(method => method.Name == "RecordException" && method.Parameters.Count == 2);
        var hasListeners = activitySource.Methods.FirstOrDefault(method => method.Name == "HasListeners" && method.Parameters.Count == 0);
        var start = FindMethod(activitySource, "StartActivity", "System.String", "System.Diagnostics.ActivityKind");
        var setTag = FindMethod(activity, "SetTag", "System.String", "System.Object");
        var dispose = activity.Methods.FirstOrDefault(method => method.Name == "Dispose" && method.Parameters.Count == 0 && method.IsPublic);
        var ctor = woven.Methods.FirstOrDefault(method => method.IsConstructor && !method.IsStatic && method.Parameters.Count == 0);
        if (sourceField is null || record is null || hasListeners is null || start is null || setTag is null || dispose is null || ctor is null)
        {
            error = "Tracepoint could not bind the Activity APIs required to weave traced methods.";
            return false;
        }

        context = new WeaveContext(
            module,
            module.ImportReference(sourceField),
            module.ImportReference(start),
            module.ImportReference(setTag),
            module.ImportReference(dispose),
            module.ImportReference(record),
            module.ImportReference(hasListeners),
            module.ImportReference(ctor),
            module.ImportReference(activity),
            module.ImportReference(exception));
        error = "";
        return true;
    }

    private static MethodDefinition? FindMethod(TypeDefinition type, string name, params string[] parameterTypes)
    {
        foreach (var method in type.Methods)
        {
            if (method.Name != name || method.Parameters.Count != parameterTypes.Length)
            {
                continue;
            }

            var match = true;
            for (var i = 0; i < parameterTypes.Length; i++)
            {
                if (method.Parameters[i].ParameterType.FullName != parameterTypes[i])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return method;
            }
        }

        return null;
    }

    private static TypeDefinition? FindType(ModuleDefinition module, string fullName)
    {
        var local = module.GetType(fullName);
        if (local is not null)
        {
            return local;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (fullName.StartsWith("Tracepoint.", StringComparison.Ordinal))
        {
            return ResolveTypeFromAssembly(module, fullName, "Tracepoint", seen);
        }

        foreach (var assemblyName in RuntimeAssemblies)
        {
            var type = ResolveTypeFromAssembly(module, fullName, assemblyName, seen);
            if (type is not null)
            {
                return type;
            }
        }

        var tracepoint = ResolveTypeFromAssembly(module, "Tracepoint.TracepointSources", "Tracepoint", seen);
        return tracepoint is null
            ? null
            : FindTypeInReferences(tracepoint.Module, fullName, seen, depth: 0);
    }

    private static TypeDefinition? ResolveTypeFromAssembly(
        ModuleDefinition module,
        string fullName,
        string assemblyName,
        HashSet<string> seen)
    {
        foreach (var reference in module.AssemblyReferences)
        {
            if (!string.Equals(reference.Name, assemblyName, StringComparison.Ordinal))
            {
                continue;
            }

            if (!seen.Add(reference.FullName))
            {
                return null;
            }

            try
            {
                return module.AssemblyResolver.Resolve(reference)?.MainModule.GetType(fullName);
            }
            catch (AssemblyResolutionException)
            {
                return null;
            }
        }

        return null;
    }

    private static TypeDefinition? FindTypeInReferences(
        ModuleDefinition module,
        string fullName,
        HashSet<string> seen,
        int depth)
    {
        const int maxDepth = 1;
        foreach (var reference in module.AssemblyReferences)
        {
            if (!seen.Add(reference.FullName))
            {
                continue;
            }

            AssemblyDefinition resolved;
            try
            {
                resolved = module.AssemblyResolver.Resolve(reference);
            }
            catch (AssemblyResolutionException)
            {
                continue;
            }

            var type = resolved.MainModule.GetType(fullName);
            if (type is not null)
            {
                return type;
            }

            if (depth < maxDepth)
            {
                var nested = FindTypeInReferences(resolved.MainModule, fullName, seen, depth + 1);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static bool HasTraceAttribute(AssemblyDefinition assembly)
    {
        foreach (var type in EnumerateTypes(assembly.MainModule.Types))
        {
            foreach (var method in type.Methods)
            {
                if (HasAttribute(method, "Tracepoint.TraceAttribute"))
                {
                    return true;
                }
            }
        }

        return false;
    }

    internal static bool HasAttribute(ICustomAttributeProvider provider, string fullName)
    {
        if (!provider.HasCustomAttributes)
        {
            return false;
        }

        var shortName = fullName.AsSpan(fullName.LastIndexOf('.') + 1);
        foreach (var attribute in provider.CustomAttributes)
        {
            if (attribute.AttributeType.Name.AsSpan().SequenceEqual(shortName) &&
                attribute.AttributeType.FullName == fullName)
            {
                return true;
            }
        }

        return false;
    }

    // Cecil's ModuleDefinition is not thread-safe, so candidates are found and rewritten on one thread.
    // Compiler-generated types never carry [Trace]; their bodies load only if an async method resolves them.
    private static IEnumerable<TypeDefinition> EnumerateTypes(IEnumerable<TypeDefinition> types)
    {
        foreach (var type in types)
        {
            if (type.Name.Length > 0 && type.Name[0] == '<')
            {
                continue;
            }

            yield return type;
            if (!type.HasNestedTypes)
            {
                continue;
            }

            foreach (var nested in EnumerateTypes(type.NestedTypes))
            {
                yield return nested;
            }
        }
    }

    private static void AddDirectory(HashSet<string> directories, DefaultAssemblyResolver resolver, string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && directories.Add(directory))
        {
            resolver.AddSearchDirectory(directory);
        }
    }
}
