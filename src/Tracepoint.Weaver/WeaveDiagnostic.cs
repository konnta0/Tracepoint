namespace Tracepoint.Weaving;

/// <summary>
/// A compile-time instrumentation error that should fail the build.
/// </summary>
public sealed class WeaveDiagnostic
{
    /// <summary>
    /// Creates a diagnostic.
    /// </summary>
    /// <param name="id">Diagnostic identifier, such as TP004.</param>
    /// <param name="message">Human-readable message.</param>
    /// <param name="filePath">Source file, when symbols are available.</param>
    /// <param name="line">One-based line, or 0 when unknown.</param>
    /// <param name="column">One-based column, or 0 when unknown.</param>
    public WeaveDiagnostic(string id, string message, string? filePath, int line, int column)
    {
        Id = id;
        Message = message;
        FilePath = filePath;
        Line = line;
        Column = column;
    }

    /// <summary>Gets the diagnostic identifier.</summary>
    public string Id { get; }

    /// <summary>Gets the message.</summary>
    public string Message { get; }

    /// <summary>Gets the source file path, when available.</summary>
    public string? FilePath { get; }

    /// <summary>Gets the one-based line number, or 0.</summary>
    public int Line { get; }

    /// <summary>Gets the one-based column number, or 0.</summary>
    public int Column { get; }
}
