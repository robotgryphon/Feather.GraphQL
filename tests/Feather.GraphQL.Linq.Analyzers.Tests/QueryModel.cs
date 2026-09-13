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

/// <inheritdoc cref="Country"/>
public class Continent
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
}
