namespace Feather.GraphQL.Example;

public struct CountryAndContinentsResponse
{
    public IReadOnlyCollection<Country>? Countries { get; init; }
}

public struct Country
{
    public string Name { get; init; }

    public Continent Continent { get; init; }
}

public struct Continent
{
    public string Name { get; init; }
}
