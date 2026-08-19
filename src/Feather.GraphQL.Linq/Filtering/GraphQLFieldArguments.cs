using System.Text.Json.Nodes;

namespace Feather.GraphQL.Linq.Filtering;

/// <summary>
/// The field arguments a LINQ chain lowers to. Returned by
/// <c>ToGraphQLArguments()</c> for callers translating a whole query rather than
/// just its predicate.
/// </summary>
public sealed record GraphQLFieldArguments
{
    /// <summary>The <c>where:</c> argument, or null when the chain has no predicate.</summary>
    public JsonObject? Where { get; init; }

    /// <summary>The <c>order:</c> argument, or null when the chain is unordered.</summary>
    public JsonArray? Order { get; init; }

    public int? Skip { get; init; }

    public int? Take { get; init; }

    public bool IsEmpty => Where is null && Order is null && Skip is null && Take is null;
}
