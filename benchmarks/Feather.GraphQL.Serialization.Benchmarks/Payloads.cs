using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Bogus;

namespace Feather.GraphQL.Serialization.Benchmarks;

/// <summary>
/// Canned server replies, built once and reused, so no benchmark pays to construct one.
/// </summary>
/// <remarks>
/// <para>
/// The sizes stop at 500 on purpose. This project measures reading, and reading is linear in the
/// rows — past a few hundred every row costs the same as the last and the table only repeats
/// itself. What changes across 1 to 500 is the part that is <em>not</em> linear: the envelope, the
/// first-call contract resolution, the buffer that has to grow.
/// </para>
/// <para>
/// The rows come from Bogus, seeded, so they are the length and character distribution of real
/// data while staying identical between runs. Uniform filler would flatter the parsers: real
/// names vary in length, carry non-ASCII, and defeat the short-string paths a payload built on
/// <c>"Country 1"</c> would quietly measure instead.
/// </para>
/// </remarks>
public static class Payloads
{
    /// <summary>The row counts every benchmark here runs at.</summary>
    public static readonly int[] Sizes = [1, 25, 100, 500];

    private static readonly string[] _continentCodes = ["AF", "AN", "AS", "EU", "NA", "OC", "SA"];

    private static readonly Dictionary<string, string> _continentNames = new(StringComparer.Ordinal)
    {
        ["AF"] = "Africa",
        ["AN"] = "Antarctica",
        ["AS"] = "Asia",
        ["EU"] = "Europe",
        ["NA"] = "North America",
        ["OC"] = "Oceania",
        ["SA"] = "South America"
    };

    private static readonly ConcurrentDictionary<int, byte[]> _bodies = new();
    private static readonly ConcurrentDictionary<int, byte[]> _narrow = new();

    /// <summary>
    /// A full reply of <paramref name="rows"/> countries, as UTF-8: every field of the type.
    /// </summary>
    /// <remarks>
    /// What a query that named the whole element gets back. Built once per size and kept, so the
    /// cost lands in <c>GlobalSetup</c> rather than in a measurement.
    /// </remarks>
    public static byte[] Body(int rows) => _bodies.GetOrAdd(rows, Build);

    /// <summary>
    /// The same reply as a projecting query would receive: only the fields it named.
    /// </summary>
    /// <remarks>
    /// Kept separate because the point of projecting is that the reply is smaller. Comparing a
    /// shaped read against an unshaped one over the same wide payload would measure both paths
    /// skipping the same unread fields, and miss the saving the narrowing is for.
    /// </remarks>
    public static byte[] NarrowBody(int rows) => _narrow.GetOrAdd(rows, BuildNarrow);

    /// <summary>The rows a reply of this size carries, as objects.</summary>
    public static Country[] Rows(int rows) => Generate(rows);

    /// <summary>
    /// Serializes through the model rather than writing JSON by hand, so the canned body is
    /// exactly what a server sending this shape would produce — no hand-escaped string can drift
    /// from the type the benchmarks deserialize into.
    /// </summary>
    private static byte[] Build(int rows)
    {
        var builder = new StringBuilder("{\"data\":{\"countries\":");

        builder.Append(JsonSerializer.Serialize(
            Generate(rows), BenchmarkSerializerContext.Default.CountryArray));

        return Encoding.UTF8.GetBytes(builder.Append("}}").ToString());
    }

    /// <summary>The reply for <c>{ countries { name continent { name } } }</c>.</summary>
    private static byte[] BuildNarrow(int rows)
    {
        var builder = new StringBuilder("{\"data\":{\"countries\":[");
        var source = Generate(rows);

        for (int i = 0; i < source.Length; i++)
        {
            if (i > 0)
                builder.Append(',');

            builder.Append("{\"name\":").Append(JsonSerializer.Serialize(source[i].Name))
                .Append(",\"continent\":{\"name\":")
                .Append(JsonSerializer.Serialize(source[i].Continent.Name))
                .Append("}}");
        }

        return Encoding.UTF8.GetBytes(builder.Append("]}}").ToString());
    }

    /// <summary>
    /// A fixed seed per size: every run of a given benchmark sees byte-identical rows, so a
    /// difference between two runs is a difference in the code and never in the data.
    /// </summary>
    private static Country[] Generate(int rows)
    {
        var continents = new Faker<Continent>()
            .StrictMode(true)
            .RuleFor(c => c.Code, f => f.PickRandom(_continentCodes))
            .RuleFor(c => c.Name, (_, c) => _continentNames[c.Code]);

        return new Faker<Country>()
            .UseSeed(20260912 + rows)
            .StrictMode(true)
            .RuleFor(c => c.Name, f => f.Address.Country())
            .RuleFor(c => c.Code, f => f.Address.CountryCode())
            .RuleFor(c => c.Continent, _ => continents.Generate())
            .Generate(rows)
            .ToArray();
    }
}
