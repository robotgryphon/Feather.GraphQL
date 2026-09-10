using Feather.GraphQL.Linq.Filtering;
using Feather.GraphQL.Linq.Query;

namespace Feather.GraphQL.Linq.Providers.Tests;

/// <summary>
/// Creating a source is one call on a client the app already owns — no registration, no
/// container, nothing to configure twice.
/// </summary>
[TestFixture]
public class SourceCreationTests
{
    [Test]
    public void A_client_becomes_a_source()
    {
        var handler = new StubHandler("""{"data":{"people":[]}}""");

        var source = handler.Client().CreateQueryable();

        Assert.That(source, Is.Not.Null.And.InstanceOf<IGraphQLQueryableSource>());
    }

    [Test]
    public void The_source_hands_out_executable_queryables()
    {
        var handler = new StubHandler("""{"data":{"people":[{"name":"Ada"}]}}""");

        var people = handler.Client().CreateQueryable()
            .Queryable<Person>()
            .Where(p => p.Age > 30)
            .ToArray();

        Assert.That(people[0].Name, Is.EqualTo("Ada"));
    }

    /// <summary>With no path given, the base address is the endpoint.</summary>
    [Test]
    public void The_default_endpoint_is_the_base_address()
    {
        var handler = new StubHandler("""{"data":{"people":[]}}""");
        var client = handler.Client();

        Query(client.CreateQueryable());

        Assert.That(handler.SentRequest!.RequestUri, Is.EqualTo(client.BaseAddress));
    }

    [Test]
    public void An_endpoint_path_can_be_overridden()
    {
        var handler = new StubHandler("""{"data":{"people":[]}}""");

        Query(handler.Client().CreateQueryable("api/v2/graphql"));

        Assert.That(handler.SentRequest!.RequestUri,
            Is.EqualTo(new Uri("https://example.test/api/v2/graphql")));
    }

    /// <summary>For a client already pointed straight at the endpoint.</summary>
    [Test]
    public void A_null_path_posts_to_the_base_address_unchanged()
    {
        var handler = new StubHandler("""{"data":{"people":[]}}""");
        var client = handler.Client("https://example.test/gql");

        Query(client.CreateQueryable(endpointPath: null));

        Assert.That(handler.SentRequest!.RequestUri, Is.EqualTo(client.BaseAddress));
    }

    [Test]
    public void An_empty_path_posts_to_the_base_address_unchanged()
    {
        var handler = new StubHandler("""{"data":{"people":[]}}""");
        var client = handler.Client("https://example.test/gql");

        Query(client.CreateQueryable(""));

        Assert.That(handler.SentRequest!.RequestUri, Is.EqualTo(client.BaseAddress));
    }

    [Test]
    public void An_absolute_path_ignores_the_base_address()
    {
        var handler = new StubHandler("""{"data":{"people":[]}}""");

        Query(handler.Client().CreateQueryable("https://elsewhere.test/graphql"));

        Assert.That(handler.SentRequest!.RequestUri,
            Is.EqualTo(new Uri("https://elsewhere.test/graphql")));
    }

    /// <summary>
    /// HttpClient's own resolution treats a base address as a document, not a directory. Worth
    /// pinning: it is the difference between /v1/graphql and /graphql.
    /// </summary>
    [Test]
    public void A_base_address_keeps_its_path_only_with_a_trailing_slash()
    {
        var withSlash = new StubHandler("""{"data":{"people":[]}}""");
        var without = new StubHandler("""{"data":{"people":[]}}""");

        Query(withSlash.Client("https://example.test/v1/").CreateQueryable("graphql"));
        Query(without.Client("https://example.test/v1").CreateQueryable("graphql"));

        Assert.Multiple(() =>
        {
            Assert.That(withSlash.SentRequest!.RequestUri,
                Is.EqualTo(new Uri("https://example.test/v1/graphql")));
            Assert.That(without.SentRequest!.RequestUri,
                Is.EqualTo(new Uri("https://example.test/graphql")));
        });
    }

