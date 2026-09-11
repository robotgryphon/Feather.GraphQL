using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using BenchmarkDotNet.Attributes;
using Feather.GraphQL.Http;
using Feather.GraphQL.Linq.Providers;
using Feather.GraphQL.Linq.Query;
using GraphQL;
using GraphQL.Client.Http;
using GraphQL.Client.Serializer.SystemTextJson;

namespace Feather.GraphQL.Benchmarks;

/// <summary>
/// Feather against GraphQL.Client, doing the same job: post a query string, read typed data out
/// of the reply.
/// </summary>
/// <remarks>
/// <para>
/// The comparison is deliberately narrow, because it is the only part of the two libraries that
/// is actually comparable. GraphQL.Client takes a query string; it has no LINQ translation, no
/// projection and no filter lowering. Feather's translation is measured separately, in
/// <c>QueryTranslation</c>, so that what it costs is visible rather than smuggled into a number
/// labelled as a like-for-like comparison.
/// </para>
/// <para>
/// Everything outside the libraries is held equal: one shared <see cref="HttpMessageHandler"/>,
/// the same canned reply, and the same source-generated serializer contracts on both sides.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class ClientComparison
{
    private const string Query =
        "query { countries { name code continent { code name } } }";

    private CannedTransport _transport = null!;
    private HttpClient _feather = null!;
    private HttpClient _shared = null!;
    private GraphQLHttpClient _other = null!;

    /// <summary>How many rows the canned reply carries.</summary>
    [Params(1, 100, 1000)]
    public int Rows { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _transport = new CannedTransport(Payloads.Body(Rows));
        _feather = _transport.Client();
        _shared = _transport.Client();

        var options = new JsonSerializerOptions
        {
            TypeInfoResolver = BenchmarkSerializerContext.Default,
            PropertyNameCaseInsensitive = true
        };

        _other = new GraphQLHttpClient(
            new GraphQLHttpClientOptions { EndPoint = _shared.BaseAddress },
            new SystemTextJsonSerializer(options),
            _shared);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        // Disposes _shared with it: GraphQLHttpClient owns the HttpClient it is constructed with.
        _other.Dispose();
        _feather.Dispose();
        _transport.Dispose();
    }

    [Benchmark(Baseline = true, Description = "Feather: send + read")]
    public async Task<int> Feather()
    {
        using var response = await _feather.SendGraphQLQueryAsync(Query);

        return (await response.ReadGraphQLAsync<CountriesData>()).Countries.Length;
    }

    /// <summary>
    /// The LINQ surface end to end: translate, post, materialize.
    /// </summary>
    /// <remarks>
    /// Deliberately unbounded, which is what FGQL012 is about — the row exists to measure the
    /// cheapest chain the provider accepts, and a predicate or a page would put translation work
    /// into a number meant to isolate everything else. The document it prints selects only the
    /// element's scalars, so it is shorter than the one the other two rows post; the reply is the
    /// same canned payload either way, so the read side being compared is unchanged.
    /// </remarks>
#pragma warning disable FGQL012
    [Benchmark(Description = "Feather: LINQ")]
    public async Task<int> FeatherLinq()
    {
        var response = await _feather.CreateQueryable<Country>("countries")
            .ToArrayAsync()
            .ConfigureAwait(false);

        return response.Length;
    }
#pragma warning restore FGQL012

    [Benchmark(Description = "GraphQL.Client: send + read")]
    public async Task<int> Other()
    {
        var response = await _other.SendQueryAsync<CountriesData>(new GraphQLRequest(Query));

        return response.Data.Countries.Length;
    }
}
