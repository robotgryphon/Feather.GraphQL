using System.Text.Json;
using Feather.GraphQL.Linq.Expressions;
using Feather.GraphQL.Linq.Metadata;
using Feather.GraphQL.Linq.Query;

namespace Feather.GraphQL.Linq.Execution;

/// <summary>
/// Reads a response body into the result the chain's terminal asked for.
/// </summary>
/// <remarks>
/// <para>
/// The projection is applied here rather than being deserialized into directly: an anonymous
/// type has no accessible setters and no parameterless constructor, so the elements are
/// materialized as the queried type first and the compiled <c>Select</c> runs over them. That
/// also keeps one rule for field naming — the one the selection set was built from.
/// </para>
/// <para>
/// Deserialization goes through a <c>JsonTypeInfo</c> rather than a <see cref="Type"/>, so a
/// registered <c>JsonSerializerContext</c> supplies the contract and
/// nothing reflects. See <see cref="GraphQLJsonContextRegistry"/>.
/// </para>
/// </remarks>
internal static class ResultMaterializer
{
    /// <summary>Materializes the rows the query returned, in server order.</summary>
    public static List<TOut> Rows<TOut>(GraphQLQueryPlan plan, JsonElement data)
    {
        var elements = Elements(plan, data);

        if (elements.ValueKind is not JsonValueKind.Array)
            return [];

        var rows = new List<TOut>(elements.GetArrayLength());

        // One contract for the queried type, resolved once: generated when a registered context
        // covers it, reflected when none does.
        var contract = GraphQLJsonContextRegistry.TypeInfo(plan.ElementType);

        // A generated shaper if one was emitted for this projection; otherwise the lambda is
        // compiled, which is the only step here that generates IL at runtime.
        var shaper = plan.Projection is null ? null : GraphQLProjectionRegistry.Find(plan.Projection);
        var compiled = shaper is null ? plan.Projection?.Compile() : null;

        foreach (var element in elements.EnumerateArray())
        {
            object? source = element.Deserialize(contract);

            rows.Add(Shape<TOut>(source, shaper, compiled));
        }

        return rows;
    }

    /// <summary>
    /// Applies the projection: the generated shaper when there is one, the compiled lambda when
    /// there is not, and neither when the chain had no <c>Select</c>.
    /// </summary>
    private static TOut Shape<TOut>(object? source, Func<object?, object?>? shaper, Delegate? compiled)
    {
        if (shaper is not null)
            return (TOut)shaper(source)!;

        // No Select: the queried type is the result type.
        return compiled is null ? (TOut)source! : (TOut)compiled.DynamicInvoke(source)!;
    }

    /// <summary>Reads the <c>totalCount</c> the count translation asked for.</summary>
    public static long Count(GraphQLQueryPlan plan, JsonElement data)
    {
        var field = RootField(plan, data);

        if (field.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return 0;

        if (!field.TryGetProperty("totalCount", out var total))
            throw new GraphQLTranslationException("FGQL009",
                $"'{plan.RootField}' returned no 'totalCount'. The server must expose it on the "
                + "connection for Count() to work — HotChocolate needs IncludeTotalCount on the "
                + "paging attribute.");

        return total.GetInt64();
    }

    /// <summary>Reduces the rows the way the chain's terminal operator asks.</summary>
    /// <remarks>
    /// The server was already told to return at most what the operator needs — one row for
    /// <c>First</c>, two for <c>Single</c> — so this is a reduction over a narrow page, not over
    /// a collection that was fetched whole and then thrown away.
    /// </remarks>
    public static TOut Reduce<TOut>(GraphQLQueryPlan plan, List<TOut> rows)
    {
        switch (plan.ResultOperator)
        {
            case QueryResultOperator.First when rows.Count > 0:
            case QueryResultOperator.FirstOrDefault when rows.Count > 0:
            case QueryResultOperator.Single when rows.Count == 1:
            case QueryResultOperator.SingleOrDefault when rows.Count == 1:
                return rows[0];

            case QueryResultOperator.Last when rows.Count > 0:
            case QueryResultOperator.LastOrDefault when rows.Count > 0:
                return rows[^1];

            case QueryResultOperator.FirstOrDefault:
            case QueryResultOperator.SingleOrDefault when rows.Count == 0:
            case QueryResultOperator.LastOrDefault:
                return default!;

            case QueryResultOperator.First:
            case QueryResultOperator.Last:
            case QueryResultOperator.Single when rows.Count == 0:
                throw new InvalidOperationException("The query returned no elements.");

            case QueryResultOperator.Single:
            case QueryResultOperator.SingleOrDefault:
                throw new InvalidOperationException("The query returned more than one element.");

            default:
                throw GraphQLTranslationException.UnsupportedOperator(plan.ResultOperator.ToString());
        }
    }

    /// <summary>True when the existence check's page came back with a row in it.</summary>
    public static bool Any(GraphQLQueryPlan plan, JsonElement data)
        => Elements(plan, data) is { ValueKind: JsonValueKind.Array } elements
            && elements.GetArrayLength() > 0;

    /// <summary>
    /// Walks past the root field and the paging wrapper the translator emitted, to the array of
    /// elements. A null root field reads as an empty result rather than as an error.
    /// </summary>
    private static JsonElement Elements(GraphQLQueryPlan plan, JsonElement data)
    {
        var field = RootField(plan, data);

        if (field.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return default;

        string? wrapper = plan.Paging switch
        {
            PagingKind.Cursor => "nodes",
            PagingKind.Offset => "items",
            _ => null
        };

        if (wrapper is not null)
        {
            if (!field.TryGetProperty(wrapper, out var wrapped))
                throw new GraphQLTranslationException("FGQL018",
                    $"'{plan.RootField}' returned no '{wrapper}'. The declared PagingKind."
                    + $"{plan.Paging} does not match how the server pages this field.");

            field = wrapped;
        }

        if (field.ValueKind is JsonValueKind.Null)
            return default;

        if (field.ValueKind is not JsonValueKind.Array)
            throw new GraphQLTranslationException("FGQL018",
                $"'{plan.RootField}' returned {field.ValueKind}, not a list of elements.");

        return field;
    }

    private static JsonElement RootField(GraphQLQueryPlan plan, JsonElement data)
        => data.ValueKind is JsonValueKind.Object && data.TryGetProperty(plan.RootField, out var field)
            ? field
            : throw new GraphQLTranslationException("FGQL018",
                $"The response has no '{plan.RootField}' field.");

}
