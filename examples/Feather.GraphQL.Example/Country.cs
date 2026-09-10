using Feather.GraphQL.Linq;

namespace Feather.GraphQL.Example;

/// <summary>
/// Bound to the <c>countries</c> field on the schema's <c>Query</c> type. The attribute is what
/// makes <c>Queryable&lt;Country&gt;()</c> translatable — without it, translation is FGQL011.
/// </summary>
[GenerateQueryable("countries")]
public struct Country
{
    public string Name { get; init; }

    public Continent Continent { get; init; }
}
