using System.Net;
using System.Text;
using System.Text.Json;
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

    /// <summary>
    /// The body is written member by member rather than serialized from a dictionary, so what it
    /// comes out as is worth pinning exactly.
    /// </summary>
    /// <remarks>
    /// Members carrying nothing are left out. The previous shape sent
    /// <c>"variables":null,"operationName":null,"extensions":null</c> on every request, which is
    /// legal but is three members a server has to parse and one — a present <c>operationName</c>
    /// of null — that a strict server can reasonably object to.
    /// </remarks>
    [Test]
    public async Task A_query_with_no_variables_posts_only_the_query()
    {
        var handler = new StubHandler("""{"data":{"people":[]}}""");

        await handler.Client().SendGraphQLQueryAsync("{ people { name } }");

        Assert.That(handler.SentBody, Is.EqualTo("""{"query":"{ people { name } }"}"""));
    }

    [Test]
    public void A_query_with_variables_posts_them_under_variables()
    {
        var handler = new StubHandler("""{"data":{"people":[]}}""");

        Query(handler).Where(p => p.Age > 30).ToArray();

        Assert.That(handler.SentBody, Does.Contain("""{"v0":{"age":{"gt":30}}}"""));
    }

    /// <summary>A query is written as a JSON string, so its quotes have to survive the trip.</summary>
    [Test]
    public async Task A_query_containing_quotes_is_escaped()
    {
        var handler = new StubHandler("""{"data":{"people":[]}}""");

        await handler.Client().SendGraphQLQueryAsync("""{ people(name: "Ada") { name } }""");

        Assert.That(handler.SentBody,
            Is.EqualTo("""{"query":"{ people(name: \u0022Ada\u0022) { name } }"}"""));
    }

    /// <summary>
    /// The request headers are rendered once and reused, so what they render to is worth
    /// pinning: nothing else in the suite would notice if a cached value came out wrong.
    /// </summary>
    [Test]
    public void The_request_carries_the_GraphQL_headers()
    {
        var handler = new StubHandler("""{"data":{"people":[{"name":"Ada"}]}}""");

        Query(handler).Where(p => p.Age > 30).ToArray();

        var request = handler.SentRequest!;

        Assert.Multiple(() =>
        {
            Assert.That(request.Headers.Accept.Select(a => a.MediaType),
                Is.EquivalentTo(GraphQLHttpConstants.RESPONSE_CONTENT_TYPES));
            Assert.That(request.Headers.AcceptCharset.Single().Value, Is.EqualTo("utf-8"));
            Assert.That(request.Headers.UserAgent.Single().Product!.Name,
                Is.EqualTo("Feather.GraphQL.Http"));

            // No charset: some GraphQL servers reject a content type that carries one.
            Assert.That(request.Content!.Headers.ContentType!.ToString(), Is.EqualTo("application/json"));
        });
    }

    /// <summary>
    /// The reader deserializes <c>data</c> where it finds it rather than scanning past it, so
    /// the case that has to be proved is the one where it cannot know about the errors yet.
    /// </summary>
    [Test]
    public void Errors_sent_after_data_still_surface_as_a_response_exception()
    {
        var handler = new StubHandler(
            """{"data":{"people":null},"errors":[{"message":"Unknown field 'people'."}]}""");

        var exception = Assert.Throws<GraphQLHttpException>(
            () => Query(handler).Where(p => p.Age > 30).ToArray());

        Assert.That(exception!.Errors[0].Message, Is.EqualTo("Unknown field 'people'."));
    }

    /// <summary>
    /// The same ordering, but with a payload the caller's type cannot be read from at all: the
    /// errors explain why, and are what the caller is told about.
    /// </summary>
    /// <remarks>
    /// Read typed rather than through the provider, which asks for a <c>JsonElement</c> — a
    /// shape any valid JSON reads into, so nothing the provider does can make the payload fail
    /// and the fallback would never run.
    /// </remarks>
    [Test]
    public void Errors_sent_after_an_unreadable_payload_are_preferred_to_the_parse_failure()
    {
        var response = Reply("""{"data":"not an object","errors":[{"message":"nope"}]}""");

        var exception = Assert.ThrowsAsync<GraphQLHttpException>(
            async () => await response.ReadGraphQLAsync<PeopleData>());

        Assert.That(exception!.Errors[0].Message, Is.EqualTo("nope"));
    }

    /// <summary>An unreadable payload with no errors to explain it is still a parse failure.</summary>
    [Test]
    public void An_unreadable_payload_with_no_errors_throws_the_parse_failure()
    {
        var response = Reply("""{"data":"not an object"}""");

        Assert.ThrowsAsync<JsonException>(async () => await response.ReadGraphQLAsync<PeopleData>());
    }

    /// <summary>
    /// The typed read goes through the contract registry, not through plain reflection.
    /// </summary>
    /// <remarks>
    /// Proved by a consequence rather than by asking where the contract came from: the registry
    /// relaxes <c>required</c>, because a GraphQL query selects a subset of a type's fields and
    /// the ones it did not ask for come back unset. Reflection defaults would refuse this reply,
    /// which is what reading it used to do — a caller who declared a source-generated context got
    /// it used for a composed query and ignored for a hand-written one.
    /// </remarks>
    [Test]
    public async Task A_reply_omitting_a_required_member_is_read_through_the_registry()
    {
        var response = Reply("""{"data":{"people":[{"age":36}]}}""");

        var data = await response.ReadGraphQLAsync<PeopleData>();

        Assert.That(data.People, Has.Length.EqualTo(1));
    }

    private static HttpResponseMessage Reply(string json)
        => new() { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>The shape of a <c>data</c> field, for the reads that do not go through a query.</summary>
    private sealed class PeopleData
    {
        public Person[] People { get; init; } = [];
    }

    /// <summary>A member the reader does not care about is read past, not tripped over.</summary>
    [Test]
    public void Extensions_alongside_the_data_are_ignored()
    {
        var handler = new StubHandler(
            """{"extensions":{"tracing":{"version":1}},"data":{"people":[{"name":"Ada"}]}}""");

        var people = Query(handler).Where(p => p.Age > 30).ToArray();

        Assert.That(people[0].Name, Is.EqualTo("Ada"));
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
