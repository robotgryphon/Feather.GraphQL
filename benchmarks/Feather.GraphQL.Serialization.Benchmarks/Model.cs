using System.Text.Json.Serialization;

namespace Feather.GraphQL.Serialization.Benchmarks;

/// <summary>The element every benchmark here reads. A plain POCO, as the library requires.</summary>
public class Country
{
    public string Name { get; set; } = "";

    public string Code { get; set; } = "";

    public Continent Continent { get; set; } = new();
}

/// <summary>A nested object, so a reader has something to descend into.</summary>
public class Continent
{
    public string Code { get; set; } = "";

    public string Name { get; set; } = "";
}

/// <summary>
/// What a narrowing projection asks for: two fields, one of them reached through another.
/// </summary>
/// <remarks>
/// Deliberately not shaped like <see cref="Country"/>. <c>Title</c> is not <c>name</c> and
/// <c>Continent</c> is a string where the reply has an object, so no amount of name matching
/// reads a reply into this — it has to be shaped, which is the case the fused reader is for.
/// </remarks>
public class CountrySummary
{
    public string Title { get; set; } = "";

    public string Continent { get; set; } = "";
}

/// <summary>
/// The envelope a caller has to declare to read a reply with <c>System.Text.Json</c> alone.
/// </summary>
/// <remarks>
/// Two types that exist for the serializer and nobody else: <c>data</c>, and the root field
/// inside it. They are the baseline's real cost as much as its parsing is — the library's readers
/// take the field as <c>data</c>'s single member and need neither.
/// </remarks>
public class CountriesReply
{
    public CountriesData? Data { get; set; }
}

/// <inheritdoc cref="CountriesReply"/>
public class CountriesData
{
    public Country[] Countries { get; set; } = [];
}

/// <summary>
/// Source-generated contracts for the model.
/// </summary>
/// <remarks>
/// What the second row of the comparison uses, and what a consumer of this library declares to
/// keep reflection out of their reads. Same types, same JSON, same naming policy as the
/// reflection row — so the gap between those two rows is the source generator and nothing else.
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CountriesReply))]
[JsonSerializable(typeof(CountriesData))]
[JsonSerializable(typeof(Country[]))]
[JsonSerializable(typeof(Country))]
[JsonSerializable(typeof(Continent))]
public partial class BenchmarkSerializerContext : JsonSerializerContext;
