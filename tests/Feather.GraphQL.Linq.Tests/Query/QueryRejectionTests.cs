using Feather.GraphQL.Linq.Query;

namespace Feather.GraphQL.Linq.Tests.Query;

[TestFixture]
public class QueryRejectionTests
{
    private static string ThrowsWith(TestDelegate action)
        => Assert.Throws<GraphQLTranslationException>(action)!.DiagnosticId;

    [Test]
    public void Unbounded_query_is_FGQL012()
        => Assert.That(ThrowsWith(() => Schema.People.ToGraphQLQuery()),
            Is.EqualTo("FGQL012"));

    /// <summary>
    /// A queryable from elsewhere carries no schema, so translating one without saying how it is
    /// queried has nothing to ask for.
    /// </summary>
    [Test]
    public void A_query_with_no_root_field_is_FGQL011()
        => Assert.That(ThrowsWith(() => Array.Empty<Orphan>().AsQueryable()
                .Where(o => o.Name == "x")
                .ToGraphQLQuery()),
            Is.EqualTo("FGQL011"));

    [Test]
    public void Skip_under_cursor_paging_is_FGQL008()
        => Assert.That(ThrowsWith(() => Schema.Connected.Skip(5).ToGraphQLQuery()),
            Is.EqualTo("FGQL008"));

    [Test]
    public void Computation_in_a_projection_is_FGQL013()
        => Assert.That(ThrowsWith(() => Schema.People
                .Where(p => p.Age > 1)
                .Select(p => new { Shouted = p.Name.ToUpperInvariant() })
                .ToGraphQLQuery()),
            Is.EqualTo("FGQL013"));

    /// <summary>
    /// A nested field is skipped rather than refused: the automatic selection takes the type's
    /// own scalars and leaves the rest to an explicit projection.
    /// </summary>
    [Test]
    public void A_nested_field_is_left_out_of_the_automatic_selection()
        => Assert.That(Schema.Authors.Where(a => a.Name == "x").ToGraphQLQuery(),
            Is.EqualTo("""query { authors(where: {name: {eq: "x"}}) { name } }"""));

    /// <summary>
    /// A client-less queryable still translates, but has nowhere to send — so a terminal that
    /// would execute says so instead of failing somewhere further in.
    /// </summary>
    [Test]
    public void A_terminal_without_an_endpoint_is_FGQL016()
        => Assert.That(ThrowsWith(() => Schema.People.Where(p => p.Age > 1).First()),
            Is.EqualTo("FGQL016"));

    [Test]
    public void Enumerating_without_an_endpoint_is_FGQL016()
        => Assert.That(ThrowsWith(() => Schema.People.Where(p => p.Age > 1).ToList()),
            Is.EqualTo("FGQL016"));
}
