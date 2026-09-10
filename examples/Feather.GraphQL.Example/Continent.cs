namespace Feather.GraphQL.Example;

public struct Continent
{
    public string Code { get; init; }

    public string Name { get; init; }

    public IReadOnlyCollection<Country> Countries { get; set; }
    
    public override string ToString() => $"Code: {Code}, Name: {Name}";
}
