using System.Runtime.CompilerServices;
using System.Buffers;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Feather.GraphQL.Http;
using Feather.GraphQL.Serialization;
using Feather.GraphQL.Linq.Filtering;
using Feather.GraphQL.Linq.Providers;
using Feather.GraphQL.Linq.Query;
using GraphQL;
using GraphQL.Client.Http;
using GraphQL.Client.Serializer.SystemTextJson;
// ReSharper disable UseConfigureAwaitFalse
// ReSharper disable InconsistentNaming

namespace Feather.GraphQL.Benchmarks;

/// <summary>
/// Every way this library has of asking the same question, and GraphQL.Client asking it too.
/// </summary>
[MemoryDiagnoser]
[WarmupCount(1)]
public partial class ClientComparison
{
    private const string ContinentCode = "EU";

    /// <summary>The variables the filtered document binds.</summary>
    /// <remarks>
    /// Built once. A caller sending the same query repeatedly holds theirs the same way,
    /// and rebuilding it per iteration would measure the dictionary rather than the request.
    /// </remarks>
    private static readonly Dictionary<string, object?> _continent =
        new() { ["continent"] = ContinentCode };

    private const string Query =
        "query { countries { name continent { name } } }";

    /// <summary>The filtered document, as the declared surface would have written it.</summary>
    private const string FilteredQuery =
        "query($code: String!) { countries(filter: { continent: { eq: $code } }) "
        + "{ name continent { name } } }";

    private CannedTransport _transport = null!;
    private HttpClient _feather = null!;
    private HttpClient _shared = null!;
    private GraphQLHttpClient _other = null!;

    /// <summary>How many rows the canned reply carries.</summary>
    [Params(1, 25)]
    public int Rows { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        // Exactly the fields every row's document names, so no row is charged for reading
        // something another row was never asked for.
        _transport = new CannedTransport(Payloads.NestedBody(Rows));
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

    // ---- static: a document written by hand ------------------------------------------------

    [Benchmark(Baseline = true, Description = "Static")]
    public async Task<int> Static()
    {
        using var response = await _feather.SendGraphQLQueryAsync(Query);

        return (await response.ReadGraphQLAsync<CountriesData>()).Countries.Length;
    }

    /// <summary>
    /// The same by hand, with the value bound to a variable.
    /// </summary>
    /// <remarks>
    /// A document and a dictionary, which is all a caller sending their own query has. The
    /// declared row below sends the same document with a body the compiler wrote instead, so the
    /// pair measures what declaring saves — and that is now only the escaping of the document
    /// itself, since both build their body the same way.
    /// </remarks>
    [Benchmark(Description = "Static (filtered)")]
    public async Task<int> StaticFiltered()
    {
        using var response = await _feather.SendGraphQLQueryAsync(FilteredQuery, _continent);

        return (await response.ReadGraphQLAsync<CountriesData>()).Countries.Length;
    }

    // ---- declared: [GraphQLQuery] ------------------------------------------------------------

    /// <summary>
    /// The declared surface: the document is a literal and the values are parameters.
    /// </summary>
    /// <remarks>
    /// Nothing is composed, so no expression tree is built and nothing is walked to recover a
    /// value the caller already had.
    /// </remarks>
    [Benchmark(Description = "Feather: AOT SourceGen")]
    public async Task<int> Declared()
        => (await CountriesAsync(_feather, CancellationToken.None)).Length;

    [GraphQLQuery(Query)]
    private static partial Task<Country[]> CountriesAsync(
        HttpClient client, CancellationToken cancellationToken);

    /// <summary>The same filtered query, declared. Nothing is composed and nothing is walked.</summary>
    [Benchmark(Description = "Feather: AOT SourceGen (filtered)")]
    public async Task<int> DeclaredFiltered()
        => (await FilteredAsync(_feather, ContinentCode, CancellationToken.None)).Length;

    [GraphQLQuery(FilteredQuery)]
    private static partial Task<Country[]> FilteredAsync(
        HttpClient client, string code, CancellationToken cancellationToken);


    // ---- compiled: [GraphQLQuery] over a chain -------------------------------------------------

    /// <summary>
    /// The same query written as LINQ and compiled, rather than written as a document.
    /// </summary>
    /// <remarks>
    /// The row that says what the LINQ surface costs once the compiler has had it: the document is
    /// printed at build time, the variables payload is written from the method's own parameters,
    /// and the reply is read by a reader generated for this query's shape. Nothing is composed at
    /// run time, so it should land on the declared rows above — the two are the same request
    /// reached from opposite directions, and a gap between them would mean one of them is doing
    /// work the other found a way to avoid.
    /// </remarks>
    [Benchmark(Description = "Feather: LINQ compiled")]
    public async Task<int> LinqCompiled()
        => (await CompiledAsync(_feather, CancellationToken.None)).Length;

    /// <inheritdoc cref="LinqCompiled"/>
    [Benchmark(Description = "Feather: LINQ compiled (filtered)")]
    public async Task<int> LinqCompiledFiltered()
        => (await CompiledFilteredAsync(_feather, ContinentCode, CancellationToken.None)).Length;

    /// <summary>
    /// The chains, isolated. They never run: every call to them is replaced by the compiled
    /// request, and what is left here is the description the compiler wrote that request from.
    /// </summary>
    /// <remarks>
    /// The projection is what makes the chain ask for the same fields the documents above name —
    /// an unprojected chain selects the element's own scalars and would never reach into
    /// <c>continent</c>. The filter is written against the server's input model, which is the
    /// shape a real schema forces and the one the compiler has to be able to print.
    /// </remarks>
    [GraphQLQuery]
    private static Task<Country[]> CompiledAsync(
        HttpClient client, CancellationToken cancellationToken)
        => client.CreateQueryable<Country>("countries")
            .Select(c => new Country { Name = c.Name, Continent = new Continent { Name = c.Continent.Name } })
            .ToArrayAsync(cancellationToken);

    /// <inheritdoc cref="CompiledAsync"/>
    [GraphQLQuery]
    private static Task<Country[]> CompiledFilteredAsync(
        HttpClient client, string code, CancellationToken cancellationToken)
        => client.CreateQueryable<Country>("countries")
            .Where("filter", (CountryFilter c) => c.Continent == code)
            .Select(c => new Country { Name = c.Name, Continent = new Continent { Name = c.Continent.Name } })
            .ToArrayAsync(cancellationToken);

    // ---- the other library -------------------------------------------------------------------

    [Benchmark(Description = "GraphQL.Client: send + read")]
    public async Task<int> Other()
    {
        var response = await _other.SendQueryAsync<CountriesData>(new GraphQLRequest(Query));

        return response.Data.Countries.Length;
    }
}

/// <summary>
/// A model of the server's filter input, whose shape is not the element's.
/// </summary>
/// <remarks>
/// A reply's <c>continent</c> is an object with a name; the filter input takes a string filter
/// directly. Declared here rather than borrowed from the example project, which references the
/// published packages and would put a second copy of the library on this compilation.
/// </remarks>
internal sealed class CountryFilter
{
    public string? Continent { get; set; }
}
