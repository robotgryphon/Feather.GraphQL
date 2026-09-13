using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Feather.GraphQL.Linq.Analyzers;

/// <summary>
/// Reports the queries that will not survive there being only one way to write one.
/// </summary>
/// <remarks>
/// <para>
/// A compiled query is a <c>[GraphQLQuery]</c> method whose call sites the compiler replaces. Two
/// things defeat that, and neither says anything today: a chain written somewhere that is not such
/// a method, and a call that is not a call.
/// </para>
/// <para>
/// Warnings for now. The runtime translation still exists, so both still work — they work the slow
/// way, which is the thing being measured before it is removed. When it goes, these become the two
/// errors that say so.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CompilableQueryAnalyzer : DiagnosticAnalyzer
{
    private const string Attribute = "Feather.GraphQL.GraphQLQueryAttribute";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(GraphQLDiagnostics.NotIsolated, GraphQLDiagnostics.NotInvoked);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterSyntaxNodeAction(Isolated, SyntaxKind.InvocationExpression);
        context.RegisterSyntaxNodeAction(Invoked, SyntaxKind.IdentifierName, SyntaxKind.SimpleMemberAccessExpression);
    }

    /// <summary>
    /// Reports a chain that begins somewhere the compiler cannot replace.
    /// </summary>
    /// <remarks>
    /// Reported at the entry point rather than at the terminal, because the entry point is the one
    /// part of a chain that is always written where the chain is — a terminal can be reached from
    /// a variable several statements later, and blaming that line would point at the wrong code.
    /// </remarks>
    private static void Isolated(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;

        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol
            is not IMethodSymbol method || !EntryPoints.IsEntryPoint(method))
            return;

        if (Compiled(invocation, context))
            return;

        context.ReportDiagnostic(Diagnostic.Create(GraphQLDiagnostics.NotIsolated, invocation.GetLocation()));
    }

    /// <summary>Reports a compiled query named without being called.</summary>
    private static void Invoked(SyntaxNodeAnalysisContext context)
    {
        var node = context.Node;

        // The name of the method being called is not a use of it as a value, and neither is the
        // member access that qualifies it — both are part of the call this is looking past.
        if (node.Parent is InvocationExpressionSyntax call && call.Expression == node)
            return;

        if (node is IdentifierNameSyntax
            && node.Parent is MemberAccessExpressionSyntax { Parent: InvocationExpressionSyntax outer } access
            && outer.Expression == access
            && access.Name == node)
            return;

        // A qualified name is reported once, at the member access, rather than again at its parts.
        if (node is IdentifierNameSyntax && node.Parent is MemberAccessExpressionSyntax)
            return;

        // A `cref` names a method in prose. It resolves to the same symbol and is not a use of
        // it at all, which is the one thing a symbol lookup cannot tell on its own.
        if (node.FirstAncestorOrSelf<DocumentationCommentTriviaSyntax>() is not null)
            return;

        if (context.SemanticModel.GetSymbolInfo(node, context.CancellationToken).Symbol
            is not IMethodSymbol method || !Marked(method))
            return;

        // `nameof(ByIdAsync)` names the method without reaching it, which is not a call that
        // needed replacing.
        if (node.Ancestors().OfType<InvocationExpressionSyntax>().Any(IsNameOf))
            return;

        context.ReportDiagnostic(
            Diagnostic.Create(GraphQLDiagnostics.NotInvoked, node.GetLocation(), method.Name));
    }

    /// <summary>Whether this chain sits in the body of a method the compiler will write out.</summary>
    /// <remarks>
    /// The attribute is checked on the symbol rather than counted on the syntax: a method with
    /// some other attribute is not a compiled query, and a method with none is the common case.
    /// </remarks>
    private static bool Compiled(SyntaxNode node, SyntaxNodeAnalysisContext context)
    {
        for (var current = node; current is not null; current = current.Parent)
        {
            switch (current)
            {
                case MethodDeclarationSyntax declaration:
                    return context.SemanticModel.GetDeclaredSymbol(declaration, context.CancellationToken)
                        is { } method && Marked(method);

                // A chain inside a lambda or a local function is not the method's body even when
                // the method is marked, because what is replaced is the call to the method.
                case LambdaExpressionSyntax:
                case LocalFunctionStatementSyntax:
                case AnonymousMethodExpressionSyntax:
                    return false;
            }
        }

        return false;
    }

    private static bool Marked(IMethodSymbol method)
        => method.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == Attribute);

    private static bool IsNameOf(InvocationExpressionSyntax invocation)
        => invocation.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" };
}
