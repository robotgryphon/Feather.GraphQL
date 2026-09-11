using System.Text.Json.Serialization;

namespace Feather.GraphQL.Example;

/// <summary>
/// The contracts query results are materialized through, so nothing reflects over
/// <see cref="Country"/> at runtime.
/// </summary>
/// <remarks>
/// Declared here rather than generated: a <see cref="JsonSerializerContext"/> has to be in source
/// that System.Text.Json's own generator can see, and a generator cannot see what another
/// generator emitted. Registering it is automatic — the analyzer package finds it.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(Country))]
[JsonSerializable(typeof(Continent))]
internal sealed partial class CountrySerializerContext : JsonSerializerContext;