    [Test]
    public void A_relative_path_with_no_base_address_says_so()
    {
        var handler = new StubHandler("""{"data":{"people":[]}}""");

        var exception = Assert.Throws<InvalidOperationException>(
            () => Query(handler.RootlessClient().CreateQueryable("graphql")));

        Assert.That(exception!.Message, Does.Contain("BaseAddress"));
    }

    [Test]
    public void An_absolute_path_needs_no_base_address()
    {
        var handler = new StubHandler("""{"data":{"people":[{"name":"Ada"}]}}""");

        var people = handler.RootlessClient()
            .CreateQueryable("https://elsewhere.test/graphql")
            .Queryable<Person>().Where(p => p.Age > 30).ToArray();

        Assert.That(people[0].Name, Is.EqualTo("Ada"));
    }

    /// <summary>
    /// The short way in: no source named, same behaviour.
    /// </summary>
    [Test]
    public void CreateQueryable_executes_without_naming_a_source()
    {
        var handler = new StubHandler("""{"data":{"people":[{"name":"Ada"}]}}""");

        var people = handler.Client()
            .CreateQueryable<Person>()
            .Where(p => p.Age > 30)
            .ToArray();

        Assert.That(people[0].Name, Is.EqualTo("Ada"));
    }

    [Test]
    public void CreateQueryable_posts_to_the_base_address()
    {
        var handler = new StubHandler("""{"data":{"people":[]}}""");
        var client = handler.Client();

        client.CreateQueryable<Person>().Where(p => p.Age > 30).ToArray();

        Assert.That(handler.SentRequest!.RequestUri, Is.EqualTo(client.BaseAddress));
    }

    [Test]
    public void CreateQueryable_takes_an_endpoint_path()
    {
        var handler = new StubHandler("""{"data":{"people":[]}}""");

        handler.Client().CreateQueryable<Person>("api/v2/graphql")
            .Where(p => p.Age > 30).ToArray();

        Assert.That(handler.SentRequest!.RequestUri,
            Is.EqualTo(new Uri("https://example.test/api/v2/graphql")));
    }

    /// <summary>It is the same path in, so the document must come out identical.</summary>
    [Test]
    public void CreateQueryable_translates_the_same_as_a_source()
    {
        var direct = new StubHandler("""{"data":{"people":[]}}""");
        var viaSource = new StubHandler("""{"data":{"people":[]}}""");

        direct.Client().CreateQueryable<Person>().Where(p => p.Age > 30).ToArray();
        Query(viaSource.Client().CreateQueryable());

        Assert.That(direct.SentBody, Is.EqualTo(viaSource.SentBody));
    }

    private static void Query(IGraphQLQueryableSource source)
        => source.Queryable<Person>().Where(p => p.Age > 30).ToArray();

    /// <summary>
    /// Two endpoints are two clients. Keeping them apart is the app's business — a field, a
    /// keyed registration, whatever it already uses for two of anything else.
    /// </summary>
    [Test]
    public void Two_clients_give_two_independent_sources()
    {
        var one = new StubHandler("""{"data":{"people":[{"name":"Ada"}]}}""");
        var two = new StubHandler("""{"data":{"people":[{"name":"Grace"}]}}""");

        var first = one.Client().CreateQueryable();
        var second = two.Client().CreateQueryable();

        Assert.Multiple(() =>
        {
            Assert.That(first.Queryable<Person>().Where(p => p.Age > 30).ToArray()[0].Name,
                Is.EqualTo("Ada"));
            Assert.That(second.Queryable<Person>().Where(p => p.Age > 30).ToArray()[0].Name,
                Is.EqualTo("Grace"));
        });
    }

    /// <summary>A filter dialect is per source, not per process.</summary>
    [Test]
    public void A_filter_provider_can_be_supplied_per_source()
    {
        var handler = new StubHandler("""{"data":{"people":[]}}""");

        var source = handler.Client().CreateQueryable(null, filterProvider: HotChocolateFilterProvider.Instance);

        Assert.That(source, Is.Not.Null);
    }
}
