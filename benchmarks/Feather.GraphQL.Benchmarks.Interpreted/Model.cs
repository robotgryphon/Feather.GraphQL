using System.Text.Json.Serialization;

namespace Feather.GraphQL.Benchmarks.Interpreted;

/// <summary>The element every benchmark here queries. A plain POCO, as the library requires.</summary>
public class Country
{
    public string Name { get; set; } = "";

    public string Code { get; set; } = "";

    public Continent Continent { get; set; } = new();
}

/// <summary>A nested object, so the reply has something to descend into.</summary>
public class Continent
{
    public string Code { get; set; } = "";

    public string Name { get; set; } = "";
}

/// <summary>The shape of the reply's <c>data</c> field, for the row that reads it whole.</summary>
public class CountriesData
{
    public Country[] Countries { get; set; } = [];
}

/// <summary>
/// Source-generated contracts for the model.
/// </summary>
/// <remarks>
/// Kept, even though this project is about what happens without generated code, because the
/// generator that writes it is <c>System.Text.Json</c>'s and ships with the framework rather than
/// with this library. Leaving it out would put reflection-based deserialization on one side of the
/// comparison and not the other, and the difference being measured here is Feather's own
/// generated code — not which serializer contract each side happened to get.
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CountriesData))]
[JsonSerializable(typeof(Country[]))]
[JsonSerializable(typeof(Country))]
[JsonSerializable(typeof(Continent))]
public partial class BenchmarkSerializerContext : JsonSerializerContext;
