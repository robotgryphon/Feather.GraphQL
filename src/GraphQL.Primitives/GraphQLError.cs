using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace GraphQL;

/// <summary>
/// Represents a GraphQL Error of a GraphQL Query
/// </summary>
public record struct GraphQLError(
        [property: JsonPropertyName("message")]
        string Message)
{
    /// <summary>
    /// The locations of the error
    /// </summary>
    [JsonPropertyName("locations")]
    public GraphQLLocation[]? Locations { get; init; }

    /// <summary>
    /// The Path of the error
    /// </summary>
    [JsonPropertyName("path")]
    public ErrorPath? Path { get; init; }

    /// <summary>
    /// The extensions of the error
    /// </summary>
    [JsonPropertyName("extensions")]
    public IReadOnlyDictionary<string, object>? Extensions { get; init; }
}
