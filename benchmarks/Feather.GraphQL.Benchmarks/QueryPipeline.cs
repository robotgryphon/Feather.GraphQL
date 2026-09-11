using System.Runtime.CompilerServices;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Feather.GraphQL.Linq.Execution;
using Feather.GraphQL.Linq.Filtering;
using Feather.GraphQL.Linq.Query;

namespace Feather.GraphQL.Benchmarks;

/// <summary>
/// The whole library path — translate, execute, materialize — with the network removed.
/// </summary>
/// <remarks>
/// <para>
/// The executor hands back an already-parsed <c>data</c> element, so no HTTP, no serialization
/// of the request and no parsing of the reply is in the measurement. What remains is Feather's
/// own work: walking the chain, printing the document, and turning JSON into objects. This is
/// the tier to profile in, because a profile taken over <see cref="ClientComparison"/> is mostly
/// <c>HttpClient</c>.
/// </para>
/// <para>
/// The two benchmarks differ in one thing only. <c>Composed</c> builds its chain behind a helper,
/// which the generator declines, so it translates at runtime on every call. <c>Precompiled</c>
/// writes the same chain inline at its terminal, which the generator recognises and prints a
/// document for at build time. The gap between them is what precompilation is worth.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class QueryPipeline
{
    private CannedExecutor _executor = null!;

    /// <summary>How many rows the canned reply carries.</summary>
    [Params(1, 100, 1000)]
    public int Rows { get; set; }

    [GlobalSetup]
    public void Setup() => _executor = new CannedExecutor(Payloads.Data(Rows));

    [Benchmark(Baseline = true, Description = "Composed at runtime")]
    public async Task<int> Composed()
    {
        var rows = await Source(_executor)
            .Where(c => c.Continent.Code == "EU")
            .Select(c => new { c.Name, Continent = c.Continent.Name })
            .ToArrayAsync();

        return rows.Length;
    }

    [Benchmark(Description = "Precompiled document")]
    public async Task<int> Precompiled()
    {
        var rows = await GraphQLQueryable.For<Country>(_executor, "countries")
            .Where(c => c.Continent.Code == "EU")
            .Select(c => new { c.Name, Continent = c.Continent.Name })
            .ToArrayAsync();

        return rows.Length;
    }

    /// <summary>The same query, behind a boundary the generator declines to look through.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IQueryable<Country> Source(IGraphQLQueryExecutor executor)
        => GraphQLQueryable.For<Country>(executor, "countries");

    /// <summary>
    /// An executor that returns a fixed answer without doing any work for it.
    /// </summary>
    /// <remarks>
    /// The <see cref="JsonElement"/> is parsed once in setup and handed back by reference, so
    /// nothing in the measurement is attributable to the transport — including the JSON parse,
    /// whose cost <c>TransportFloor.Parse</c> reports on its own.
    /// </remarks>
    private sealed class CannedExecutor(JsonElement data) : IGraphQLQueryExecutor
    {
        public IFilterTranslationProvider FilterProvider => HotChocolateFilterProvider.Instance;

        public ValueTask<JsonElement> ExecuteAsync(
            string query,
            IReadOnlyDictionary<string, object?> variables,
            CancellationToken cancellationToken)
            => new(data);
    }
}
