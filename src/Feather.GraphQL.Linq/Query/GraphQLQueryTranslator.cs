using System.Linq.Expressions;
using System.Text.Json.Nodes;
using Feather.GraphQL.Linq.Document;
using Feather.GraphQL.Linq.Expressions;
using Feather.GraphQL.Linq.Filtering;
using Feather.GraphQL.Linq.Metadata;

namespace Feather.GraphQL.Linq.Query;

/// <summary>
/// Translates a LINQ chain into a <see cref="GraphQLQueryPlan"/> — the request to send and the
/// shape of the answer to expect.
/// </summary>
/// <remarks>
/// A result operator is a translation concern, not just a post-processing one. <c>First()</c>
/// asks the server for a page of one rather than fetching everything and discarding the tail,
/// and <c>Count()</c> asks for <c>totalCount</c> rather than for rows at all.
/// </remarks>
internal sealed class GraphQLQueryTranslator(IFilterTranslationProvider provider)
{
    /// <summary>Translates a chain into the request to send and the shape of its answer.</summary>
    public GraphQLQueryPlan Translate(Expression expression)
    {
        var (document, chain, rootField, paging) = Build(expression);

        return new GraphQLQueryPlan(
            GraphQLDocumentPrinter.Print(document),
            BuildVariables(document.Variables),
            chain.ElementType,
            rootField,
            paging,
            chain.Projection,
            chain.ResultOperator);
    }

    /// <summary>
    /// Translates a chain into a self-contained document, with every argument written out.
    /// </summary>
    /// <remarks>
    /// The readable form: what the parameterized document would mean once its variables are
    /// filled in. Not what gets sent — see <see cref="GraphQLDocumentPrinter.PrintInline"/>.
    /// </remarks>
    public string TranslateInline(Expression expression)
        => GraphQLDocumentPrinter.PrintInline(Build(expression).Document);

    private (GqlDocument Document, QueryChain Chain, string RootField, PagingKind Paging) Build(
        Expression expression)
    {
        var chain = QueryChain.Parse(expression);
        var metadata = ReflectionTypeMetadata.For(chain.ElementType);

        if (metadata.RootField is not { Length: > 0 } rootField)
            throw new GraphQLTranslationException("FGQL011",
                $"'{chain.ElementType.Name}' has no [GenerateQueryable] attribute, so there is no "
                + "root field to query.");

        ApplyResultOperator(chain, metadata, rootField);

        // A count asks for a single number, so "this would fetch every record" does not apply.
        if (!chain.IsCount && !chain.HasFilter && !chain.HasPaging && chain.Projection is null)
            throw new GraphQLTranslationException("FGQL012",
                $"A query over '{chain.ElementType.Name}' with no Where, Take or Select would "
                + "request every record. Add one of them.");

        if (chain.Skip.HasValue && metadata.Paging == PagingKind.Cursor)
            throw new GraphQLTranslationException("FGQL008",
                $"'{rootField}' uses cursor paging, which has no offset to Skip to. Use Take with "
                + "a cursor argument instead.");

        var filter = new FilterTranslator(provider);
        var variables = new List<GqlVariableDefinition>();
        var arguments = new List<GqlArgument>();

        // Argument names come from the chain, not from here: they are a fact about one schema.
        var names = chain.Arguments;

        if (filter.Translate(chain.MergedPredicate()) is { } where)
            Bind(variables, arguments, names.Filter, metadata.FilterInputName, where);

        if (filter.TranslateOrdering(chain.Ordering) is { } order)
            Bind(variables, arguments, names.Order, $"[{metadata.SortInputName}!]", order);

        if (chain.Take is { } take)
            Bind(variables, arguments,
                metadata.Paging == PagingKind.Cursor ? names.First : names.Take, "Int", take);

        if (chain.Skip is { } skip)
            Bind(variables, arguments, names.Skip, "Int", skip);

        // Cursor paging reads backwards with `last:`, which is the only faithful Last() there is.
        if (chain.TakeLast is { } last)
            Bind(variables, arguments, names.Last, "Int", last);

        var root = new GqlField(rootField)
        {
            Arguments = arguments,
            Selection = BuildSelection(chain, metadata)
        };

        return (new GqlDocument(variables, root), chain, rootField, metadata.Paging);
    }

