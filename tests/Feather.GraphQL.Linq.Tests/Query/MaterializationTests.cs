using System.Net;
using System.Text;
using Feather.GraphQL.Http;
using Feather.GraphQL.Linq.Query;

namespace Feather.GraphQL.Linq.Tests.Query;

/// <summary>
/// The response half of a chain: what comes back becomes the elements the chain asked for.
/// </summary>
[TestFixture]
public class MaterializationTests
{
    [Test]
    public async Task Elements_are_read_by_the_field_names_that_were_requested()
    {
        var handler = Responds("""
            {"data":{"people":[
                {"name":"John","age":42,"emailAddress":"john@example.com"},
                {"name":"Ada","age":36,"emailAddress":null}
            ]}}
            """);

        var people = await GraphQLQueryable.For<Person>()
            .Where(p => p.Age > 30)
            .ToArrayAsync(new HttpClient(handler) { BaseAddress = Endpoint });

        Assert.Multiple(() =>
        {
            Assert.That(people.Select(p => p.Name), Is.EqualTo(new[] { "John", "Ada" }));
            Assert.That(people[0].Email, Is.EqualTo("john@example.com"));
            Assert.That(people[1].Email, Is.Null);
            Assert.That(handler.LastRequest, Does.Contain("people(where:"));
        });
    }

    /// <summary>
    /// The reason the reader is named by the field metadata rather than by a naming policy:
    /// System.Text.Json does not honour <c>[DataMember]</c>, so a policy would ask for
    /// <c>displayName</c> and then look for <c>name</c>.
    /// </summary>
    [Test]
    public async Task A_DataMember_name_round_trips()
    {
        var handler = Responds("""{"data":{"members":[{"displayName":"Ada"}]}}""");

        var members = await GraphQLQueryable.For<Member>()
            .Where(m => m.Name == "Ada")
            .ToArrayAsync(new HttpClient(handler) { BaseAddress = Endpoint });

        Assert.That(members.Single().Name, Is.EqualTo("Ada"));
    }

    [Test]
    public async Task Offset_paging_reads_through_items()
    {
        var handler = Responds("""{"data":{"offset":{"items":[{"name":"Ada"},{"name":"Grace"}]}}}""");

        var people = await GraphQLQueryable.For<OffsetPerson>()
            .Skip(5).Take(10)
            .ToArrayAsync(new HttpClient(handler) { BaseAddress = Endpoint });

        Assert.That(people.Select(p => p.Name), Is.EqualTo(new[] { "Ada", "Grace" }));
    }

    [Test]
    public async Task Cursor_paging_reads_through_nodes()
    {
        var handler = Responds("""{"data":{"connected":{"nodes":[{"name":"Ada"}]}}}""");

        var people = await GraphQLQueryable.For<CursorPerson>()
            .Take(10)
            .ToArrayAsync(new HttpClient(handler) { BaseAddress = Endpoint });

        Assert.That(people.Single().Name, Is.EqualTo("Ada"));
    }

    /// <summary>
    /// The case a plain deserialize cannot cover: the anonymous type's member is <c>Email</c>
    /// while the field on the wire is <c>emailAddress</c>.
    /// </summary>
    [Test]
    public async Task A_projection_binds_by_field_name_not_by_member_name()
    {
        var handler = Responds("""
            {"data":{"people":[{"name":"John","emailAddress":"john@example.com"}]}}
            """);

        var rows = await GraphQLQueryable.For<Person>()
            .Where(p => p.Age > 30)
            .Select(p => new { p.Name, p.Email })
            .ToArrayAsync(new HttpClient(handler) { BaseAddress = Endpoint });

        Assert.Multiple(() =>
        {
            Assert.That(rows.Single().Name, Is.EqualTo("John"));
            Assert.That(rows.Single().Email, Is.EqualTo("john@example.com"));
        });
    }

    [Test]
    public async Task A_nested_projection_reads_the_collection()
    {
        var handler = Responds("""
            {"data":{"authors":[
                {"name":"Le Guin","books":[{"title":"The Dispossessed"},{"title":"A Wizard of Earthsea"}]},
                {"name":"Butler","books":[]}
            ]}}
            """);

        var rows = await GraphQLQueryable.For<Author>()
            .Where(a => a.Name != "")
            .Select(a => new { a.Name, Titles = a.Books.Select(b => b.Title) })
            .ToArrayAsync(new HttpClient(handler) { BaseAddress = Endpoint });

        Assert.Multiple(() =>
        {
            Assert.That(rows[0].Titles, Is.EqualTo(new[] { "The Dispossessed", "A Wizard of Earthsea" }));
            Assert.That(rows[1].Titles, Is.Empty);
        });
    }

    [Test]
    public async Task A_nested_projection_can_reshape_the_collection_items()
    {
        var handler = Responds("""
            {"data":{"authors":[{"name":"Le Guin","books":[{"title":"The Dispossessed","pages":341}]}]}}
            """);

        var rows = await GraphQLQueryable.For<Author>()
            .Where(a => a.Name != "")
            .Select(a => new { Books = a.Books.Select(b => new { b.Title, b.Pages }) })
            .ToArrayAsync(new HttpClient(handler) { BaseAddress = Endpoint });

        var book = rows.Single().Books.Single();
        Assert.Multiple(() =>
        {
            Assert.That(book.Title, Is.EqualTo("The Dispossessed"));
            Assert.That(book.Pages, Is.EqualTo(341));
        });
    }

