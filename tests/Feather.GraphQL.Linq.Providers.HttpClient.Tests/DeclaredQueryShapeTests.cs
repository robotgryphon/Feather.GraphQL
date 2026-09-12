using Feather.GraphQL;

namespace Feather.GraphQL.Linq.Providers.Tests;

/// <summary>
/// Declared queries in the shapes a consumer will actually write them in.
/// </summary>
/// <remarks>
/// Every one of these is a way the generated implementation can fail to match the declaration it
/// is implementing — a different containing type, an optional parameter, a nullable one. A
/// mismatch is a compile error in the consumer's own assembly, so the test is that this file
/// builds at all; the assertions are there to prove the body it wrote also works.
/// </remarks>
[TestFixture]
public partial class DeclaredQueryShapeTests
{
    private const string Rows = """{"data":{"people":[{"name":"Ada","age":36}]}}""";

    /// <summary>An optional token, which the declaration gives a default and the body must not.</summary>
    [GraphQLQuery("query { people { name age } }")]
    private static partial Task<Person[]> OptionalTokenAsync(
        HttpClient client, CancellationToken cancellationToken = default);

    /// <summary>A nullable parameter, whose annotation the implementation has to keep.</summary>
    [GraphQLQuery("query($name: String) { people(where: { name: { eq: $name } }) { name age } }")]
    private static partial Task<Person[]> ByNullableNameAsync(
        HttpClient client, string? name, CancellationToken cancellationToken = default);

    /// <summary>No cancellation token at all.</summary>
    [GraphQLQuery("query { people { name age } }")]
    private static partial Task<Person[]> NoTokenAsync(HttpClient client);

    /// <summary>A ValueTask, which is the other awaitable the generator accepts.</summary>
    [GraphQLQuery("query { people { name age } }")]
    private static partial ValueTask<Person[]> ValueTaskAsync(HttpClient client);

    [Test]
    public async Task An_optional_token_is_honoured()
        => Assert.That((await OptionalTokenAsync(new StubHandler(Rows).Client()))[0].Name, Is.EqualTo("Ada"));

    [Test]
    public async Task A_nullable_parameter_is_posted()
    {
        var handler = new StubHandler(Rows);

        await ByNullableNameAsync(handler.Client(), null);

        Assert.That(handler.SentBody, Does.Contain(@"""variables"":{""name"":null}"));
    }

    [Test]
    public async Task A_query_with_no_token_works()
        => Assert.That((await NoTokenAsync(new StubHandler(Rows).Client()))[0].Name, Is.EqualTo("Ada"));

    [Test]
    public async Task A_value_task_return_works()
        => Assert.That((await ValueTaskAsync(new StubHandler(Rows).Client()))[0].Name, Is.EqualTo("Ada"));
}

/// <summary>
/// A second containing type, which is what makes the generator emit more than one group.
/// </summary>
/// <remarks>
/// Worth its own type rather than another method above it: the methods and the variable structs
/// they name are numbered by two separate walks, and nothing proves those walks agree until the
/// grouping puts them in different orders.
/// </remarks>
public static partial class DeclaredQueriesElsewhere
{
    [GraphQLQuery("query($min: Int!) { people(where: { age: { gt: $min } }) { name age } }")]
    public static partial Task<Person[]> OlderThanAsync(HttpClient client, int min);
}

[TestFixture]
public class DeclaredQueriesElsewhereTests
{
    [Test]
    public async Task A_query_in_another_type_posts_its_own_variables()
    {
        var handler = new StubHandler("""{"data":{"people":[{"name":"Ada","age":36}]}}""");

        await DeclaredQueriesElsewhere.OlderThanAsync(handler.Client(), 21);

        Assert.That(handler.SentBody, Does.Contain(@"""variables"":{""min"":21}"));
    }
}
