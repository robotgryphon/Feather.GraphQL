using Feather.GraphQL;

namespace Feather.GraphQL.Linq.Providers.Tests;

/// <summary>
/// Queries declared with <c>[GraphQLQuery]</c> rather than composed.
/// </summary>
/// <remarks>
/// The compiler writes the body, so what these establish is that the body it wrote sends the
/// right request and reads the reply back — the document verbatim, the parameters as variables,
/// and nothing between the two.
/// </remarks>
/// <remarks>
/// The return type is the root field's own — <c>Person[]</c>, not a wrapper holding one — because
/// a reply's <c>data</c> carries exactly the field the query asked for and a type declared to
/// hold it would exist for the serializer alone.
/// </remarks>
[TestFixture]
public partial class DeclaredQueryTests
{
    private const string Rows = """{"data":{"people":[{"name":"Ada","age":36}]}}""";

    [GraphQLQuery("query { people { name age } }")]
    private static partial Task<Person[]> AllAsync(HttpClient client, CancellationToken cancellationToken);

    [GraphQLQuery("query($min: Int!) { people(where: { age: { gt: $min } }) { name age } }")]
    private static partial Task<Person[]> OlderThanAsync(
        HttpClient client, int min, CancellationToken cancellationToken);

    [GraphQLQuery("query($name: String!, $min: Int!) { people(where: { name: { eq: $name }, age: { gt: $min } }) { name age } }")]
    private static partial Task<Person[]> NamedOlderThanAsync(
        HttpClient client, string name, int min, CancellationToken cancellationToken);

    [Test]
    public async Task A_declared_query_posts_its_document_and_reads_the_reply()
    {
        var handler = new StubHandler(Rows);

        var people = await AllAsync(handler.Client(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(handler.SentBody, Is.EqualTo("""{"query":"query { people { name age } }"}"""));
            Assert.That(people[0].Name, Is.EqualTo("Ada"));
        });
    }

    /// <summary>A parameter becomes a variable, named as the method named it.</summary>
    [Test]
    public async Task A_parameter_is_posted_as_the_variable_of_the_same_name()
    {
        var handler = new StubHandler(Rows);

        await OlderThanAsync(handler.Client(), 30, CancellationToken.None);

        Assert.That(handler.SentBody, Does.Contain(@"""variables"":{""min"":30}"));
    }

    /// <summary>Several parameters, in the order the method declares them.</summary>
    [Test]
    public async Task Several_parameters_are_posted_together()
    {
        var handler = new StubHandler(Rows);

        await NamedOlderThanAsync(handler.Client(), "Ada", 30, CancellationToken.None);

        Assert.That(handler.SentBody, Does.Contain(@"""variables"":{""name"":""Ada"",""min"":30}"));
    }

    /// <summary>
    /// The document goes over verbatim. It is a literal in the attribute, so nothing is printed,
    /// escaped or reassembled on the way.
    /// </summary>
    [Test]
    public async Task The_document_is_sent_exactly_as_declared()
    {
        var handler = new StubHandler(Rows);

        await OlderThanAsync(handler.Client(), 1, CancellationToken.None);

        // The braces and dollars survive; only the JSON string escaping is applied to them.
        Assert.That(handler.SentBody,
            Does.Contain(@"query($min: Int!) { people(where: { age: { gt: $min } }) { name age } }"));
    }
}
