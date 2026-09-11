using Feather.GraphQL.Linq;
using Feather.GraphQL.Linq.Filtering;
using Feather.GraphQL.Linq.Query;

namespace Feather.GraphQL.Linq.Providers.Tests;

/// <summary>
/// Creating a query is one call on a client the app already owns — no registration, no container,
/// and no wrapper between the client and the query.
/// </summary>
[TestFixture]
public class QueryCreationTests
{
    private static void Run(IQueryable<Person> query) => query.Where(p => p.Age > 30).ToArray();

    [Test]
    public void A_client_becomes_a_query()
    {
        var handler = new StubHandler("""{"data":{"people":[{"name":"Ada"}]}}""");

        var people = handler.Client()
            .CreateQueryable<Person>("people")
            .Where(p => p.Age > 30)
            .ToArray();

        Assert.That(people[0].Name, Is.EqualTo("Ada"));
    }

    [Test]
    public void The_root_field_names_the_field_queried()
    {
        var handler = new StubHandler("""{"data":{"humans":[]}}""");

        Run(handler.Client().CreateQueryable<Person>("humans"));

        Assert.That(handler.SentBody, Does.Contain("humans(where: $v0)"));
    }

    [Test]
    public void A_query_with_no_root_field_is_rejected()
        => Assert.Throws<ArgumentException>(
            () => new StubHandler("{}").Client().CreateQueryable<Person>(" "));

    /// <summary>With no path given, the base address is the endpoint.</summary>
    [Test]
    public void The_default_endpoint_is_the_base_address()
    {
        var handler = new StubHandler("""{"data":{"people":[]}}""");
        var client = handler.Client();

        Run(client.CreateQueryable<Person>("people"));

        Assert.That(handler.SentRequest!.RequestUri, Is.EqualTo(client.BaseAddress));
    }

    [Test]
    public void An_endpoint_path_can_be_overridden()
    {
        var handler = new StubHandler("""{"data":{"people":[]}}""");

        Run(handler.Client().CreateQueryable<Person>("people", o => o.EndpointPath = "api/v2/graphql"));

        Assert.That(handler.SentRequest!.RequestUri,
            Is.EqualTo(new Uri("https://example.test/api/v2/graphql")));
    }

    [Test]
    public void An_empty_path_posts_to_the_base_address_unchanged()
    {
        var handler = new StubHandler("""{"data":{"people":[]}}""");
        var client = handler.Client("https://example.test/gql");

        Run(client.CreateQueryable<Person>("people", o => o.EndpointPath = ""));

        Assert.That(handler.SentRequest!.RequestUri, Is.EqualTo(client.BaseAddress));
    }

    [Test]
    public void An_absolute_path_ignores_the_base_address()
    {
        var handler = new StubHandler("""{"data":{"people":[]}}""");

        Run(handler.Client().CreateQueryable<Person>(
            "people", o => o.EndpointPath = "https://elsewhere.test/graphql"));

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

        Run(withSlash.Client("https://example.test/v1/")
            .CreateQueryable<Person>("people", o => o.EndpointPath = "graphql"));
        Run(without.Client("https://example.test/v1")
            .CreateQueryable<Person>("people", o => o.EndpointPath = "graphql"));

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
            () => Run(handler.RootlessClient()
                .CreateQueryable<Person>("people", o => o.EndpointPath = "graphql")));

        Assert.That(exception!.Message, Does.Contain("BaseAddress"));
    }

    [Test]
    public void An_absolute_path_needs_no_base_address()
    {
        var handler = new StubHandler("""{"data":{"people":[{"name":"Ada"}]}}""");

        var people = handler.RootlessClient()
            .CreateQueryable<Person>("people", o => o.EndpointPath = "https://elsewhere.test/graphql")
            .Where(p => p.Age > 30).ToArray();

        Assert.That(people[0].Name, Is.EqualTo("Ada"));
    }

    /// <summary>Two clients stay independent; there is nothing shared between them to leak.</summary>
    [Test]
    public void Two_clients_give_two_independent_queries()
    {
        var one = new StubHandler("""{"data":{"people":[{"name":"Ada"}]}}""");
        var two = new StubHandler("""{"data":{"people":[{"name":"Grace"}]}}""");

        Assert.Multiple(() =>
        {
            Assert.That(one.Client().CreateQueryable<Person>("people")
                .Where(p => p.Age > 30).ToArray()[0].Name, Is.EqualTo("Ada"));
            Assert.That(two.Client().CreateQueryable<Person>("people")
                .Where(p => p.Age > 30).ToArray()[0].Name, Is.EqualTo("Grace"));
        });
    }

    [Test]
    public void A_filter_provider_can_be_supplied_per_query()
    {
        var handler = new StubHandler("""{"data":{"people":[]}}""");

        Run(handler.Client().CreateQueryable<Person>(
            "people", o => o.FilterProvider = HotChocolateFilterProvider.Instance));

        Assert.That(handler.SentBody, Does.Contain("""{"age":{"gt":30}}"""));
    }

    [Test]
    public void The_paging_kind_reaches_the_query()
    {
        var handler = new StubHandler("""{"data":{"people":{"totalCount":3}}}""");

        int count = handler.Client()
            .CreateQueryable<Person>("people", o => o.Paging = PagingKind.Cursor)
            .Count();

        Assert.That(count, Is.EqualTo(3));
    }

    /// <summary>
    /// Options built elsewhere work the same way, which is what makes a container's
    /// <c>IOptions&lt;T&gt;</c> usable without this library knowing about the container.
    /// </summary>
    [Test]
    public void Options_can_be_supplied_as_an_instance()
    {
        var handler = new StubHandler("""{"data":{"people":[]}}""");
        var options = new GraphQLHttpQueryOptions { RootField = "people", EndpointPath = "api/graphql" };

        Run(handler.Client().CreateQueryable<Person>(options));

        Assert.That(handler.SentRequest!.RequestUri,
            Is.EqualTo(new Uri("https://example.test/api/graphql")));
    }

    /// <summary>
    /// A container's options instance is shared, so the root field is taken from the call and the
    /// instance is left untouched.
    /// </summary>
    [Test]
    public void Configured_options_are_not_mutated_by_a_query()
    {
        var handler = new StubHandler("""{"data":{"humans":[]}}""");
        var configured = new GraphQLHttpQueryOptions { EndpointPath = "api/graphql" };

        Run(handler.Client().CreateQueryable<Person>("humans", new StubOptions(configured)));

        Assert.Multiple(() =>
        {
            Assert.That(handler.SentBody, Does.Contain("humans(where: $v0)"));
            Assert.That(configured.RootField, Is.Null, "the shared instance must not be written to");
        });
    }
}
