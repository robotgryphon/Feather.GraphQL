using System.Text.Json.Serialization;
using Feather.GraphQL.Linq;

namespace Feather.GraphQL.Linq.Tests.Query;

[GenerateQueryable("people", FilterInput = "PersonFilterInput", SortInput = "PersonSortInput")]
public partial class Person
{
    public required string Name { get; init; }
    public int Age { get; init; }

    [JsonPropertyName("emailAddress")]
    public string? Email { get; init; }

    [JsonIgnore]
    public string Secret { get; init; } = "";
}

[GenerateQueryable("connected", Paging = PagingKind.Cursor, FilterInput = "PersonFilterInput")]
public partial class CursorPerson
{
    public required string Name { get; init; }
}

[GenerateQueryable("offset", Paging = PagingKind.Offset, FilterInput = "PersonFilterInput")]
public partial class OffsetPerson
{
    public required string Name { get; init; }
}

/// <summary>Carries a nested field, so it cannot use the no-Select default.</summary>
[GenerateQueryable("authors", FilterInput = "AuthorFilterInput")]
public partial class Author
{
    public required string Name { get; init; }
    public List<Book> Books { get; init; } = [];
}

public class Book
{
    public required string Title { get; init; }
    public int Pages { get; init; }
}

/// <summary>No [GenerateQueryable]: filterable, but not a query root.</summary>
public class Orphan
{
    public required string Name { get; init; }
}

/// <summary>
/// Nests its continent, the way the countries API's <c>Country</c> does.
/// </summary>
[GenerateQueryable("countries", FilterInput = "CountryFilterInput")]
public partial class Country
{
    public required string Name { get; init; }
    public required CountryContinent Continent { get; init; }
}

public class CountryContinent
{
    public required string Name { get; init; }
}

/// <summary>
/// Models <c>CountryFilterInput</c>, which takes a string filter on <c>continent</c> rather than
/// the nested object the query returns.
/// </summary>
public class CountryFilter
{
    public string? Continent { get; set; }

    [JsonPropertyName("currency")]
    public string? CurrencyCode { get; set; }
}

/// <summary>
/// Shaped like the Pokémon API's <c>pokemon</c>: an object member, a scalar list, and a member
/// that nests a level further into collections of objects.
/// </summary>
[GenerateQueryable("gadgets", FilterInput = "GadgetFilterInput")]
public partial class Gadget
{
    public required string Name { get; init; }
    public Dimensions? Size { get; init; }
    public string[] Tags { get; init; } = [];
    public GadgetParts? Parts { get; init; }

    /// <summary>Points back at <see cref="Gadget"/>, so expanding it must not recurse.</summary>
    public NestedGadget? Nested { get; init; }
}

public class NestedGadget
{
    public required string Label { get; init; }
    public IReadOnlyCollection<Gadget> Gadgets { get; init; } = [];
}

public class Dimensions
{
    public required string Minimum { get; init; }
    public required string Maximum { get; init; }
}

public class GadgetParts
{
    public Part[] Primary { get; init; } = [];
    public List<Part> Secondary { get; init; } = [];
}

public class Part
{
    public required string Name { get; init; }
    public int Weight { get; init; }
}
