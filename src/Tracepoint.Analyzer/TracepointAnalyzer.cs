using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Tracepoint;

/// <summary>
/// Reports correctness diagnostics for <c>Trace</c> and <c>TraceTag</c>.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class TracepointAnalyzer : DiagnosticAnalyzer
{
    private const string HelpLink = "https://github.com/konnta0/Tracepoint#diagnostics";

    private static readonly DiagnosticDescriptor UnsupportedTag = new(
        DiagnosticMessages.UnsupportedTagId,
        "Unsupported TraceTag type",
        DiagnosticMessages.UnsupportedTagFormat,
        "Tracepoint",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor InvalidName = new(
        DiagnosticMessages.InvalidNameId,
        "Invalid trace name",
        DiagnosticMessages.InvalidNameFormat,
        "Tracepoint",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor UnsupportedMethod = new(
        DiagnosticMessages.UnsupportedMethodId,
        "Unsupported traced method",
        DiagnosticMessages.UnsupportedMethodFormat,
        "Tracepoint",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor TagWithoutTrace = new(
        DiagnosticMessages.TagWithoutTraceId,
        "TraceTag without Trace",
        DiagnosticMessages.TagWithoutTraceFormat,
        "Tracepoint",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: HelpLink);

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        [UnsupportedTag, InvalidName, UnsupportedMethod, TagWithoutTrace];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(static start =>
        {
            var traceAttribute = start.Compilation.GetTypeByMetadataName("Tracepoint.TraceAttribute");
            var tagAttribute = start.Compilation.GetTypeByMetadataName("Tracepoint.TraceTagAttribute");
            if (traceAttribute is null || tagAttribute is null)
            {
                return;
            }

            start.RegisterSymbolAction(symbolContext => AnalyzeMethod(symbolContext, traceAttribute, tagAttribute), SymbolKind.Method);
        });
    }

    private static void AnalyzeMethod(SymbolAnalysisContext context, INamedTypeSymbol traceAttribute, INamedTypeSymbol tagAttribute)
    {
        if (context.Symbol is not IMethodSymbol method)
        {
            return;
        }

        if (method.PartialImplementationPart is not null)
        {
            return;
        }

        if (!method.Locations.Any(static location => location.IsInSource))
        {
            return;
        }

        var hasTrace = HasAttribute(method, traceAttribute);
        foreach (var parameter in method.Parameters)
        {
            var tag = GetAttribute(parameter, tagAttribute);
            if (tag is null)
            {
                continue;
            }

            if (!hasTrace)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    TagWithoutTrace,
                    parameter.Locations.FirstOrDefault() ?? method.Locations[0],
                    parameter.Name,
                    method.Name));
            }

            if (parameter.RefKind != RefKind.None || !IsSupportedTagType(parameter.Type))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    UnsupportedTag,
                    parameter.Locations.FirstOrDefault() ?? method.Locations[0],
                    parameter.Name,
                    parameter.Type.ToDisplayString()));
            }

            if (TryGetInvalidName(tag, out var invalidTagName))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    InvalidName,
                    tag.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation() ?? parameter.Locations[0],
                    "Trace tag name",
                    invalidTagName));
            }
        }

        if (!hasTrace)
        {
            return;
        }

        var trace = GetAttribute(method, traceAttribute);
        if (trace is not null && TryGetInvalidName(trace, out var invalidTraceName))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                InvalidName,
                trace.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation() ?? method.Locations[0],
                "Trace name",
                invalidTraceName));
        }

        if (TryGetUnsupportedReason(method, out var reason))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                UnsupportedMethod,
                method.Locations[0],
                method.Name,
                reason));
        }
    }

    private static bool TryGetUnsupportedReason(IMethodSymbol method, out string reason)
    {
        if (method.MethodKind == MethodKind.LocalFunction)
        {
            reason = "local functions are not supported";
            return true;
        }

        if (method.IsGenericMethod)
        {
            reason = "generic methods are not supported";
            return true;
        }

        if (method.IsAsync && method.ReturnsVoid)
        {
            reason = "async void is not supported";
            return true;
        }

        if (method.ReturnsByRef || method.ReturnsByRefReadonly)
        {
            reason = "ref returns are not supported";
            return true;
        }

        if (IsIterator(method))
        {
            reason = "iterators are not supported";
            return true;
        }

        if (method.IsAbstract || method.IsExtern || !HasBody(method))
        {
            reason = "the method has no body";
            return true;
        }

        reason = "";
        return false;
    }

    private static bool HasBody(IMethodSymbol method)
    {
        foreach (var reference in method.DeclaringSyntaxReferences)
        {
            switch (reference.GetSyntax())
            {
                case MethodDeclarationSyntax methodSyntax when methodSyntax.Body is not null || methodSyntax.ExpressionBody is not null:
                case LocalFunctionStatementSyntax local when local.Body is not null || local.ExpressionBody is not null:
                    return true;
            }
        }

        return false;
    }

    private static bool IsIterator(IMethodSymbol method)
    {
        foreach (var reference in method.DeclaringSyntaxReferences)
        {
            var root = reference.GetSyntax();
            SyntaxNode? body = root switch
            {
                MethodDeclarationSyntax methodSyntax => methodSyntax.Body ?? (SyntaxNode?)methodSyntax.ExpressionBody,
                LocalFunctionStatementSyntax local => local.Body ?? (SyntaxNode?)local.ExpressionBody,
                _ => null,
            };
            if (body is null)
            {
                continue;
            }

            foreach (var node in body.DescendantNodes(static child =>
                         child is not MethodDeclarationSyntax
                         and not LocalFunctionStatementSyntax
                         and not AnonymousFunctionExpressionSyntax))
            {
                if (node is YieldStatementSyntax)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsSupportedTagType(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { IsGenericType: true })
        {
            return false;
        }

        return type.SpecialType is SpecialType.System_String
            or SpecialType.System_Boolean
            or SpecialType.System_Int32
            or SpecialType.System_Int64
            or SpecialType.System_Double;
    }

    private static bool TryGetInvalidName(AttributeData attribute, out string name)
    {
        if (attribute.ConstructorArguments.Length == 1 && attribute.ConstructorArguments[0].Value is string value)
        {
            name = value;
            return string.IsNullOrWhiteSpace(value);
        }

        name = "";
        return false;
    }

    private static bool HasAttribute(ISymbol symbol, INamedTypeSymbol attributeType) =>
        GetAttribute(symbol, attributeType) is not null;

    private static AttributeData? GetAttribute(ISymbol symbol, INamedTypeSymbol attributeType)
    {
        foreach (var attribute in symbol.GetAttributes())
        {
            if (SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, attributeType))
            {
                return attribute;
            }
        }

        return null;
    }
}
