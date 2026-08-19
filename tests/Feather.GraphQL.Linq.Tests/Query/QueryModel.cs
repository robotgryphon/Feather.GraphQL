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
