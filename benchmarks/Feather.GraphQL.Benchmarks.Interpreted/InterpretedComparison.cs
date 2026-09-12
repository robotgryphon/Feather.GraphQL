using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Feather.GraphQL.Http;
using Feather.GraphQL.Linq.Providers;
using Feather.GraphQL.Linq.Query;
using Feather.GraphQL.Metadata;

namespace Feather.GraphQL.Benchmarks.Interpreted;

/// <summary>
/// The LINQ surface with nothing generated for it.
/// </summary>
/// <remarks>
/// <para>
/// This assembly does not reference the analyzer, so every chain below is translated, shaped and
/// materialized at run time: the document is printed from the expression tree on each call, the
/// projection is compiled with <c>Expression.Compile</c>, and the queried type's fields are found
/// by reflection rather than read from a generated table.
/// </para>
/// <para>
/// It is the same query, over the same bytes, as <c>ClientComparison</c> in the sibling project —
/// same rows from the same seed, same document, same canned reply. Read against those numbers,
/// which is the only way these mean anything: what separates the two tables is one project
/// reference, and therefore everything the compiler was able to do ahead of time.
/// </para>
/// <para>
/// Nothing here is defeated or contrived. There is no <c>NoInlining</c> helper hiding a chain from
/// an interceptor, because there is no interceptor to hide from — which is also why these numbers
/// are the honest floor for a consumer who has not installed the analyzer, or whose chain the
/// compiler could not see.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class InterpretedComparison
{
    private const string ContinentCode = "EU";

    private const string Query =
        "query { countries { name continent { name } } }";

    private CannedTransport _transport = null!;
    private HttpClient _client = null!;

    /// <summary>The row counts the generated benchmarks run at, so the tables line up.</summary>
    [Params(1, 25, 100)]
    public int Rows { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        // The same contracts the generated side reads through, so the difference between the two
        // tables is this library's generated code and not which serializer each one got.
        GraphQLJsonContextRegistry.Register(BenchmarkSerializerContext.Default);

        _transport = new CannedTransport(Payloads.NestedBody(Rows));
        _client = _transport.Client();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _client.Dispose();
        _transport.Dispose();
    }

    /// <summary>
    /// The document posted by hand, which no generator was ever involved in.
    /// </summary>
    /// <remarks>
    /// The baseline, and the row that should be unchanged from the generated project: posting a
    /// string and reading the reply into a declared type is the same work either way. Its being
    /// the same is what says the two tables are measuring the same machine.
    /// </remarks>
    [Benchmark(Baseline = true, Description = "Feather: String/HttpClient")]
    public async Task<int> Static()
    {
        using var response = await _client.SendGraphQLQueryAsync(Query);

        return (await response.ReadGraphQLAsync<CountriesData>()).Countries.Length;
    }

    /// <summary>
    /// The chain, translated on every call.
    /// </summary>
    /// <remarks>
    /// Written inline, as anyone would write it. In the generated project this same text is
    /// replaced at the call site; here it builds an expression tree, walks it to print the
    /// document, and compiles the projection into a delegate before the first row is read.
    /// </remarks>
    [Benchmark(Description = "LINQ")]
    public async Task<int> Linq()
        => (await _client.CreateQueryable<Country>("countries")
            .Select(c => new Country { Name = c.Name, Continent = new Continent { Name = c.Continent.Name } })
            .ToArrayAsync()
            .ConfigureAwait(false)).Length;

    /// <summary>The same with a predicate, which has to be lowered into a filter as well.</summary>
    [Benchmark(Description = "LINQ (filtered)")]
    public async Task<int> LinqFiltered()
        => (await _client.CreateQueryable<Country>("countries")
            .Where(c => c.Code == ContinentCode)
            .Select(c => new Country { Name = c.Name, Continent = new Continent { Name = c.Continent.Name } })
            .ToArrayAsync()
            .ConfigureAwait(false)).Length;

    /// <summary>
    /// The same chain with no projection, for what the shaping itself costs.
    /// </summary>
    /// <remarks>
    /// Its document asks for the element's own scalars rather than the nested field, so it is not
    /// comparable with the rows above — it is here to separate what translating a chain costs from
    /// what compiling a projection for it costs, which the row above pays and this one does not.
    /// </remarks>
#pragma warning disable FGQL012
    [Benchmark(Description = "LINQ (no projection)")]
    public async Task<int> LinqUnprojected()
        => (await _client.CreateQueryable<Country>("countries")
            .ToArrayAsync()
            .ConfigureAwait(false)).Length;
#pragma warning restore FGQL012
}
