namespace Feather.GraphQL.Example;

/// <summary>
/// A plain POCO. Nothing on it says how it is queried — that is a property of the schema, and
/// lives at the call site in <c>CreateQueryable&lt;Country&gt;("countries")</c>.
/// </summary>
public struct Country
{
    public string Name { get; init; }

    public Continent Continent { get; init; }
}
