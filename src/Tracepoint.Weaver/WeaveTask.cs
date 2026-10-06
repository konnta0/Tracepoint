using System.Reflection;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Task = Microsoft.Build.Utilities.Task;

namespace Tracepoint.Weaving;

/// <summary>
/// MSBuild task that rewrites methods marked with <c>TraceAttribute</c>.
/// </summary>
public sealed class WeaveTask : Task
{
    static WeaveTask()
    {
        AppDomain.CurrentDomain.AssemblyResolve += static (_, args) =>
        {
            var name = new AssemblyName(args.Name).Name;
            if (name is not ("Mono.Cecil" or "Mono.Cecil.Rocks" or "Mono.Cecil.Mdb" or "Mono.Cecil.Pdb"))
            {
                return null;
            }

            var directory = Path.GetDirectoryName(typeof(WeaveTask).Assembly.Location);
            if (directory is null)
            {
                return null;
            }

            var path = Path.Combine(directory, name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
    }

    /// <summary>
    /// Gets or sets the assembly to rewrite.
    /// </summary>
    [Required]
    public string AssemblyPath { get; set; } = "";

    /// <summary>
    /// Gets or sets the compilation references used to resolve assemblies.
    /// </summary>
    public ITaskItem[] References { get; set; } = [];

    /// <inheritdoc />
    public override bool Execute()
    {
        try
        {
            var references = new string[References.Length];
            for (var i = 0; i < References.Length; i++)
            {
                references[i] = References[i].ItemSpec;
            }

            var diagnostics = AssemblyWeaver.Weave(AssemblyPath, references);
            foreach (var diagnostic in diagnostics)
            {
                Log.LogError(
                    subcategory: null,
                    errorCode: diagnostic.Id,
                    helpKeyword: null,
                    file: diagnostic.FilePath,
                    lineNumber: diagnostic.Line,
                    columnNumber: diagnostic.Column,
                    endLineNumber: 0,
                    endColumnNumber: 0,
                    message: diagnostic.Message);
            }

            return diagnostics.Count == 0;
        }
        catch (Exception exception)
        {
            Log.LogErrorFromException(exception, showStackTrace: true);
            return false;
        }
    }
}
