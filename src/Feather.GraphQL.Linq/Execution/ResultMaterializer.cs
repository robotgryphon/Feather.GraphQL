using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Feather.GraphQL.Linq.Expressions;
using Feather.GraphQL.Linq.Query;

namespace Feather.GraphQL.Linq.Execution;

/// <summary>
/// Reads a response body into the result the chain's terminal asked for.
/// </summary>
/// <remarks>
/// The projection is applied here rather than being deserialized into directly: an anonymous
/// type has no accessible setters and no parameterless constructor, so the elements are
/// materialized as the queried type first and the compiled <c>Select</c> runs over them. That
/// also keeps one rule for field naming — the one the selection set was built from.
/// </remarks>
internal static class ResultMaterializer
{
    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { NothingIsRequired } }
    };

    /// <summary>
    /// A projection asks for a subset of the type's fields, so the response legitimately omits
    /// the rest — including members marked <c>required</c>, which the serializer would otherwise
    /// refuse to leave unset. Requiredness is the server's contract to enforce, not the
    /// materializer's, and enforcing it here would make <c>Select(p =&gt; p.Name)</c> fail on any
    /// type with a required member.
    /// </summary>
    private static void NothingIsRequired(JsonTypeInfo info)
    {
        if (info.Kind is not JsonTypeInfoKind.Object)
            return;

        foreach (var property in info.Properties)
            property.IsRequired = false;
    }

    /// <summary>Materializes the rows the query returned, in server order.</summary>
    public static List<TOut> Rows<TOut>(GraphQLQueryPlan plan, JsonElement data)
    {
        var elements = Elements(plan, data);

        if (elements.ValueKind is not JsonValueKind.Array)
            return [];

        var rows = new List<TOut>(elements.GetArrayLength());

        // No Select: the queried type is the result type, so one deserialization does it.
        if (plan.Projection is null)
        {
            foreach (var element in elements.EnumerateArray())
                rows.Add(Deserialize<TOut>(element));

            return rows;
        }

        var project = plan.Projection.Compile();
        foreach (var element in elements.EnumerateArray())
        {
            object? source = element.Deserialize(plan.ElementType, _options);
            rows.Add((TOut)project.DynamicInvoke(source)!);
        }

        return rows;
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

    private static TOut Deserialize<TOut>(JsonElement element)
        => element.Deserialize<TOut>(_options)!;
}
