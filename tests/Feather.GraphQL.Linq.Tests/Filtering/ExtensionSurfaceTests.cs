using Feather.GraphQL.Linq.Filtering;
using Feather.GraphQL.Linq.Tests.Where;

namespace Feather.GraphQL.Linq.Tests.Filtering;

/// <summary>
/// §4.1: which operators each extension consumes, and its refusal to silently drop the rest.
/// </summary>
[TestFixture]
public class ExtensionSurfaceTests
{
    private static IQueryable<Person> People => Array.Empty<Person>().AsQueryable();

    private static string ThrowsWith(TestDelegate action)
        => Assert.Throws<GraphQLTranslationException>(action)!.DiagnosticId;

    [Test]
    public void No_predicate_lowers_to_null()
        => Assert.That(People.ToGraphQLFilter(), Is.Null);

    [Test]
    public void Ordering_lowers_in_chain_order()
        => Assert.That(People.OrderBy(p => p.Name).ThenByDescending(p => p.Age)
                .ToGraphQLSort()?.ToJsonString(),
            Is.EqualTo("""[{"name":"ASC"},{"age":"DESC"}]"""));

    [Test]
    public void Arguments_consume_filter_ordering_and_paging()
    {
        var arguments = People
            .Where(p => p.Age > 30)
            .OrderBy(p => p.Name)
            .Skip(10)
            .Take(5)
            .ToGraphQLArguments();

        Assert.Multiple(() =>
        {
            Assert.That(arguments.Where?.ToJsonString(), Is.EqualTo("""{"age":{"gt":30}}"""));
            Assert.That(arguments.Order?.ToJsonString(), Is.EqualTo("""[{"name":"ASC"}]"""));
            Assert.That(arguments.Skip, Is.EqualTo(10));
            Assert.That(arguments.Take, Is.EqualTo(5));
        });
    }

    [Test]
    public void ToGraphQLFilter_refuses_to_silently_drop_Take()
    {
        var exception = Assert.Throws<GraphQLTranslationException>(
            () => People.Where(p => p.Age > 1).Take(5).ToGraphQLFilter());

        Assert.Multiple(() =>
        {
            Assert.That(exception!.DiagnosticId, Is.EqualTo("FGQL001"));
            Assert.That(exception.Message, Does.Contain("ToGraphQLArguments"));
        });
    }

    [Test]
    public void ToGraphQLFilter_refuses_to_silently_drop_OrderBy()
        => Assert.That(ThrowsWith(() => People.Where(p => p.Age > 1).OrderBy(p => p.Name).ToGraphQLFilter()),
            Is.EqualTo("FGQL001"));

    [Test]
    public void ToGraphQLSort_refuses_to_silently_drop_Where()
        => Assert.That(ThrowsWith(() => People.Where(p => p.Age > 1).OrderBy(p => p.Name).ToGraphQLSort()),
            Is.EqualTo("FGQL001"));
}
