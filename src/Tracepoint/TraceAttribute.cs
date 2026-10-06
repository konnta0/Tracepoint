namespace Tracepoint;

/// <summary>
/// Marks a method for compile-time <see cref="System.Diagnostics.Activity"/> instrumentation.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class TraceAttribute : Attribute
{
    /// <summary>
    /// Initializes the attribute using <c>{TypeName}.{MethodName}</c> as the span name.
    /// </summary>
    public TraceAttribute()
    {
    }

    /// <summary>
    /// Initializes the attribute with an explicit span name.
    /// </summary>
    /// <param name="name">Span name passed to <c>ActivitySource.StartActivity</c>.</param>
    public TraceAttribute(string name)
    {
        Name = name;
    }

    /// <summary>
    /// Gets the explicit span name, or <see langword="null"/> when the default name is used.
    /// </summary>
    public string? Name { get; }
}
