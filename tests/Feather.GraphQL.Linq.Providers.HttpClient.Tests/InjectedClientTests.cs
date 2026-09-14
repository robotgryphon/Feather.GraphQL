using Feather.GraphQL;
using Feather.GraphQL.Linq.Query;

namespace Feather.GraphQL.Linq.Providers.Tests;

/// <summary>
/// Queries in a service class, over the client that class was built with.
/// </summary>
/// <remarks>
/// <para>
/// The shape nearly every real caller has: a client injected once into a constructor, and a
/// handful of queries over it. Asking each of those methods to take the client again would be
/// asking the caller to repeat what the type already knows, on every method, forever.
/// </para>
/// <para>
/// So the client is found on the declaring type when the method does not take one — a field, a
/// property, or a parameter of the primary constructor, which is what this one holds. A document
/// is implemented as another part of that same type, so it reaches all three exactly as a method
/// written by hand would: the generated code names <c>client</c>, and the compiler captures it.
/// </para>
/// </remarks>
public partial class CountryService(HttpClient client)
{
    /// <summary>No client parameter, and no mention of one: the type holds it.</summary>
    [GraphQLQuery("query { people { name age } }")]
    public partial Task<Person[]> AllAsync(CancellationToken cancellationToken = default);

    /// <summary>The same, with a variable the method does take.</summary>
    [GraphQLQuery("query($min: Int!) { people(where: { age: { gt: $min } }) { name age } }")]
    public partial Task<Person[]> OlderThanAsync(int min, CancellationToken cancellationToken = default);

    /// <summary>
    /// A client the method was handed, which wins over the one the type holds.
    /// </summary>
    /// <remarks>
    /// Worth keeping: a service may talk to two schemas, and naming the client is how a method
    /// says which. The parameter is looked at first for exactly that reason.
    /// </remarks>
    [GraphQLQuery("query { people { name age } }")]
    public partial Task<Person[]> FromAsync(HttpClient other, CancellationToken cancellationToken = default);
}

/// <summary>
/// A service holding its client in a field of its own, rather than capturing the parameter.
/// </summary>
/// <remarks>
/// The other shape of the same idea, and worth keeping beside it: the two reach the client by
/// different means — a member lookup and a captured parameter — and only one of them would notice
/// if the other broke.
/// </remarks>
public partial class FieldClientService
{
    private readonly HttpClient _client;

    public FieldClientService(HttpClient client) => _client = client;

    [GraphQLQuery("query { people { name age } }")]
    public partial Task<Person[]> AllAsync(CancellationToken cancellationToken = default);
}

/// <summary>A service whose client is static, queried from a static method.</summary>
public static partial class SharedCountryService
{
    private static readonly HttpClient _shared = new StubHandler(
        """{"data":{"people":[{"name":"Shared","age":1}]}}""").Client();

    [GraphQLQuery("query { people { name age } }")]
    public static partial Task<Person[]> AllAsync(CancellationToken cancellationToken = default);
}

[TestFixture]
public class InjectedClientTests
{
    private const string Rows = """{"data":{"people":[{"name":"Ada","age":36}]}}""";

    [Test]
    public async Task A_query_uses_the_client_its_type_was_built_with()
    {
        var handler = new StubHandler(Rows);
        var service = new CountryService(handler.Client());

        var people = await service.AllAsync();

        Assert.Multiple(() =>
        {
            Assert.That(handler.SentBody, Is.EqualTo("""{"query":"query { people { name age } }"}"""));
            Assert.That(people[0].Name, Is.EqualTo("Ada"));
        });
    }

