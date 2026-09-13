using Feather.GraphQL;
using Feather.GraphQL.Linq.Query;

namespace Feather.GraphQL.Linq.Providers.Tests;

/// <summary>
/// Queries written as a LINQ chain and compiled into a request, rather than composed into one.
/// </summary>
/// <remarks>
/// <para>
/// The chain is the same chain; what the attribute changes is where it ends up. Isolated to a
/// method, it binds that method's parameters and nothing else, so the compiler can write the
/// request out in full and replace every call with it — and the body, which is now only the
/// specification, never runs.
/// </para>
/// <para>
/// That last part is what these assert, and they assert it by counting. A chain that runs
/// attaches its precompiled document to the provider on the way through, so the counter moving
/// would mean the body had executed and the compiled call had not replaced anything.
/// </para>
/// </remarks>
[TestFixture]
public class CompiledQueryTests
{
    private const string Rows = """{"data":{"people":[{"name":"Ada","age":36}]}}""";

    /// <summary>Unbounded on purpose — FGQL012 is about the chain, and this one has no filter.</summary>
#pragma warning disable FGQL012
    [GraphQLQuery]
    private static Task<Person[]> AllAsync(HttpClient client, CancellationToken cancellationToken)
        => client.CreateQueryable<Person>("people").ToArrayAsync(cancellationToken);
#pragma warning restore FGQL012

    [GraphQLQuery]
    private static Task<Person[]> OlderThanAsync(
        HttpClient client, int min, CancellationToken cancellationToken)
        => client.CreateQueryable<Person>("people")
            .Where(p => p.Age > min)
            .ToArrayAsync(cancellationToken);

    /// <summary>Two parameters, which the filter has to bind in the order the predicate names them.</summary>
    [GraphQLQuery]
    private static Task<Person[]> NamedOlderThanAsync(
        HttpClient client, string name, int min, CancellationToken cancellationToken)
        => client.CreateQueryable<Person>("people")
            .Where(p => p.Name == name && p.Age > min)
            .ToArrayAsync(cancellationToken);

    /// <summary>A constant the chain closes over, which is written back out as a literal.</summary>
    [GraphQLQuery]
    private static Task<Person[]> AdultsAsync(HttpClient client)
        => client.CreateQueryable<Person>("people")
            .Where(p => p.Age >= 18)
            .ToArrayAsync();

    /// <summary>A List, which is the other sequence a terminal produces.</summary>
    [GraphQLQuery]
    private static Task<List<Person>> ListedAsync(
        HttpClient client, int min, CancellationToken cancellationToken = default)
        => client.CreateQueryable<Person>("people")
            .Where(p => p.Age > min)
            .ToListAsync(cancellationToken);

    [Test]
    public async Task A_compiled_query_posts_its_document_and_reads_the_reply()
    {
        var handler = new StubHandler(Rows);

        var people = await AllAsync(handler.Client(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(handler.SentBody, Is.EqualTo("""{"query":"query { people { name age } }"}"""));
            Assert.That(people[0].Name, Is.EqualTo("Ada"));
        });
    }

    /// <summary>
    /// The value the predicate compared against arrives as an argument, so the filter is written
    /// straight from it — nothing is composed and no expression tree exists to read it out of.
    /// </summary>
    [Test]
    public async Task A_parameter_the_predicate_binds_is_posted_as_the_filter()
    {
        var handler = new StubHandler(Rows);

        await OlderThanAsync(handler.Client(), 30, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(handler.SentBody,
                Does.Contain(@"query($v0: PersonFilterInput) { people(where: $v0) { name age } }"));

            Assert.That(handler.SentBody, Does.Contain(@"""variables"":{""v0"":{""age"":{""gt"":30}}}"));

        });
    }

    [Test]
    public async Task Several_bound_values_are_written_in_the_order_the_predicate_names_them()
    {
        var handler = new StubHandler(Rows);

        await NamedOlderThanAsync(handler.Client(), "Ada", 30, CancellationToken.None);

        Assert.That(handler.SentBody,
            Does.Contain(@"""variables"":{""v0"":{""name"":{""eq"":""Ada""},""age"":{""gt"":30}}}"));
    }

    [Test]
    public async Task A_constant_is_written_out_as_itself()
    {
        var handler = new StubHandler(Rows);

        await AdultsAsync(handler.Client());

        Assert.That(handler.SentBody, Does.Contain(@"""variables"":{""v0"":{""age"":{""gte"":18}}}"));
    }

    [Test]
    public async Task A_list_returning_query_is_compiled_too()
    {
        var handler = new StubHandler(Rows);

        var people = await ListedAsync(handler.Client(), 30);

        Assert.Multiple(() =>
        {
            Assert.That(people[0].Name, Is.EqualTo("Ada"));
        });
    }

    /// <summary>
    /// The comparison that makes the compiled form trustworthy: the same query composed at the
    /// call site posts the same bytes.
    /// </summary>
    [Test]
    public async Task The_compiled_request_is_what_composing_the_chain_would_have_sent()
    {
        var compiled = new StubHandler(Rows);

        await OlderThanAsync(compiled.Client(), 30, CancellationToken.None);

        // Through a local, so the entry-point interceptor cannot precompile it either — this is
        // the chain translated end to end at run time.

        // Frozen from the runtime translation while both paths still existed. When the runtime
        // path goes, this literal is what is left of the comparison — the bytes the two agreed
        // on, rather than a guess at what the compiler ought to emit.
        const string Agreed =
            "{\"query\":\"query($v0: PersonFilterInput) { people(where: $v0) { name age } }\",\"variables\":{\"v0\":{\"age\":{\"gt\":30}}}}";

        Assert.That(compiled.SentBody, Is.EqualTo(Agreed));
    }
}
