using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Feather.GraphQL.Linq.Analyzers;

/// <summary>
/// Reports projections the translator would refuse, at the call site rather than on the first
/// request.
/// </summary>
/// <remarks>
/// <para>
/// Naming an object member selects its scalar fields, one level deep. That is enough for most
/// members and wrong for two kinds: one whose type nests further, and one whose type leads back
/// into the projection. Both are decidable from the source, so both are decided here.
/// </para>
/// <para>
/// Conservative by construction. When the analyzer cannot follow a chain it says nothing and
/// leaves the runtime to enforce the same rule — a false red squiggle costs more than a late
/// error.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ProjectionAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(GraphQLDiagnostics.NeedsProjection);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;

        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Select" })
            return;

        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol
            is not IMethodSymbol { Name: "Select" } method)
            return;

        // Only this library's queryables. An EF or in-memory Select has entirely different
        // rules, and applying these to one would be a false error on unrelated code.
        if (!EntryPoints.StartsAtEntryPoint(context.SemanticModel, invocation, context.CancellationToken))
            return;

        var lambda = invocation.ArgumentList.Arguments.Count == 1
            ? invocation.ArgumentList.Arguments[0].Expression as LambdaExpressionSyntax
            : null;

        if (lambda?.Body is not ExpressionSyntax body)
            return;

        if (context.SemanticModel.GetSymbolInfo(lambda, context.CancellationToken).Symbol
            is not IMethodSymbol { Parameters.Length: 1 } projection)
            return;

        Visit(context, body, projection.Parameters[0]);
    }

    /// <summary>
    /// Walks the projection the way <c>SelectionSetBuilder</c> does, stopping at each member the
    /// translator would have to expand on its own.
    /// </summary>
    private static void Visit(SyntaxNodeAnalysisContext context, ExpressionSyntax node, IParameterSymbol parameter)
    {
        switch (node)
        {
            case AnonymousObjectCreationExpressionSyntax anonymous:
                foreach (var initializer in anonymous.Initializers)
                    Visit(context, initializer.Expression, parameter);

                return;

            case ObjectCreationExpressionSyntax creation:
                VisitCreation(context, creation.ArgumentList, creation.Initializer, parameter);
                return;

            case ImplicitObjectCreationExpressionSyntax implicitCreation:
                VisitCreation(context, implicitCreation.ArgumentList, implicitCreation.Initializer, parameter);
                return;

            case CastExpressionSyntax cast:
                Visit(context, cast.Expression, parameter);
                return;

            case ParenthesizedExpressionSyntax parenthesized:
                Visit(context, parenthesized.Expression, parameter);
                return;

            case PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression } suppress:
                Visit(context, suppress.Operand, parameter);
                return;

            // A LINQ chain over a collection member says what to take from it, so the member
            // itself needs no expansion — but a chain that never projects still does.
            case InvocationExpressionSyntax invocation:
                VisitChain(context, invocation, parameter);
                return;

            case MemberAccessExpressionSyntax member:
                Check(context, member, parameter);
                return;
        }
    }

    private static void VisitCreation(
        SyntaxNodeAnalysisContext context,
        ArgumentListSyntax? arguments,
        InitializerExpressionSyntax? initializer,
        IParameterSymbol parameter)
    {
        foreach (var argument in arguments?.Arguments ?? default)
            Visit(context, argument.Expression, parameter);

        foreach (var expression in initializer?.Expressions ?? default)
        {
            Visit(context, expression is AssignmentExpressionSyntax assignment
                ? assignment.Right
                : expression, parameter);
        }
    }

    /// <summary>
    /// Follows a LINQ chain to its source. A chain containing a projection has said what it
    /// wants; one that only materializes — <c>.ToArray()</c> — is still a bare member.
    /// </summary>
    private static void VisitChain(
        SyntaxNodeAnalysisContext context,
        InvocationExpressionSyntax invocation,
        IParameterSymbol parameter)
    {
        var current = (ExpressionSyntax)invocation;
        bool projects = false;

        while (current is InvocationExpressionSyntax call
               && call.Expression is MemberAccessExpressionSyntax access)
        {
            if (access.Name.Identifier.ValueText == "Select")
                projects = true;

            current = access.Expression;
        }

        if (!projects && current is MemberAccessExpressionSyntax source)
            Check(context, source, parameter);
    }

    /// <summary>
    /// Decides whether one bare member has anything to select, and reports it when it does not.
    /// </summary>
    /// <remarks>
    /// Only the member's own scalar fields are selected, so a type that has none contributes an
    /// empty selection set — which GraphQL does not allow. Nested fields inside it are simply not
    /// requested, which is why a member whose type points back at the projection is fine.
    /// </remarks>
    private static void Check(
        SyntaxNodeAnalysisContext context,
        MemberAccessExpressionSyntax member,
        IParameterSymbol parameter)
    {
        if (!IsRootedAt(member, parameter, context))
            return;

        if (context.SemanticModel.GetTypeInfo(member, context.CancellationToken).Type is not { } memberType)
            return;

        // Asked of the member where the member is known, since a converter may sit on it rather
        // than on its type — and a value the model takes whole has nothing to project.
        bool leaf = context.SemanticModel.GetSymbolInfo(member, context.CancellationToken).Symbol
            is IPropertySymbol property
            ? GraphQLTypeFacts.IsLeaf(property)
            : GraphQLTypeFacts.IsLeaf(memberType);

        if (leaf)
            return;

        var target = GraphQLTypeFacts.Unwrap(memberType);
        if (target.TypeKind == TypeKind.Error)
            return;

        foreach (var field in GraphQLTypeFacts.Fields(target))
        {
            if (GraphQLTypeFacts.IsLeaf(field))
                return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            GraphQLDiagnostics.NeedsProjection, member.GetLocation(), member.ToString(), target.Name));
    }

    private static string FirstScalar(ITypeSymbol type)
    {
        foreach (var field in GraphQLTypeFacts.Fields(type))
        {
            if (GraphQLTypeFacts.IsScalar(GraphQLTypeFacts.Unwrap(field.Type)))
                return field.Name;
        }

        return "…";
    }

    /// <summary>True when the member chain bottoms out at the projection's own parameter.</summary>
    private static bool IsRootedAt(
        ExpressionSyntax expression,
        IParameterSymbol parameter,
        SyntaxNodeAnalysisContext context)
    {
        var current = expression;

        while (true)
        {
            switch (current)
            {
                case MemberAccessExpressionSyntax member:
                    current = member.Expression;
                    continue;

                case PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression } suppress:
                    current = suppress.Operand;
                    continue;

                case ParenthesizedExpressionSyntax parenthesized:
                    current = parenthesized.Expression;
                    continue;

                case IdentifierNameSyntax identifier:
                    return SymbolEqualityComparer.Default.Equals(
                        context.SemanticModel.GetSymbolInfo(identifier, context.CancellationToken).Symbol,
                        parameter);

                default:
                    return false;
            }
        }
    }

}
