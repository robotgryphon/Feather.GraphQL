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
    /// <summary>Row counts every size-sensitive benchmark runs at.</summary>
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
    private static readonly Dictionary<int, byte[]> _bodies =
        Sizes.ToDictionary(size => size, Build);

    /// <summary>A full GraphQL reply of <paramref name="rows"/> countries, as UTF-8.</summary>
    public static byte[] Body(int rows) => _bodies[rows];

    /// <summary>The same reply's <c>data</c> field, already parsed.</summary>
    public static JsonElement Data(int rows)
        => JsonDocument.Parse(_bodies[rows]).RootElement.GetProperty("data");

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