    [Test]
    public async Task A_variable_still_comes_from_the_methods_own_parameters()
    {
        var handler = new StubHandler(Rows);
        var service = new CountryService(handler.Client());

        await service.OlderThanAsync(30);

        Assert.That(handler.SentBody, Does.Contain(@"""variables"":{""min"":30}"));
    }

    /// <summary>A client on the method is the one that is used, not the one on the type.</summary>
    [Test]
    public async Task A_client_the_method_takes_wins()
    {
        var held = new StubHandler(Rows);
        var passed = new StubHandler("""{"data":{"people":[{"name":"Alan","age":41}]}}""");

        var service = new CountryService(held.Client());

        var people = await service.FromAsync(passed.Client());

        Assert.Multiple(() =>
        {
            Assert.That(people[0].Name, Is.EqualTo("Alan"));
            Assert.That(held.SentBody, Is.Null, "the client the type holds was used instead");
        });
    }

    /// <summary>
    /// A client captured from the primary constructor, which has no field to find by name.
    /// </summary>
    /// <remarks>
    /// What a captured parameter has behind it is a field the compiler synthesised, named
    /// <c>&lt;client&gt;P</c> — not a name any code can write. The generated part names the
    /// parameter instead, as every other method of the type does.
    /// </remarks>
    [Test]
    public async Task A_captured_constructor_parameter_is_the_client()
    {
        var handler = new StubHandler(Rows);

        var people = await new CountryService(handler.Client()).AllAsync();

        Assert.That(people[0].Name, Is.EqualTo("Ada"));
    }

    /// <summary>The same, for a service that keeps its client in a field.</summary>
    [Test]
    public async Task A_client_in_a_field_is_found_too()
    {
        var handler = new StubHandler(Rows);

        var people = await new FieldClientService(handler.Client()).AllAsync();

        Assert.That(people[0].Name, Is.EqualTo("Ada"));
    }

    /// <summary>
    /// A chain over a client captured from the primary constructor is compiled.
    /// </summary>
    /// <remarks>
    /// The interceptor cannot name that client — it is a field the compiler made, private, under a
    /// name no C# can write — so it reaches it through an accessor bound to the field's metadata.
    /// The counter is what proves the call was replaced rather than run.
    /// </remarks>
    [Test]
    public async Task A_chain_over_a_captured_parameter_is_compiled()
    {
        var handler = new StubHandler(Rows);
        var service = new CapturedClientService(handler.Client());

        var people = await service.OlderThanAsync(30);

        Assert.Multiple(() =>
        {
            Assert.That(handler.SentBody,
                Does.Contain(@"query($v0: Int) { people(where: { age: { gt: $v0 } }) { name age } }"));

            Assert.That(people[0].Name, Is.EqualTo("Ada"));
        });
    }

    /// <summary>The same, over a field the service keeps private.</summary>
    [Test]
    public async Task A_chain_over_a_private_field_is_compiled()
    {
        var handler = new StubHandler(Rows);
        var service = new PrivateFieldChainService(handler.Client());

        var people = await service.OlderThanAsync(30);

        Assert.Multiple(() =>
        {
            Assert.That(people[0].Name, Is.EqualTo("Ada"));
        });
    }

    [Test]
    public async Task A_static_method_reads_a_static_client()
        => Assert.That((await SharedCountryService.AllAsync())[0].Name, Is.EqualTo("Shared"));

    /// <summary>
    /// A chain in a service class compiles too, when the client it names can be reached from the
    /// call.
    /// </summary>
    /// <remarks>
    /// The difference between the two forms, and the only place it shows: a document is written
    /// into the declaring type and reads what that type declares, while a chain is replaced at the
    /// call site and reads only what the caller could. So this one holds its client where a caller
    /// can see it.
    /// </remarks>
    [Test]
    public async Task A_chain_over_a_reachable_client_is_compiled()
    {
        var handler = new StubHandler(Rows);
        var service = new ReachableClientService(handler.Client());

        var people = await service.OlderThanAsync(30);

        Assert.Multiple(() =>
        {
            Assert.That(people[0].Name, Is.EqualTo("Ada"));
        });
    }
}

/// <inheritdoc cref="InjectedClientTests.A_chain_over_a_reachable_client_is_compiled"/>
public class ReachableClientService(HttpClient client)
{
    internal HttpClient Client { get; } = client;

    [GraphQLQuery]
    public Task<Person[]> OlderThanAsync(int min, CancellationToken cancellationToken = default)
        => Client.CreateQueryable<Person>("people")
            .Where(p => p.Age > min)
            .ToArrayAsync(cancellationToken);
}

/// <summary>
/// A chain in a service that captures its client from the primary constructor.
/// </summary>
/// <remarks>
/// The shape that used to decline. The interceptor is written somewhere else and the client is a
/// field the compiler made, under a name no C# can write — so it is reached by an accessor the
/// runtime binds to that field's metadata, which costs nothing at run time and survives trimming.
/// </remarks>
public class CapturedClientService(HttpClient client)
{
    [GraphQLQuery]
    public Task<Person[]> OlderThanAsync(int min, CancellationToken cancellationToken = default)
        => client.CreateQueryable<Person>("people")
            .Where(p => p.Age > min)
            .ToArrayAsync(cancellationToken);
}

/// <summary>The same, over a field the service keeps private.</summary>
public class PrivateFieldChainService
{
    private readonly HttpClient _client;

    public PrivateFieldChainService(HttpClient client) => _client = client;

    [GraphQLQuery]
    public Task<Person[]> OlderThanAsync(int min, CancellationToken cancellationToken = default)
        => _client.CreateQueryable<Person>("people")
            .Where(p => p.Age > min)
            .ToArrayAsync(cancellationToken);
}
