namespace Feather.GraphQL.Linq.Analyzers.Tests;

/// <summary>The model the corpus queries over, shared by both halves of every comparison.</summary>
public class Country
{
    public string Name { get; set; } = "";
    public string Code { get; set; } = "";
    public Continent Continent { get; set; } = new();
}

/// <summary>The server's filter input, whose shape is not the element's.</summary>
public class CountryFilter
{
    public string Continent { get; set; } = "";
}

/// <summary>
/// A filter over a value whose schema name cannot be guessed.
/// </summary>
/// <remarks>
/// <c>char</c> is a schema's choice between a one-character <c>String</c> and a number, and
/// <see cref="Feather.GraphQL.Linq.Analyzers.GraphQLTypeFacts.ScalarName"/> declines to make it.
/// A filter over one therefore stays in a variable of the filter input's own type, where nothing
/// has to name the value at all.
/// </remarks>
public class InitialFilter
{
    public char Initial { get; set; }
}

/// <inheritdoc cref="Country"/>
/// <remarks>
/// Holds many of something, so a projection has a nested sequence to reach into — and one that
/// leads back to the queried type, which is the shape a schema usually has.
/// </remarks>
public class Continent
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public Country[] Countries { get; set; } = [];
}
