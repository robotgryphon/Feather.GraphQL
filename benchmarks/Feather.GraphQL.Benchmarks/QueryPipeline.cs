using System.Runtime.CompilerServices;
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
/// The executor answers from a fixed byte array, so no HTTP and no serialization of the request
/// is in the measurement. What remains is Feather's own work: walking the chain, printing the
/// document, reading the reply and turning it into objects. This is the tier to profile in,
/// because a profile taken over <see cref="ClientComparison"/> is mostly <c>HttpClient</c>.
/// </para>
/// <para>
/// Reading the reply is in the number and cannot be taken out: the transport seam takes the
/// contract to read a reply through rather than returning a parsed element, so reading is how
/// the rows come to exist at all.
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
    public void Setup() => _executor = new CannedExecutor(Payloads.Body(Rows));

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
    /// The reply is deserialized through the contract it is handed, which is the one thing a
    /// transport does that this benchmark cannot take out — it is where the rows come from. What
    /// is excluded is the transport itself: no HTTP, no request serialization, and no buffering
    /// of a response that is already a byte array.
    /// </remarks>
    private sealed class CannedExecutor(byte[] reply) : IGraphQLQueryExecutor
    {
        public IFilterTranslationProvider FilterProvider => HotChocolateFilterProvider.Instance;

        public ValueTask<IReadOnlyList<TElement>> ExecuteAsync<TElement>(
            GraphQLOperation operation,
            CancellationToken cancellationToken)
            => new(GraphQLReplyReader.ReadRows<TElement>(reply, operation));

        public IAsyncEnumerable<TElement> StreamAsync<TElement>(
            GraphQLOperation operation,
            CancellationToken cancellationToken)
            => GraphQLReplyReader.StreamRows<TElement>(reply, operation, cancellationToken);

        public ValueTask<long> ExecuteCountAsync(
            GraphQLOperation operation,
            CancellationToken cancellationToken)
            => new(GraphQLReplyReader.ReadCount(reply, operation));
    }
}
