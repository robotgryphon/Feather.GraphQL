using System.Text.Json;
using Feather.GraphQL.Linq.Query;

namespace Feather.GraphQL.Linq.Materialization;

/// <summary>
/// Turns a response's <c>data</c> into the elements the chain asked for.
/// </summary>
internal static class ResultMaterializer
{
    public static T[] Materialize<T>(TranslatedQuery query, JsonElement data)
    {
        var items = Unwrap(query, data);

        if (items.ValueKind == JsonValueKind.Null)
            return [];

        if (items.ValueKind != JsonValueKind.Array)
            throw Mismatch(query, $"'{Describe(query)}' is {items.ValueKind}, not a list");

        var read = Reader<T>(query);
        var results = new T[items.GetArrayLength()];
        int index = 0;

        foreach (var element in items.EnumerateArray())
            results[index++] = read(element);

        return results;
    }

    /// <summary>
    /// A chain without <c>Select</c> reads straight into its element type — the selection set was
    /// built from that type's own field names, so the two agree by construction. A projection has
    /// no such guarantee and goes through the binder.
    /// </summary>
    private static Func<JsonElement, T> Reader<T>(TranslatedQuery query)
        => query.Projection is null
            ? static element => element.Deserialize<T>(GraphQLJsonOptions.Instance)!
            : ProjectionBinder.Compile<T>(query.Projection);

    /// <summary>Opens <c>data</c> down to the list: the root field, then any paging wrapper.</summary>
    private static JsonElement Unwrap(TranslatedQuery query, JsonElement data)
    {
        if (data.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            throw Mismatch(query, "the response carried no 'data'");

        var root = Property(query, data, query.RootField, "the response");

        return query.Paging switch
        {
            PagingKind.Cursor => Property(query, root, "nodes", $"'{query.RootField}'"),
            PagingKind.Offset => Property(query, root, "items", $"'{query.RootField}'"),
            _ => root
        };
    }

    private static JsonElement Property(TranslatedQuery query, JsonElement parent, string name, string owner)
    {
        if (parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value))
            return value;

        throw Mismatch(query, $"{owner} has no '{name}' field");
    }

    private static string Describe(TranslatedQuery query)
        => query.Paging switch
        {
            PagingKind.Cursor => $"{query.RootField}.nodes",
            PagingKind.Offset => $"{query.RootField}.items",
            _ => query.RootField
        };

    /// <summary>
    /// FGQL015. Almost always a schema disagreement rather than a bug in the chain — the wrong
    /// <see cref="PagingKind"/> on <c>[GenerateQueryable]</c> being the usual one — so the message
    /// names the path that was expected.
    /// </summary>
    private static GraphQLTranslationException Mismatch(TranslatedQuery query, string detail)
        => new("FGQL015",
            $"The response does not match the query over '{query.ElementType.Name}': {detail}. "
            + $"Expected a list at 'data.{Describe(query)}'.");
}
