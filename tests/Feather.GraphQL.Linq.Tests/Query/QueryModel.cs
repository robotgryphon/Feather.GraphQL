using Feather.GraphQL.Linq.Filtering;
using Feather.GraphQL.Linq.Query;
using System.Text.Json.Serialization;

namespace Feather.GraphQL.Linq.Tests.Query;

/// <summary>
/// How each model is queried. The types are plain POCOs; what the schema calls them lives here,
/// next to the tests that use it.
/// </summary>
internal static class Schema
{
    public static IQueryable<Person> People => GraphQLQueryable.For<Person>("people");

    public static IQueryable<CursorPerson> Connected => GraphQLQueryable.For<CursorPerson>(
        "connected", o => { o.Paging = PagingKind.Cursor; o.FilterInput = "PersonFilterInput"; });

    public static IQueryable<OffsetPerson> Offset => GraphQLQueryable.For<OffsetPerson>(
        "offset", o => { o.Paging = PagingKind.Offset; o.FilterInput = "PersonFilterInput"; });

    public static IQueryable<Author> Authors => GraphQLQueryable.For<Author>("authors");

    public static IQueryable<Country> Countries => GraphQLQueryable.For<Country>("countries");

    public static IQueryable<Gadget> Gadgets => GraphQLQueryable.For<Gadget>("gadgets");
}

public class Person
{
    public required string Name { get; init; }
    public int Age { get; init; }

    [JsonPropertyName("emailAddress")]
    public string? Email { get; init; }

    [JsonIgnore]
    public string Secret { get; init; } = "";
}

public class CursorPerson
{
    public required string Name { get; init; }
}

public class OffsetPerson
{
    public required string Name { get; init; }
}

/// <summary>Carries a nested field, so it cannot use the no-Select default.</summary>
public class Author
{
    public required string Name { get; init; }
    public List<Book> Books { get; init; } = [];
}

public class Book
{
    public required string Title { get; init; }
    public int Pages { get; init; }
}

/// <summary>Never given a root field, so translating a query over it is FGQL011.</summary>
public class Orphan
{
    public required string Name { get; init; }
}

/// <summary>
/// Nests its continent, the way the countries API's <c>Country</c> does.
/// </summary>
public class Country
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
public class Gadget
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
