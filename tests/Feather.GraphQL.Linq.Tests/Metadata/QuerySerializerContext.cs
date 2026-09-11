using System.Text.Json.Serialization;
using Feather.GraphQL.Linq.Tests.Query;

namespace Feather.GraphQL.Linq.Tests.Metadata;

/// <summary>
/// The contracts results are materialized through, declared here because a
/// <see cref="JsonSerializerContext"/> has to live in source a generator can see — STJ's
/// generator does not process what another generator emits.
/// </summary>
/// <remarks>
/// Nothing registers this by hand: the analyzer package finds it and emits the registration in a
/// module initializer.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(Person))]
[JsonSerializable(typeof(Gadget))]
[JsonSerializable(typeof(Dimensions))]
[JsonSerializable(typeof(Country))]
[JsonSerializable(typeof(CountryContinent))]
internal sealed partial class QuerySerializerContext : JsonSerializerContext;
