using System.ComponentModel;

namespace Tracepoint;

/// <summary>
/// Marks a method whose body has already been rewritten. The weaver uses this to avoid weaving twice.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class WovenAttribute : Attribute
{
}
