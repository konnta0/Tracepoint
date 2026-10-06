namespace Tracepoint;

/// <summary>
/// Opts a parameter in as a span attribute. Parameters without this attribute are not recorded.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter, Inherited = false, AllowMultiple = false)]
public sealed class TraceTagAttribute : Attribute
{
    /// <summary>
    /// Initializes the attribute using the parameter name as the attribute name.
    /// </summary>
    public TraceTagAttribute()
    {
    }

    /// <summary>
    /// Initializes the attribute with an explicit attribute name.
    /// </summary>
    /// <param name="name">Span attribute name passed to <c>Activity.SetTag</c>.</param>
    public TraceTagAttribute(string name)
    {
        Name = name;
    }

    /// <summary>
    /// Gets the explicit attribute name, or <see langword="null"/> when the parameter name is used.
    /// </summary>
    public string? Name { get; }
}
