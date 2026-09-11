using System.Text.Json.Serialization;
using GraphQL;

namespace Feather.GraphQL.Benchmarks;

/// <summary>The element every benchmark queries. A plain POCO, as the library requires.</summary>
public class Country
{
    public string Name { get; set; } = "";
    public string Code { get; set; } = "";
    public Continent Continent { get; set; } = new();
}

/// <summary>A nested object, so projections have something to reach through.</summary>
public class Continent
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>
/// The shape of the reply's <c>data</c> field, which is what both clients deserialize into.
/// </summary>
public class CountriesData
{
    public Country[] Countries { get; set; } = [];
}

/// <summary>
/// Source-generated contracts for the model.
/// </summary>
/// <remarks>
/// Given to <em>both</em> clients. Feather picks it up through its registry; GraphQL.Client gets
/// it through the <c>JsonSerializerOptions</c> its serializer is constructed with. Letting one
/// side use reflection-based serialization and the other source generation would make the
/// comparison a measurement of System.Text.Json configuration rather than of either library.
/// That is also why GraphQL.Client's own envelope types are declared here: leaving them to the
/// reflection fallback would tax it for something Feather does not pay.
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CountriesData))]
[JsonSerializable(typeof(Country[]))]
[JsonSerializable(typeof(Country))]
[JsonSerializable(typeof(Continent))]
[JsonSerializable(typeof(GraphQLRequest))]
[JsonSerializable(typeof(GraphQLResponse<CountriesData>))]
public partial class BenchmarkSerializerContext : JsonSerializerContext;
