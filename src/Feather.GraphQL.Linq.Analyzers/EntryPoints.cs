using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Feather.GraphQL.Linq.Analyzers;

/// <summary>
/// Which calls produce one of this library's queryables, and whether a chain visibly starts at
/// one.
/// </summary>
/// <remarks>
/// Shared by the analyzer and the generator because they must agree: a chain the analyzer
/// declines to check is one the generator cannot shape either, and the two drifting apart would
/// mean diagnostics and generated code disagreeing about the same code.
/// </remarks>
internal static class EntryPoints
{
    /// <summary>A call that names a root field, and so produces a queryable of ours.</summary>
    public static bool IsEntryPoint(IMethodSymbol method)
    {
        if (method.Name is not ("CreateQueryable" or "For"))
            return false;

        // Matched on the outermost containing type, because an extension member's own container
        // is a compiler-generated nested type whose name is not something to depend on.
        var container = method.ContainingType;
        while (container?.ContainingType is not null)
            container = container.ContainingType;

        return container?.ToDisplayString() is
            "Feather.GraphQL.Linq.Providers.HttpClientGraphQLQueryableExtensions"
            or "Feather.GraphQL.Linq.Query.GraphQLQueryable";
    }

    /// <summary>
    /// True when the chain this call sits on visibly starts at an entry point.
    /// </summary>
    /// <remarks>
    /// Works when the whole chain is one expression and not otherwise: a queryable arriving
    /// through a variable, a parameter or a field is invisible here. That is the `FGQL006`
    /// boundary, and both callers treat it the same way — say nothing, generate nothing, and
    /// leave it to the runtime.
    /// </remarks>
    public static bool StartsAtEntryPoint(
        SemanticModel model,
        InvocationExpressionSyntax invocation,
        CancellationToken token)
    {
        var current = invocation.Expression is MemberAccessExpressionSyntax access
            ? access.Expression
            : null;

        while (current is not null)
        {
            switch (current)
            {
                case InvocationExpressionSyntax call:
                {
                    if (model.GetSymbolInfo(call, token).Symbol is IMethodSymbol origin
                        && IsEntryPoint(origin))
                        return true;

                    current = call.Expression is MemberAccessExpressionSyntax member
                        ? member.Expression
                        : null;
                    continue;
                }

                case ParenthesizedExpressionSyntax parenthesized:
                    current = parenthesized.Expression;
                    continue;

                default:
                    return false;
            }
        }

        return false;
    }
}