    [Test]
    public async Task Projecting_the_element_itself_reads_the_whole_object()
    {
        var handler = Responds("""{"data":{"people":[{"name":"Ada","age":36,"emailAddress":"ada@example.com"}]}}""");

        var people = await GraphQLQueryable.For<Person>()
            .Where(p => p.Age > 30)
            .Select(p => p)
            .ToArrayAsync(new HttpClient(handler) { BaseAddress = Endpoint });

        Assert.That(people.Single().Age, Is.EqualTo(36));
    }

    [Test]
    public async Task ToListAsync_reads_the_same_elements()
    {
        var handler = Responds("""{"data":{"people":[{"name":"Ada","age":36,"emailAddress":null}]}}""");

        var people = await GraphQLQueryable.For<Person>()
            .Where(p => p.Age > 30)
            .ToListAsync(new HttpClient(handler) { BaseAddress = Endpoint });

        Assert.That(people, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task An_empty_result_materializes_empty()
    {
        var handler = Responds("""{"data":{"people":[]}}""");

        var people = await GraphQLQueryable.For<Person>()
            .Where(p => p.Age > 30)
            .ToArrayAsync(new HttpClient(handler) { BaseAddress = Endpoint });

        Assert.That(people, Is.Empty);
    }

    [Test]
    public async Task A_null_root_field_materializes_empty()
    {
        var handler = Responds("""{"data":{"people":null}}""");

        var people = await GraphQLQueryable.For<Person>()
            .Where(p => p.Age > 30)
            .ToArrayAsync(new HttpClient(handler) { BaseAddress = Endpoint });

        Assert.That(people, Is.Empty);
    }

    [Test]
    public void GraphQL_errors_surface_as_a_response_exception()
    {
        var handler = Responds("""{"errors":[{"message":"Unknown field 'age'."}]}""");

        var exception = Assert.ThrowsAsync<GraphQLResponseException>(() => GraphQLQueryable.For<Person>()
            .Where(p => p.Age > 30)
            .ToArrayAsync(new HttpClient(handler) { BaseAddress = Endpoint }));

        Assert.That(exception!.Response.Errors!.Single().Message, Is.EqualTo("Unknown field 'age'."));
    }

    /// <summary>A 400 carrying a GraphQL body is still a GraphQL error, not an HTTP one.</summary>
    [Test]
    public void A_failing_status_with_a_GraphQL_body_still_reports_the_GraphQL_error()
    {
        var handler = Responds("""{"errors":[{"message":"Syntax error."}]}""", HttpStatusCode.BadRequest);

        Assert.ThrowsAsync<GraphQLResponseException>(() => GraphQLQueryable.For<Person>()
            .Where(p => p.Age > 30)
            .ToArrayAsync(new HttpClient(handler) { BaseAddress = Endpoint }));
    }

    [Test]
    public void A_failing_status_with_no_GraphQL_body_stays_an_HTTP_failure()
    {
        var handler = Responds("<html>gateway timeout</html>", HttpStatusCode.GatewayTimeout, "text/html");

        Assert.ThrowsAsync<HttpRequestException>(() => GraphQLQueryable.For<Person>()
            .Where(p => p.Age > 30)
            .ToArrayAsync(new HttpClient(handler) { BaseAddress = Endpoint }));
    }

    /// <summary>
    /// The usual cause is a <c>PagingKind</c> that disagrees with the server, so the message has
    /// to name the path that was expected rather than just failing to bind.
    /// </summary>
    [Test]
    public void A_response_shaped_unlike_the_query_is_FGQL015()
    {
        var handler = Responds("""{"data":{"connected":[{"name":"Ada"}]}}""");

        var exception = Assert.ThrowsAsync<GraphQLTranslationException>(() =>
            GraphQLQueryable.For<CursorPerson>()
                .Take(10)
                .ToArrayAsync(new HttpClient(handler) { BaseAddress = Endpoint }));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.DiagnosticId, Is.EqualTo("FGQL015"));
            Assert.That(exception.Message, Does.Contain("data.connected.nodes"));
        });
    }

    [Test]
    public void A_response_missing_the_root_field_is_FGQL015()
    {
        var handler = Responds("""{"data":{"somethingElse":[]}}""");

        var exception = Assert.ThrowsAsync<GraphQLTranslationException>(() =>
            GraphQLQueryable.For<Person>()
                .Where(p => p.Age > 30)
                .ToArrayAsync(new HttpClient(handler) { BaseAddress = Endpoint }));

        Assert.That(exception!.DiagnosticId, Is.EqualTo("FGQL015"));
    }

    private static Uri Endpoint { get; } = new("https://example.test/graphql");

    private static StubHandler Responds(
        string body,
        HttpStatusCode status = HttpStatusCode.OK,
        string mediaType = "application/json")
        => new(body, status, mediaType);

    private sealed class StubHandler(string body, HttpStatusCode status, string mediaType) : HttpMessageHandler
    {
        /// <summary>The posted request body, so a test can check what was actually asked for.</summary>
        public string? LastRequest { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, mediaType)
            };
        }
    }
}
