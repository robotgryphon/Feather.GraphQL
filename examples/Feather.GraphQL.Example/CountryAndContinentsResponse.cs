namespace Feather.GraphQL.Example;

public struct CountryAndContinentsResponse
{
    public IReadOnlyCollection<Country>? Countries { get; init; }
}
