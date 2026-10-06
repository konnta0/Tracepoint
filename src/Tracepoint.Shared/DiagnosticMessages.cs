namespace Tracepoint;

internal static class DiagnosticMessages
{
    internal const string UnsupportedTagId = "TP001";
    internal const string InvalidNameId = "TP003";
    internal const string UnsupportedMethodId = "TP004";
    internal const string TagWithoutTraceId = "TP005";

    internal const string UnsupportedTagFormat =
        "Parameter '{0}' has unsupported TraceTag type '{1}'; supported types are string, bool, int, long, and double";

    internal const string InvalidNameFormat =
        "{0} '{1}' must contain a non-whitespace character";

    internal const string UnsupportedMethodFormat =
        "Method '{0}' cannot be traced because {1}";

    internal const string TagWithoutTraceFormat =
        "Parameter '{0}' has TraceTag but '{1}' does not have Trace";

    internal static string UnsupportedTag(string parameterName, string typeName) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture, UnsupportedTagFormat, parameterName, typeName);

    internal static string InvalidName(string kind, string name) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture, InvalidNameFormat, kind, name);

    internal static string UnsupportedMethod(string method, string reason) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture, UnsupportedMethodFormat, method, reason);

    internal static string TagWithoutTrace(string parameterName, string method) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture, TagWithoutTraceFormat, parameterName, method);
}
