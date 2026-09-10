using Feather.GraphQL.Linq;

namespace Feather.GraphQL.Linq.Providers.Tests;

[GenerateQueryable("people", FilterInput = "PersonFilterInput", SortInput = "PersonSortInput")]
public partial class Person
{
    public required string Name { get; init; }
    public int Age { get; init; }
}
