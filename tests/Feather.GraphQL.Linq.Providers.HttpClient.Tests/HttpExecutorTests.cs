using System.Net;
using Feather.GraphQL;
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
    private static IQueryable<Person> Query(StubHandler handler)
        => handler.Client().CreateQueryable<Person>("people");

    [Test]
    public void The_document_and_its_variables_are_posted_together()
    {
        var handler = new StubHandler("""{"data":{"people":[{"name":"Ada"}]}}""");

        Query(handler).Where(p => p.Age > 30).ToArray();

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

        var people = Query(handler).Where(p => p.Age > 30).ToArray();

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

        var exception = Assert.Throws<GraphQLHttpException>(
            () => Query(handler).Where(p => p.Age > 30).ToArray());

        Assert.That(exception!.Errors[0].Message, Is.EqualTo("Unknown field 'people'."));
    }

    /// <summary>
    /// The converter that reads an error's <c>path</c> is internal and named only by an attribute,
    /// so nothing but a round trip proves the serializer can still reach it.
    /// </summary>
    [Test]
    public void An_error_path_is_deserialized()
    {
        var handler = new StubHandler(
            """{"errors":[{"message":"nope","path":["people",0,"name"],"locations":[{"line":1,"column":9}]}]}""");

        var exception = Assert.Throws<GraphQLHttpException>(() => Query(handler).Where(p => p.Age > 30).ToArray());

        var error = exception!.Errors[0];

        Assert.Multiple(() =>
        {
            Assert.That(error.Path, Is.EqualTo(new object[] { "people", 0d, "name" }));
            Assert.That(error.Locations![0].Line, Is.EqualTo(1));
        });
    }

    [Test]
    public void GraphQL_errors_are_preferred_over_the_status_code()
    {
        var handler = new StubHandler("""{"errors":[{"message":"nope"}]}""",
            HttpStatusCode.InternalServerError);

        Assert.Throws<GraphQLHttpException>(
            () => Query(handler).Where(p => p.Age > 30).ToArray());
    }

    [Test]
    public void A_failing_status_with_no_errors_array_still_throws()
    {
        var handler = new StubHandler("""{"data":null}""", HttpStatusCode.BadGateway);

        Assert.Throws<HttpRequestException>(
            () => Query(handler).Where(p => p.Age > 30).ToArray());
    }

    /// <summary>
    /// Neither data nor errors is still a failed query, and the exception says so rather than
    /// handing back an empty result.
    /// </summary>
    [Test]
    public void A_response_with_neither_data_nor_errors_throws()
    {
        var handler = new StubHandler("{}");

        var exception = Assert.Throws<GraphQLHttpException>(
            () => Query(handler).Where(p => p.Age > 30).ToArray());

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Errors, Is.Empty);
            Assert.That(exception.Message, Does.Contain("neither data nor errors"));
        });
    }

    /// <summary>
    /// The point of the base type: code that only cares that the query failed does not have to
    /// name the transport that carried it.
    /// </summary>
    [Test]
    public void A_failure_is_catchable_as_the_transport_neutral_base()
    {
        var handler = new StubHandler("""{"errors":[{"message":"nope"}]}""");

        var exception = Assert.Catch<GraphQLException>(
            () => Query(handler).Where(p => p.Age > 30).ToArray());

        Assert.Multiple(() =>
        {
            Assert.That(exception, Is.InstanceOf<GraphQLHttpException>());
            Assert.That(exception!.Errors[0].Message, Is.EqualTo("nope"));
            Assert.That(exception.Message, Does.Contain("nope"));
        });

        ((GraphQLHttpException)exception!).Response.Dispose();
    }

    /// <summary>
    /// The response is the useful part of a failure, so it must survive the throw undisposed.
    /// </summary>
    [Test]
    public async Task The_failing_response_is_still_readable()
    {
        var handler = new StubHandler("""{"errors":[{"message":"nope"}]}""");

        var exception = Assert.Throws<GraphQLHttpException>(
            () => Query(handler).Where(p => p.Age > 30).ToArray());

        string body = await exception!.Response.Content.ReadAsStringAsync();

        Assert.That(body, Does.Contain("nope"));
        exception.Response.Dispose();
    }

    [Test]
    public async Task Async_terminals_reach_the_same_transport()
    {
        var handler = new StubHandler("""{"data":{"people":[{"name":"Ada"}]}}""");

        var people = await Query(handler).Where(p => p.Age > 30).ToListAsync();

        Assert.That(people[0].Name, Is.EqualTo("Ada"));
    }
}
