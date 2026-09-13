using System.Text.Json.Serialization;
using Feather.GraphQL.Primitives;

namespace Feather.GraphQL.Serialization;

/// <summary>
/// The contract for this library's own error shape.
/// </summary>
/// <remarks>
/// Errors are the one part of a reply whose type belongs to this assembly rather than to the
/// caller, so the caller cannot be the one to declare it. Generated here, which is also what
/// keeps reading them off the reflection path an AOT build cannot take.
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GraphQLError[]))]
internal sealed partial class GraphQLErrorContext : JsonSerializerContext;
