using Feather.GraphQL.Linq.Filtering;
using Feather.GraphQL.Linq.Query;

namespace Feather.GraphQL.Linq.Tests.Query;

/// <summary>
/// <c>ToGraphQLQuery()</c> writes the arguments out. The parameterized document that actually
/// gets sent says <c>where: $v0</c> and reveals nothing about the filter, which is exactly what
/// makes it useless for debugging.
/// </summary>
[TestFixture]
public class InlineQueryTests
{
    [Test]
    public void The_filter_is_written_into_the_document()
        => Assert.That(Schema.People
                .Where(p => p.Name == "John")
                .ToGraphQLQuery(),
            Is.EqualTo("""query { people(where: {name: {eq: "John"}}) { name age emailAddress } }"""));

    /// <summary>No variables are referenced, so none are declared.</summary>
    [Test]
    public void No_variable_declarations_survive()
        => Assert.That(Schema.People
                .Where(p => p.Name == "John")
                .ToGraphQLQuery(),
            Does.Not.Contain("$v").And.Not.Contains("PersonFilterInput"));

    [Test]
    public void Numbers_and_booleans_print_unquoted()
        => Assert.That(Schema.People
                .Where(p => p.Age > 30)
                .Take(5)
                .ToGraphQLQuery(),
            Is.EqualTo("query { people(where: {age: {gt: 30}}, take: 5) "
                + "{ name age emailAddress } }"));

    /// <summary>
    /// A sort direction is an enum, and an enum literal is a bare name — quoting it would make
    /// it a String and the server would reject the document.
    /// </summary>
    [Test]
    public void Enum_values_print_as_bare_names()
        => Assert.That(Schema.People
                .Where(p => p.Age > 30)
                .OrderBy(p => p.Name)
                .ToGraphQLQuery(),
            Is.EqualTo("query { people(where: {age: {gt: 30}}, order: [{name: ASC}]) "
                + "{ name age emailAddress } }"));

    [Test]
    public void Object_keys_print_unquoted()
        => Assert.That(Schema.People
                .Where(p => p.Age > 30 && p.Name != "Jane")
                .ToGraphQLQuery(),
            Does.Contain("{age: {gt: 30}, name: {neq: \"Jane\"}}"));

    [Test]
    public void Strings_keep_JSON_escaping()
        => Assert.That(Schema.People
                .Where(p => p.Name == "say \"hi\"")
                .ToGraphQLQuery(),
            Does.Contain("""
                         {name: {eq: "say \"hi\""}}
                         """));

    [Test]
    public void Lists_print_as_lists()
        => Assert.That(Schema.People
                .Where(p => new[] { "Ada", "Grace" }.Contains(p.Name))
                .ToGraphQLQuery(),
            Does.Contain("""{name: {in: ["Ada", "Grace"]}}"""));

    [Test]
    public void The_paging_wrapper_and_selection_are_unchanged()
        => Assert.That(Schema.Connected
                .Take(10)
                .ToGraphQLQuery(),
            Is.EqualTo("query { connected(first: 10) { nodes { name } } }"));

    [Test]
    public void Renamed_arguments_are_honoured()
        => Assert.That(Schema.People
                .Where("filter", p => p.Age > 30)
                .ToGraphQLQuery(),
            Does.Contain("people(filter: {age: {gt: 30}})"));

    /// <summary>
    /// The sent document is still parameterized; only the debugging form inlines. Reading one
    /// must not change the other.
    /// </summary>
    [Test]
    public void The_sent_document_is_still_parameterized()
    {
        var query = Schema.People.Where(p => p.Name == "John");

        Assert.Multiple(() =>
        {
            Assert.That(query.ToQueryPlan().Query, Does.Contain("where: $v0"));
            Assert.That(query.ToGraphQLQuery(), Does.Contain("""where: {name: {eq: "John"}}"""));
        });
    }
}
