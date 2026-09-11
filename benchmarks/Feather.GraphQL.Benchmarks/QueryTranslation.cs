using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using Feather.GraphQL.Linq.Query;

namespace Feather.GraphQL.Benchmarks;

/// <summary>
/// What it costs to turn a LINQ chain into a document, with no transport anywhere near it.
/// </summary>
/// <remarks>
/// <para>
/// This is the half of Feather that GraphQL.Client has no counterpart for, so it is measured on
/// its own rather than folded into <see cref="ClientComparison"/>. Read against that class: if
/// translation is a small fraction of a round trip, the LINQ surface is free in practice; if it
/// is not, this is the number to attack.
/// </para>
/// <para>
/// Three chains, in rising order of what the translator has to do — a bare projection, a
/// predicate that has to be lowered into a filter object, and a chain that also orders and pages.
/// Nothing here is intercepted: <c>ToGraphQLQuery</c> prints the inline form, which the generator
/// never supplies, so every run walks the expression tree for real.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class QueryTranslation
{
    private const string RootField = "countries";

    [Benchmark(Baseline = true, Description = "Project")]
    public string Project()
        => Source().Select(c => new { c.Name, c.Code }).ToGraphQLQuery();

    [Benchmark(Description = "Filter + project")]
    public string Filter()
        => Source()
            .Where(c => c.Continent.Code == "EU" && c.Name != "")
            .Select(c => new { c.Name, Continent = c.Continent.Name })
            .ToGraphQLQuery();

    [Benchmark(Description = "Filter + order + page + project")]
    public string Paged()
        => Source()
            .Where(c => c.Continent.Code == "EU")
            .OrderBy(c => c.Name)
            .ThenByDescending(c => c.Code)
            .Skip(20)
            .Take(10)
            .Select(c => new { c.Name, c.Code, Continent = c.Continent.Name })
            .ToGraphQLQuery();

    /// <summary>
    /// The root of every chain here, behind a call boundary the generator cannot see through.
    /// </summary>
    /// <remarks>
    /// Deliberate. A queryable handed out of a helper has no visible terminal, so the
    /// interceptor declines it and the runtime translator does the work — which is the thing
    /// this class exists to measure. <see cref="QueryPipeline"/> measures the intercepted path.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IQueryable<Country> Source()
        => GraphQLQueryable.For<Country>(RootField);
}
