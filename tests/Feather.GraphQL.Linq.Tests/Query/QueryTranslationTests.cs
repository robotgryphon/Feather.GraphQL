using Feather.GraphQL.Linq.Query;

namespace Feather.GraphQL.Linq.Tests.Query;

/// <summary>§5: the chain becomes a request. Golden text plus the variables payload.</summary>
[TestFixture]
public class QueryTranslationTests
{
    [Test]
    public void Filtered_query_binds_the_filter_to_a_variable()
    {
        var request = GraphQLQueryable.For<Person>()
            .Where(p => p.Name == "John")
            .ToGraphQLRequest();

        Assert.Multiple(() =>
        {
            Assert.That(request.Query, Is.EqualTo(
                "query($v0: PersonFilterInput) { people(where: $v0) { name age emailAddress } }"));
            Assert.That(Variables(request), Is.EqualTo("""{"v0":{"name":{"eq":"John"}}}"""));
        });
    }

    [Test]
    public void JsonIgnore_members_are_not_selected()
        => Assert.That(GraphQLQueryable.For<Person>().Where(p => p.Age > 1).ToGraphQLRequest().Query,
            Does.Not.Contain("secret"));

    [Test]
    public void Projection_determines_the_selection_set()
        => Assert.That(GraphQLQueryable.For<Person>()
                .Where(p => p.Age > 30)
                .Select(p => new { p.Name, p.Email })
                .ToGraphQLRequest().Query,
            Is.EqualTo("query($v0: PersonFilterInput) { people(where: $v0) { name emailAddress } }"));

    [Test]
    public void Nested_projection_nests_the_selection_set()
        => Assert.That(GraphQLQueryable.For<Author>()
                .Where(a => a.Name == "Le Guin")
                .Select(a => new { a.Name, Titles = a.Books.Select(b => b.Title) })
                .ToGraphQLRequest().Query,
            Is.EqualTo("query($v0: AuthorFilterInput) { authors(where: $v0) { name books { title } } }"));

    [Test]
    public void Ordering_and_paging_bind_their_own_variables()
    {
        var request = GraphQLQueryable.For<Person>()
            .Where(p => p.Age > 30)
            .OrderBy(p => p.Name)
            .Take(5)
            .ToGraphQLRequest();

        Assert.Multiple(() =>
        {
            Assert.That(request.Query, Is.EqualTo(
                "query($v0: PersonFilterInput, $v1: [PersonSortInput!], $v2: Int) "
                + "{ people(where: $v0, order: $v1, take: $v2) { name age emailAddress } }"));
            Assert.That(Variables(request), Is.EqualTo(
                """{"v0":{"age":{"gt":30}},"v1":[{"name":"ASC"}],"v2":5}"""));
        });
    }

    [Test]
    public void Cursor_paging_wraps_the_selection_in_nodes_and_takes_first()
        => Assert.That(GraphQLQueryable.For<CursorPerson>().Take(10).ToGraphQLRequest().Query,
            Is.EqualTo("query($v0: Int) { connected(first: $v0) { nodes { name } } }"));

    [Test]
    public void Offset_paging_wraps_the_selection_in_items()
        => Assert.That(GraphQLQueryable.For<OffsetPerson>().Skip(5).Take(10).ToGraphQLRequest().Query,
            Is.EqualTo("query($v0: Int, $v1: Int) { offset(take: $v0, skip: $v1) { items { name } } }"));

    /// <summary>
    /// §5.3's payoff: predicate shape lives in the variables, so differently-shaped predicates
    /// share one document and therefore one APQ hash.
    /// </summary>
    [Test]
    public void Different_predicates_over_one_selection_set_produce_identical_query_text()
    {
        string first = GraphQLQueryable.For<Person>().Where(p => p.Name == "John").ToGraphQLRequest().Query!;
        string second = GraphQLQueryable.For<Person>()
            .Where(p => p.Age > 30 && p.Name != "Jane")
            .ToGraphQLRequest().Query!;

        Assert.That(second, Is.EqualTo(first));
    }

    [Test]
    public void Equivalent_chains_hash_identically()
    {
        var single = new Primitives.GraphQLQuery(
            GraphQLQueryable.For<Person>().Where(p => p.Age > 30 && p.Name == "John")
                .ToGraphQLRequest().Query!);

        var split = new Primitives.GraphQLQuery(
            GraphQLQueryable.For<Person>().Where(p => p.Age > 30).Where(p => p.Name == "John")
                .ToGraphQLRequest().Query!);

        Assert.That(split.Sha256Hash, Is.EqualTo(single.Sha256Hash));
    }

    private static string Variables(Feather.GraphQL.Request.GraphQLRequest request)
        => System.Text.Json.JsonSerializer.Serialize(request.Variables);
}
