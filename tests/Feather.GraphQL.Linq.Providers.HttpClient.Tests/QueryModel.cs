namespace Feather.GraphQL.Linq.Providers.Tests;

/// <summary>A plain POCO. How it is queried is said at the call site.</summary>
public class Person
{
    public required string Name { get; init; }
    public int Age { get; init; }
}
