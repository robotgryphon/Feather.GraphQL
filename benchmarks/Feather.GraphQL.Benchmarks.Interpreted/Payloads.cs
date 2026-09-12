using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Bogus;

namespace Feather.GraphQL.Benchmarks.Interpreted;

/// <summary>
/// Canned server replies, built once and reused, so no benchmark pays to construct one.
/// </summary>
/// <remarks>
/// The same rows, from the same seed, in the same shape as <c>Feather.GraphQL.Benchmarks</c>
/// builds them. That is the point of the duplication: the two projects differ by one project
/// reference, and a number from either is only worth comparing with the other if the bytes they
/// read are identical.
/// </remarks>
public static class Payloads
{
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

    private static readonly ConcurrentDictionary<int, byte[]> _nested = new();

    /// <summary>
    /// A reply shaped as <c>{ name continent { name } }</c> asks for: a scalar, and one reached
    /// through an object.
    /// </summary>
    public static byte[] NestedBody(int rows) => _nested.GetOrAdd(rows, BuildNested);

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

    /// <summary>
    /// A fixed seed per size, and the same one the generated benchmarks use: every run of either
    /// project sees byte-identical rows, so a difference between them is a difference in the code.
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
