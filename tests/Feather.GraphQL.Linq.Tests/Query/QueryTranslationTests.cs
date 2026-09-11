using Feather.GraphQL.Linq.Query;

namespace Feather.GraphQL.Linq.Tests.Query;

/// <summary>
/// §5: the chain becomes a query document. Golden text plus the variables payload.
/// </summary>
/// <remarks>
/// These assert on <c>ToQueryPlan().Query</c> — the parameterized document that is actually
/// sent, and the one to hash for APQ. <c>ToGraphQLQuery()</c> inlines the values
/// instead and is covered by <see cref="InlineQueryTests"/>.
/// </remarks>
[TestFixture]
public class QueryTranslationTests
{
    [Test]
    public void Filtered_query_binds_the_filter_to_a_variable()
    {
        var plan = Schema.People
            .Where(p => p.Name == "John")
            .ToQueryPlan();

        Assert.Multiple(() =>
        {
            Assert.That(plan.Query, Is.EqualTo(
                "query($v0: PersonFilterInput) { people(where: $v0) { name age emailAddress } }"));
            Assert.That(Variables(plan), Is.EqualTo("""{"v0":{"name":{"eq":"John"}}}"""));
        });
    }

    /// <summary>
    /// A chain with no Where, Take or Select asks for the whole collection, which the translator
    /// used to refuse outright. It is a warning at the call site now — sometimes the top-level
    /// fields are exactly what is wanted — so what it translates to is worth stating.
    /// </summary>
    [Test]
    public void An_unbounded_query_selects_the_type_s_own_scalars()
        => Assert.That(Schema.People.ToQueryPlan().Query,
            Is.EqualTo("query { people { name age emailAddress } }"));

    [Test]
    public void JsonIgnore_members_are_not_selected()
        => Assert.That(Schema.People.Where(p => p.Age > 1).ToQueryPlan().Query,
            Does.Not.Contain("secret"));

    [Test]
    public void Projection_determines_the_selection_set()
        => Assert.That(Schema.People
                .Where(p => p.Age > 30)
                .Select(p => new { p.Name, p.Email })
                .ToQueryPlan().Query,
            Is.EqualTo("query($v0: PersonFilterInput) { people(where: $v0) { name emailAddress } }"));

    [Test]
    public void Nested_projection_nests_the_selection_set()
        => Assert.That(Schema.Authors
                .Where(a => a.Name == "Le Guin")
                .Select(a => new { a.Name, Titles = a.Books.Select(b => b.Title) })
                .ToQueryPlan().Query,
            Is.EqualTo("query($v0: AuthorFilterInput) { authors(where: $v0) { name books { title } } }"));

    [Test]
    public void Ordering_and_paging_bind_their_own_variables()
    {
        var plan = Schema.People
            .Where(p => p.Age > 30)
            .OrderBy(p => p.Name)
            .Take(5)
            .ToQueryPlan();

        Assert.Multiple(() =>
        {
            Assert.That(plan.Query, Is.EqualTo(
                "query($v0: PersonFilterInput, $v1: [PersonSortInput!], $v2: Int) "
                + "{ people(where: $v0, order: $v1, take: $v2) { name age emailAddress } }"));
            Assert.That(Variables(plan), Is.EqualTo(
                """{"v0":{"age":{"gt":30}},"v1":[{"name":"ASC"}],"v2":5}"""));
        });
    }

    [Test]
    public void Cursor_paging_wraps_the_selection_in_nodes_and_takes_first()
        => Assert.That(Schema.Connected.Take(10).ToQueryPlan().Query,
            Is.EqualTo("query($v0: Int) { connected(first: $v0) { nodes { name } } }"));

    [Test]
    public void Offset_paging_wraps_the_selection_in_items()
        => Assert.That(Schema.Offset.Skip(5).Take(10).ToQueryPlan().Query,
            Is.EqualTo("query($v0: Int, $v1: Int) { offset(take: $v0, skip: $v1) { items { name } } }"));

    /// <summary>
    /// §5.3's payoff: predicate shape lives in the variables, so differently-shaped predicates
    /// share one document and therefore one APQ hash.
    /// </summary>
    [Test]
    public void Different_predicates_over_one_selection_set_produce_identical_query_text()
    {
        string first = Schema.People.Where(p => p.Name == "John").ToQueryPlan().Query;
        string second = Schema.People
            .Where(p => p.Age > 30 && p.Name != "Jane")
            .ToQueryPlan().Query;

        Assert.That(second, Is.EqualTo(first));
    }

    /// <summary>
    /// Same shape, same text — which is what makes one APQ hash cover both. The hash itself is
    /// the caller's to compute now that the document is plain text.
    /// </summary>
    [Test]
    public void Equivalent_chains_print_identically()
    {
        var single = Schema.People
            .Where(p => p.Age > 30 && p.Name == "John")
            .ToQueryPlan().Query;

        var split = Schema.People
            .Where(p => p.Age > 30).Where(p => p.Name == "John")
            .ToQueryPlan().Query;

        Assert.That(split, Is.EqualTo(single));
    }

    private static string Variables(GraphQLQueryPlan plan)
        => System.Text.Json.JsonSerializer.Serialize(plan.Variables);
}
