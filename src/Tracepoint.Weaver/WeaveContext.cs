using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Tracepoint.Weaving;

internal sealed class WeaveContext
{
    internal WeaveContext(
        ModuleDefinition module,
        FieldReference sourceField,
        MethodReference startActivity,
        MethodReference setTag,
        MethodReference dispose,
        MethodReference recordException,
        MethodReference hasListeners,
        MethodReference wovenConstructor,
        TypeReference activityType,
        TypeReference exceptionType)
    {
        Module = module;
        SourceField = sourceField;
        StartActivity = startActivity;
        SetTag = setTag;
        Dispose = dispose;
        RecordException = recordException;
        HasListeners = hasListeners;
        WovenConstructor = wovenConstructor;
        ActivityType = activityType;
        ExceptionType = exceptionType;
    }

    internal ModuleDefinition Module { get; }

    internal FieldReference SourceField { get; }

    internal MethodReference StartActivity { get; }

    internal MethodReference SetTag { get; }

    internal MethodReference Dispose { get; }

    internal MethodReference RecordException { get; }

    internal MethodReference HasListeners { get; }

    internal MethodReference WovenConstructor { get; }

    internal TypeReference ActivityType { get; }

    internal TypeReference ExceptionType { get; }
}
