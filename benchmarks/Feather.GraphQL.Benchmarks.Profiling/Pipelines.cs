using Feather.GraphQL.Benchmarks;
using Feather.GraphQL.Http;
using Feather.GraphQL.Linq.Filtering;
using Feather.GraphQL.Linq.Providers;
using Feather.GraphQL.Linq.Query;

namespace Feather.GraphQL.Benchmarks.Profiling;

/// <summary>
/// One call each, for the pipelines worth profiling.
/// </summary>
/// <remarks>
/// <para>
/// Each is the whole of what a caller does: post, and read what comes back. They ask for the same
/// fields and are answered with the same bytes, so a profile of one is comparable with a profile
/// of the other — which is the only reason to have them in one program.
/// </para>
/// <para>
/// The methods are deliberately shallow. A profiler attributes to frames, and a wrapper of this
/// library's between the caller and the work would be a frame in every profile that says nothing
/// about either pipeline.
/// </para>
/// </remarks>
internal static class Pipelines
{
    private const string ContinentCode = "EU";

    /// <summary>The document, written out, as a caller with no LINQ would send it.</summary>
    private const string Query =
        "query { countries { name continent { name } } }";

    /// <summary>
    /// Post a document and read the reply into a declared type.
    /// </summary>
    /// <remarks>
    /// Nothing is generated for this: the reply goes through a serializer contract resolved at run
    /// time, into a wrapper type that exists to hold the root field. What a profile should show is
    /// where that costs more than reading with a reader written for the shape.
    /// </remarks>
    public static async Task<int> StaticAsync(HttpClient client)
    {
        using var response = await client.SendGraphQLQueryAsync(Query).ConfigureAwait(false);

        return (await response.ReadGraphQLAsync<CountriesData>().ConfigureAwait(false)).Countries.Length;
    }

    /// <summary>
    /// The same query as a compiled chain.
    /// </summary>
    /// <remarks>
    /// The document was printed at build time, the payload is written from the method's own
    /// parameters, and the reply is read by a reader generated for this query's shape. The body
    /// below never runs — every call to it is replaced by what it compiles to — so a frame for it
    /// in a profile would mean the interception did not happen.
    /// </remarks>
    public static async Task<int> CompiledAsync(HttpClient client)
        => (await Compiled(client, CancellationToken.None).ConfigureAwait(false)).Length;

    /// <inheritdoc cref="CompiledAsync"/>
    public static async Task<int> CompiledFilteredAsync(HttpClient client)
        => (await CompiledFiltered(client, ContinentCode, CancellationToken.None).ConfigureAwait(false)).Length;

    [GraphQLQuery]
    private static Task<Country[]> Compiled(HttpClient client, CancellationToken cancellationToken)
        => client.CreateQueryable<Country>("countries")
            .Select(c => new Country { Name = c.Name, Continent = new Continent { Name = c.Continent.Name } })
            .ToArrayAsync(cancellationToken);

    /// <inheritdoc cref="Compiled"/>
    [GraphQLQuery]
    private static Task<Country[]> CompiledFiltered(
        HttpClient client, string code, CancellationToken cancellationToken)
        => client.CreateQueryable<Country>("countries")
            .Where("filter", (CountryFilter c) => c.Continent == code)
            .Select(c => new Country { Name = c.Name, Continent = new Continent { Name = c.Continent.Name } })
            .ToArrayAsync(cancellationToken);
}

/// <summary>
/// A model of the server's filter input, whose shape is not the element's.
/// </summary>
/// <remarks>
/// A reply's <c>continent</c> is an object with a name; the filter input takes a string filter
/// directly. Declared here rather than borrowed from the example project, so a program meant for
/// profiling pulls in nothing it does not run.
/// </remarks>
internal sealed class CountryFilter
{
    public string? Continent { get; set; }
}
