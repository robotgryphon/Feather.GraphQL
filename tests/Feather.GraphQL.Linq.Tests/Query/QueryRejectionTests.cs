using Feather.GraphQL.Linq.Query;

namespace Feather.GraphQL.Linq.Tests.Query;

[TestFixture]
public class QueryRejectionTests
{
    private static string ThrowsWith(TestDelegate action)
        => Assert.Throws<GraphQLTranslationException>(action)!.DiagnosticId;

    [Test]
    public void Unbounded_query_is_FGQL012()
        => Assert.That(ThrowsWith(() => GraphQLQueryable.For<Person>().ToGraphQLRequest()),
            Is.EqualTo("FGQL012"));

    [Test]
    public void Type_without_GenerateQueryable_is_FGQL011()
        => Assert.That(ThrowsWith(() => GraphQLQueryable.For<Orphan>().Where(o => o.Name == "x").ToGraphQLRequest()),
            Is.EqualTo("FGQL011"));

    [Test]
    public void Skip_under_cursor_paging_is_FGQL008()
        => Assert.That(ThrowsWith(() => GraphQLQueryable.For<CursorPerson>().Skip(5).ToGraphQLRequest()),
            Is.EqualTo("FGQL008"));

    [Test]
    public void Computation_in_a_projection_is_FGQL013()
        => Assert.That(ThrowsWith(() => GraphQLQueryable.For<Person>()
                .Where(p => p.Age > 1)
                .Select(p => new { Shouted = p.Name.ToUpperInvariant() })
                .ToGraphQLRequest()),
            Is.EqualTo("FGQL013"));

    [Test]
    public void Nested_field_without_a_projection_is_FGQL014()
        => Assert.That(ThrowsWith(() => GraphQLQueryable.For<Author>().Where(a => a.Name == "x").ToGraphQLRequest()),
            Is.EqualTo("FGQL014"));

    [Test]
    public void Result_operators_have_no_translation()
        => Assert.That(ThrowsWith(() => GraphQLQueryable.For<Person>().Where(p => p.Age > 1).First()),
            Is.EqualTo("FGQL001"));

    [Test]
    public void Enumerating_a_queryable_throws_rather_than_executing()
        => Assert.That(ThrowsWith(() => GraphQLQueryable.For<Person>().Where(p => p.Age > 1).ToList()),
            Is.EqualTo("FGQL001"));
}
