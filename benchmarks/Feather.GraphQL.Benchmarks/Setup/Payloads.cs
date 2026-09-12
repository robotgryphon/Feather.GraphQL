using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Bogus;

namespace Feather.GraphQL.Benchmarks;

/// <summary>
/// Canned server replies, built once and reused, so no benchmark pays to construct one.
/// </summary>
/// <remarks>
/// <para>
/// Sizes span three orders of magnitude on purpose. A client's fixed cost — translating,
/// building a request, parsing an envelope — dominates a one-row reply and disappears into a
/// thousand-row one, and a benchmark at a single size cannot tell those two costs apart.
/// </para>
/// <para>
/// The rows come from Bogus, seeded, so they are the length and character distribution of real
/// data while staying identical between runs. Uniform filler would flatter the parsers: real
/// names vary in length, carry non-ASCII, and defeat the short-string paths a benchmark built on
/// <c>"Country 1"</c> would quietly measure instead.
/// </para>
/// </remarks>
public static class Payloads
{
    /// <summary>
    /// The row counts size-sensitive benchmarks run at unless they say otherwise.
    /// </summary>
    /// <remarks>
    /// A default rather than a restriction: these are built up front because most classes ask for
    /// them, and <see cref="Body"/> builds any other size on first use. A class free to choose its
    /// own <c>[Params]</c> and then failing in <c>GlobalSetup</c> for choosing them is a trap, and
    /// the failure reads as eleven dead rows in a table rather than as one missing payload.
    /// </remarks>
    public static readonly int[] Sizes = [1, 100, 1000];

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

    // Declared before the bodies: static field initializers run in order, and Build reads these.
    private static readonly ConcurrentDictionary<int, byte[]> _bodies =
        new(Sizes.ToDictionary(size => size, Build));

    /// <summary>
    /// A full GraphQL reply of <paramref name="rows"/> countries, as UTF-8.
    /// </summary>
    /// <remarks>
    /// Built once per size and kept, so no benchmark pays to construct one — including a size
    /// outside <see cref="Sizes"/>, which is built the first time it is asked for. That happens in
    /// <c>GlobalSetup</c>, outside anything being measured.
    /// </remarks>
    public static byte[] Body(int rows) => _bodies.GetOrAdd(rows, Build);

    /// <summary>The same reply's <c>data</c> field, already parsed.</summary>
    public static JsonElement Data(int rows)
        => JsonDocument.Parse(Body(rows)).RootElement.GetProperty("data");

    /// <summary>
    /// A reply shaped as <c>{ name continent { name } }</c> asks for: a scalar, and one reached
    /// through an object.
    /// </summary>
    /// <remarks>
    /// What <see cref="ClientComparison"/> measures over, so that every row there asks for the same
    /// fields and is sent the same ones back. A payload holding more than the documents ask for
    /// would be read by whichever client deserializes the whole object and skipped by whichever
    /// reads what its document named — which is a difference in what the rows were told to do, not
    /// in how well they do it.
    /// </remarks>
    public static byte[] NestedBody(int rows) => _nested.GetOrAdd(rows, BuildNested);

    private static readonly ConcurrentDictionary<int, byte[]> _nested = new();

    private static byte[] BuildNested(int rows)
    {
        var builder = new StringBuilder("{\"data\":{\"countries\":[");
        var source = Generate(rows);

        for (int i = 0; i < source.Length; i++)
        {
            if (i > 0)
                builder.Append(',');

            builder.Append("{\"name\":").Append(JsonSerializer.Serialize(source[i].Name))
                .Append(",\"continent\":{\"name\":")
                .Append(JsonSerializer.Serialize(source[i].Continent.Name)).Append("}}");
        }

        return Encoding.UTF8.GetBytes(builder.Append("]}}").ToString());
    }

    /// <summary>The rows a reply of this size carries, as objects.</summary>
    public static Country[] Rows(int rows) => Generate(rows);

    /// <summary>
    /// Serializes through the model rather than writing JSON by hand, so the canned body is
    /// exactly what a server sending this shape would produce — no hand-escaped string can drift
    /// from the type the benchmarks deserialize into.
    /// </summary>
    private static byte[] Build(int rows)
    {
        var payload = new { data = new CountriesData { Countries = Generate(rows) } };

        var builder = new StringBuilder("{\"data\":");
        builder.Append(JsonSerializer.Serialize(
            payload.data, BenchmarkSerializerContext.Default.CountriesData));
        builder.Append('}');

        return Encoding.UTF8.GetBytes(builder.ToString());
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
            .UseSeed(20260909 + rows)
            .StrictMode(true)
            .RuleFor(c => c.Name, f => f.Address.Country())
            .RuleFor(c => c.Code, f => f.Address.CountryCode())
            .RuleFor(c => c.Continent, _ => continents.Generate())
            .Generate(rows)
            .ToArray();
    }

}