    /// <summary>
    /// Turns the terminal operator into server-side arguments, so the reduction happens over a
    /// page the server already narrowed rather than over everything it could have sent.
    /// </summary>
    private static void ApplyResultOperator(QueryChain chain, IGraphQLTypeMetadata metadata, string rootField)
    {
        switch (chain.ResultOperator)
        {
            case QueryResultOperator.Sequence:
                return;

            // One row is enough to answer "the first" or "is there any".
            case QueryResultOperator.First:
            case QueryResultOperator.FirstOrDefault:
            case QueryResultOperator.Any:
                chain.Take = 1;
                return;

            // Two rows: enough to return the one, and enough to prove it was not two.
            case QueryResultOperator.Single:
            case QueryResultOperator.SingleOrDefault:
                chain.Take = 2;
                return;

            case QueryResultOperator.Last:
            case QueryResultOperator.LastOrDefault:
                if (metadata.Paging != PagingKind.Cursor)
                    throw new GraphQLTranslationException("FGQL015",
                        $"Last() over '{rootField}' needs cursor paging — only a connection can read "
                        + "backwards with 'last:'. Order the query descending and use First() instead.");

                chain.Take = null;
                chain.TakeLast = 1;
                return;

            case QueryResultOperator.Count:
            case QueryResultOperator.LongCount:
                if (metadata.Paging == PagingKind.None)
                    throw new GraphQLTranslationException("FGQL009",
                        $"Count() over '{rootField}' needs a paged root field — an un-paged field has "
                        + "no 'totalCount' to ask for, and counting client-side would fetch every record.");

                if (chain.HasPaging)
                    throw new GraphQLTranslationException("FGQL009",
                        "Count() cannot follow Skip or Take: 'totalCount' reports the size of the "
                        + "whole collection, not of the page.");

                return;

            default:
                throw GraphQLTranslationException.UnsupportedOperator(chain.ResultOperator.ToString());
        }
    }

    /// <summary>
    /// What to ask for: a count asks the wrapper for <c>totalCount</c>, an existence check asks
    /// for the cheapest single scalar, and everything else asks for the projection.
    /// </summary>
    private static IReadOnlyList<GqlField> BuildSelection(QueryChain chain, IGraphQLTypeMetadata metadata)
    {
        if (chain.IsCount)
            return [new GqlField("totalCount")];

        if (chain.ResultOperator is QueryResultOperator.Any)
            return Wrap(metadata.Paging, [new GqlField(CheapestScalar(metadata))]);

        return Wrap(metadata.Paging, SelectionSetBuilder.Build(chain.ElementType, chain.Projection));
    }

    /// <summary>
    /// A selection set cannot be empty, so an existence check still has to name a field. It picks
    /// one scalar rather than the type's full projection — <c>Any()</c> discards the value.
    /// </summary>
    private static string CheapestScalar(IGraphQLTypeMetadata metadata)
    {
        foreach (var field in metadata.Fields)
        {
            if (!field.IsIgnored && SelectionSetBuilder.IsScalar(field.ClrType))
                return field.FieldName;
        }

        throw new GraphQLTranslationException("FGQL014",
            $"'{metadata.ClrType.Name}' has no scalar field, so Any() has nothing to select.");
    }

    /// <summary>
    /// Every argument is bound to a variable, never inlined. That keeps the document constant per
    /// query shape — so <c>Where(p =&gt; p.Name == x)</c> and <c>Where(p =&gt; p.Age &gt; y)</c>
    /// print identically and share one APQ hash — and makes injection structurally impossible.
    /// </summary>
    private static void Bind(
        List<GqlVariableDefinition> variables,
        List<GqlArgument> arguments,
        string argument,
        string type,
        JsonNode? value)
    {
        string name = $"v{variables.Count}";
        variables.Add(new GqlVariableDefinition(name, type, value));
        arguments.Add(new GqlArgument(argument, name));
    }

    private static void Bind(
        List<GqlVariableDefinition> variables,
        List<GqlArgument> arguments,
        string argument,
        string type,
        int value)
        => Bind(variables, arguments, argument, type, JsonValue.Create(value));

    /// <summary>
    /// HotChocolate's paging attributes wrap the result, and the wrapper is part of both the
    /// request text and the path the materializer walks.
    /// </summary>
    private static IReadOnlyList<GqlField> Wrap(PagingKind paging, IReadOnlyList<GqlField> selection)
        => paging switch
        {
            PagingKind.Cursor => [new GqlField("nodes") { Selection = selection }],
            PagingKind.Offset => [new GqlField("items") { Selection = selection }],
            _ => selection
        };

    private static IReadOnlyDictionary<string, object?> BuildVariables(IReadOnlyList<GqlVariableDefinition> variables)
    {
        var payload = new Dictionary<string, object?>(variables.Count, StringComparer.Ordinal);
        foreach (var variable in variables)
            payload[variable.Name] = variable.Value;

        return payload;
    }
}
