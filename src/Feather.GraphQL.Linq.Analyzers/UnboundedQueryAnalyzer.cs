using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Feather.GraphQL.Linq.Analyzers;

/// <summary>
/// Reports a query that asks for every record, at the call site rather than not at all.
/// </summary>
/// <remarks>
/// <para>
/// The translator used to refuse such a chain outright, on the first request. That was the wrong
/// severity in the wrong place: fetching a whole collection with its scalar fields filled in is
/// a legitimate thing to want — small reference tables are the obvious case — and a rule about
/// the shape of a chain is decidable from the source, so it does not need a round trip to
/// discover.
/// </para>
/// <para>
/// Conservative for the same reason the projection rules are: this reads the chain through
/// <see cref="QueryChainReader"/>, which declines anything it cannot be certain about — a
/// queryable arriving through a parameter or a field, a chain that never reaches a terminal.
/// A declined chain is simply not reported, because a warning on code that turns out to be
/// bounded costs more than a missed one.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UnboundedQueryAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(GraphQLDiagnostics.Unbounded);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;

        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol
            is not IMethodSymbol method || !EntryPoints.IsEntryPoint(method))
            return;

        // The same reader the generator uses, so the two cannot disagree about what a chain
        // says. Null is an uncertain chain, which is left to run unremarked.
        if (QueryChainReader.Read(invocation, method, context.SemanticModel, context.CancellationToken, new Refusals())
            is not { } consumptions)
            return;

        foreach (var facts in consumptions)
        {
            if (!IsUnbounded(facts))
                continue;

            context.ReportDiagnostic(Diagnostic.Create(
                GraphQLDiagnostics.Unbounded,
                invocation.GetLocation(),
                facts.RootField,
                facts.ElementType.Name));

            // One squiggle per query, even where a stored queryable is consumed several times:
            // every one of them shares this entry point, and the entry point is what is marked.
            return;
        }
    }

    /// <summary>
    /// Whether a chain asks for the whole collection.
    /// </summary>
    /// <remarks>
    /// Only a chain that asks for a sequence can. A result operator bounds the request by
    /// construction — the translator turns <c>First</c> into a page of one and <c>Count</c> into
    /// <c>totalCount</c> — which is why the runtime rule this replaces applied its result
    /// operator before deciding.
    /// </remarks>
    private static bool IsUnbounded(ChainFacts facts)
        => facts.Result == ResultKind.Sequence
            && !facts.HasFilter
            && !facts.HasPaging
            && facts.Projection is null;
}
