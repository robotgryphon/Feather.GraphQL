using System.Text.Json;
using System.Text.Json.Serialization;
using BenchmarkDotNet.Attributes;

namespace Feather.GraphQL.Serialization.Benchmarks;

/// <summary>
/// Four ways to turn a GraphQL reply into rows, over the same bytes.
/// </summary>
/// <remarks>
/// <para>
/// Every row here reads the identical payload into the identical <see cref="Country"/> objects.
/// What differs is how much has to be worked out at run time:
/// </para>
/// <list type="table">
/// <item>
///   <term><c>reflection</c></term>
///   <description>
///   <c>System.Text.Json</c> with no contracts declared. It discovers the types, their
///   properties and their names on first use, and reads through that discovery afterwards. What
///   a consumer gets by doing nothing.
///   </description>
/// </item>
/// <item>
///   <term><c>source-generated</c></term>
///   <description>
///   The same serializer over a <c>JsonSerializerContext</c>. The contracts are written at build
///   time, so nothing is discovered — but the reader is still a general one, driving a model that
///   could be any type.
///   </description>
/// </item>
/// <item>
///   <term><c>Feather: generated reader</c></term>
///   <description>
///   A reader for this one query's shape: field names compared as UTF-8 literals against the
///   fields the document asked for, rows built where they are read. Nothing general, because the
///   shape was known when it was written.
///   </description>
/// </item>
/// <item>
///   <term><c>Feather: shape reader</c></term>
///   <description>
///   The same reading, through <c>IGraphQLRowShape</c>: the per-row switch is a generated struct
///   and the envelope, loop and buffer are the library's. It is here to prove the structure costs
///   nothing — a driver reached through a generic constraint should specialise away, and if it
///   does not then the tidier arrangement is not worth having.
///   </description>
/// </item>
/// </list>
/// <para>
/// The first two rows also differ in what a consumer has to <em>write</em>, which the table cannot
/// show: reading a reply with the serializer alone means declaring <see cref="CountriesReply"/>
/// and <see cref="CountriesData"/>, two types that exist for the envelope and nobody else.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class ReplyDeserialization
{
    private byte[] _body = null!;

    private static readonly JsonSerializerOptions _reflection = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <inheritdoc cref="Payloads.Sizes"/>
    [Params(1, 25, 100, 500)]
    public int Rows { get; set; }

    [GlobalSetup]
    public void Setup() => _body = Payloads.Body(Rows);

    [Benchmark(Baseline = true, Description = "reflection")]
    public int Reflection()
        => JsonSerializer.Deserialize<CountriesReply>(_body, _reflection)?.Data?.Countries.Length ?? 0;

    [Benchmark(Description = "source-generated")]
    public int SourceGenerated()
        => JsonSerializer.Deserialize(_body, BenchmarkSerializerContext.Default.CountriesReply)
            ?.Data?.Countries.Length ?? 0;

}

/// <summary>
/// The same comparison for a query that narrowed its selection.
/// </summary>
/// <remarks>
/// <para>
/// A projection changes what the question is. The serializer can only read a reply into a type
/// shaped like the reply, so a caller who wanted <see cref="CountrySummary"/> reads
/// <see cref="Country"/> rows and shapes them afterwards — two passes, and one object per row
/// that exists only to be discarded. A generated reader builds the row the caller asked for where
/// it reads it.
/// </para>
/// <para>
/// The payload is the narrow one for every row, because that is what the server sends back for a
/// projecting query. Reading a wide payload here would hide the saving the narrowing is for.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class ProjectedDeserialization
{
    private byte[] _narrow = null!;

    /// <inheritdoc cref="Payloads.Sizes"/>
    [Params(1, 25, 100, 500)]
    public int Rows { get; set; }

    [GlobalSetup]
    public void Setup() => _narrow = Payloads.NarrowBody(Rows);

    /// <summary>Read the rows the serializer can read, then shape them into the ones that were wanted.</summary>
    [Benchmark(Baseline = true, Description = "source-generated + shape")]
    public int SourceGeneratedThenShape()
    {
        var source = JsonSerializer.Deserialize(_narrow, BenchmarkSerializerContext.Default.CountriesReply)
            ?.Data?.Countries ?? [];

        var rows = new CountrySummary[source.Length];

        for (int i = 0; i < source.Length; i++)
        {
            var c = source[i];

            rows[i] = new CountrySummary { Title = c.Name, Continent = c.Continent.Name };
        }

        return rows.Length;
    }

}
