using System.Net;
using Feather.GraphQL.Http;
using Feather.GraphQL.Linq.Query;

namespace Feather.GraphQL.Linq.Providers.Tests;

/// <summary>
/// The seam between the LINQ provider and the HTTP client: what goes on the wire, and what a
/// server's answer turns into.
/// </summary>
[TestFixture]
public class HttpExecutorTests
{
    private static IGraphQLQueryableSource Source(StubHandler handler)
        => handler.Client().CreateQueryable();

    [Test]
    public void The_document_and_its_variables_are_posted_together()
    {
        var handler = new StubHandler("""{"data":{"people":[{"name":"Ada"}]}}""");

        Source(handler).Queryable<Person>().Where(p => p.Age > 30).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(handler.SentBody, Does.Contain("people(where: $v0)"));
            Assert.That(handler.SentBody, Does.Contain("""{"age":{"gt":30}}"""));
            Assert.That(handler.SentRequest!.Method, Is.EqualTo(HttpMethod.Post));
        });
    }

    [Test]
    public void The_response_data_is_materialized()
    {
        var handler = new StubHandler("""{"data":{"people":[{"name":"Ada","age":36}]}}""");

        var people = Source(handler).Queryable<Person>().Where(p => p.Age > 30).ToArray();

        Assert.That(people[0].Name, Is.EqualTo("Ada"));
    }

    /// <summary>
    /// Servers routinely answer 200 with an errors array, so the body is read before the status
    /// code is consulted.
    /// </summary>
    [Test]
    public void GraphQL_errors_on_a_200_surface_as_a_response_exception()
    {
        var handler = new StubHandler("""{"errors":[{"message":"Unknown field 'people'."}]}""");

        var exception = Assert.Throws<GraphQLResponseException>(
            () => Source(handler).Queryable<Person>().Where(p => p.Age > 30).ToArray());

        Assert.That(exception!.Response.Errors![0].Message, Is.EqualTo("Unknown field 'people'."));
    }

    [Test]
    public void GraphQL_errors_are_preferred_over_the_status_code()
    {
        var handler = new StubHandler("""{"errors":[{"message":"nope"}]}""",
            HttpStatusCode.InternalServerError);

        Assert.Throws<GraphQLResponseException>(
            () => Source(handler).Queryable<Person>().Where(p => p.Age > 30).ToArray());
    }

    [Test]
    public void A_failing_status_with_no_errors_array_still_throws()
    {
        var handler = new StubHandler("""{"data":null}""", HttpStatusCode.BadGateway);

        Assert.Throws<HttpRequestException>(
            () => Source(handler).Queryable<Person>().Where(p => p.Age > 30).ToArray());
    }

    [Test]
    public void A_response_with_neither_data_nor_errors_is_FGQL017()
    {
        var handler = new StubHandler("{}");

        var exception = Assert.Throws<GraphQLTranslationException>(
            () => Source(handler).Queryable<Person>().Where(p => p.Age > 30).ToArray());

        Assert.That(exception!.DiagnosticId, Is.EqualTo("FGQL017"));
    }

    [Test]
    public async Task Async_terminals_reach_the_same_transport()
    {
        var handler = new StubHandler("""{"data":{"people":[{"name":"Ada"}]}}""");

        var people = await Source(handler).Queryable<Person>().Where(p => p.Age > 30).ToListAsync();

        Assert.That(people[0].Name, Is.EqualTo("Ada"));
    }
}
