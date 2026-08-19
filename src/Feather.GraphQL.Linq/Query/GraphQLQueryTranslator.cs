using System.Linq.Expressions;
using System.Text.Json.Nodes;
using Feather.GraphQL.Linq.Document;
using Feather.GraphQL.Linq.Expressions;
using Feather.GraphQL.Linq.Filtering;
using Feather.GraphQL.Linq.Metadata;
using Feather.GraphQL.Primitives;
using Feather.GraphQL.Request;

namespace Feather.GraphQL.Linq.Query;

/// <summary>
/// Translates a LINQ chain into a <see cref="GraphQLRequest"/>. The end of v1's road: the
/// response is the caller's to read.
/// </summary>
internal sealed class GraphQLQueryTranslator(IFilterTranslationProvider provider)
{
    public GraphQLRequest Translate(Expression expression)
    {
        var chain = QueryChain.Parse(expression);
        var metadata = ReflectionTypeMetadata.For(chain.ElementType);

        if (metadata.RootField is not { Length: > 0 } rootField)
            throw new GraphQLTranslationException("FGQL011",
                $"'{chain.ElementType.Name}' has no [GenerateQueryable] attribute, so there is no "
                + "root field to query.");

        if (!chain.HasFilter && !chain.HasPaging && chain.Projection is null)
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

        if (filter.Translate(chain.MergedPredicate()) is { } where)
            Bind(variables, arguments, "where", metadata.FilterInputName, where);

        if (filter.TranslateOrdering(chain.Ordering) is { } order)
            Bind(variables, arguments, "order", $"[{metadata.SortInputName}!]", order);

        if (chain.Take is { } take)
            Bind(variables, arguments, metadata.Paging == PagingKind.Cursor ? "first" : "take", "Int", take);

        if (chain.Skip is { } skip)
            Bind(variables, arguments, "skip", "Int", skip);

        var selection = SelectionSetBuilder.Build(chain.ElementType, chain.Projection);

        var root = new GqlField(rootField)
        {
            Arguments = arguments,
            Selection = Wrap(metadata.Paging, selection)
        };

        var document = new GqlDocument(variables, root);
        var query = new GraphQLQuery(GraphQLDocumentPrinter.Print(document));

        return new GraphQLRequest(query, BuildVariables(variables));
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
    /// HotChocolate's paging attributes wrap the result, and the wrapper is part of the request
    /// text — so the translator needs the paging kind even with the response side deferred.
    /// </summary>
    private static IReadOnlyList<GqlField> Wrap(PagingKind paging, IReadOnlyList<GqlField> selection)
        => paging switch
        {
            PagingKind.Cursor => [new GqlField("nodes") { Selection = selection }],
            PagingKind.Offset => [new GqlField("items") { Selection = selection }],
            _ => selection
        };

    private static Dictionary<string, object?> BuildVariables(List<GqlVariableDefinition> variables)
    {
        var payload = new Dictionary<string, object?>(variables.Count, StringComparer.Ordinal);
        foreach (var variable in variables)
            payload[variable.Name] = variable.Value;

        return payload;
    }
}
