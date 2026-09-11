using Feather.GraphQL.Linq.Filtering;
using Feather.GraphQL.Linq.Query;

namespace Feather.GraphQL.Linq.Tests.Query;

/// <summary>
/// Argument names are a fact about one schema, so a chain can state them. The defaults are
/// HotChocolate's; nothing else in the translation changes.
/// </summary>
[TestFixture]
public class ArgumentNameTests
{
    [Test]
    public void The_filter_argument_can_be_renamed()
        => Assert.That(Schema.People
                .WithGraphQLArguments(new() { Filter = "filter" })
                .Where(p => p.Name == "John")
                .ToQueryPlan().Query,
            Is.EqualTo("query($v0: PersonFilterInput) { people(filter: $v0) { name age emailAddress } }"));

    /// <summary>Renaming one argument leaves the rest at their defaults.</summary>
    [Test]
    public void Unnamed_arguments_keep_their_defaults()
        => Assert.That(Schema.People
                .WithGraphQLArguments(new() { Filter = "filter" })
                .Where(p => p.Age > 30)
                .OrderBy(p => p.Name)
                .Take(5)
                .ToQueryPlan().Query,
            Is.EqualTo("query($v0: PersonFilterInput, $v1: [PersonSortInput!], $v2: Int) "
                + "{ people(filter: $v0, order: $v1, take: $v2) { name age emailAddress } }"));

    [Test]
    public void Every_argument_can_be_renamed()
        => Assert.That(Schema.People
                .WithGraphQLArguments(new() { Filter = "filter", Order = "sort", Take = "limit", Skip = "offset" })
                .Where(p => p.Age > 30)
                .OrderBy(p => p.Name)
                .Skip(10)
                .Take(5)
                .ToQueryPlan().Query,
            Is.EqualTo("query($v0: PersonFilterInput, $v1: [PersonSortInput!], $v2: Int, $v3: Int) "
                + "{ people(filter: $v0, sort: $v1, limit: $v2, offset: $v3) { name age emailAddress } }"));

    [Test]
    public void Cursor_paging_renames_first_rather_than_take()
        => Assert.That(Schema.Connected
                .WithGraphQLArguments(new() { First = "head", Take = "limit" })
                .Take(10)
                .ToQueryPlan().Query,
            Is.EqualTo("query($v0: Int) { connected(head: $v0) { nodes { name } } }"));

    /// <summary>It composes anywhere, because it configures rather than filters.</summary>
    [Test]
    public void It_can_be_called_anywhere_in_the_chain()
    {
        string early = Schema.People
            .WithGraphQLArguments(new() { Filter = "filter" })
            .Where(p => p.Name == "John")
            .ToQueryPlan().Query;

        string late = Schema.People
            .Where(p => p.Name == "John")
            .WithGraphQLArguments(new() { Filter = "filter" })
            .ToQueryPlan().Query;

        Assert.That(late, Is.EqualTo(early));
    }

    [Test]
    public void The_last_call_wins()
        => Assert.That(Schema.People
                .WithGraphQLArguments(new() { Filter = "filter" })
                .Where(p => p.Name == "John")
                .WithGraphQLArguments(new() { Filter = "criteria" })
                .ToQueryPlan().Query,
            Does.Contain("people(criteria: $v0)"));

    /// <summary>
    /// The names ride in the expression tree, so this works over a queryable this library did
    /// not create — the same reason the §4 filter extensions do.
    /// </summary>
    /// <summary>
    /// A queryable this library did not create knows nothing about the schema, so the terminal
    /// is told instead.
    /// </summary>
    [Test]
    public void It_works_over_a_foreign_queryable()
        => Assert.That(Array.Empty<Person>().AsQueryable()
                .WithGraphQLArguments(new() { Filter = "filter" })
                .Where(p => p.Name == "John")
                .ToQueryPlan(new GraphQLQueryOptions { RootField = "people" }).Query,
            Does.Contain("people(filter: $v0)"));

    [Test]
    public void Saying_nothing_keeps_the_HotChocolate_names()
        => Assert.That(Schema.People
                .Where(p => p.Name == "John")
                .ToQueryPlan().Query,
            Does.Contain("people(where: $v0)"));
}
