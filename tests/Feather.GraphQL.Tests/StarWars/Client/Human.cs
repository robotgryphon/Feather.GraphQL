using System.Text.Json.Serialization;

namespace Feather.GraphQL.Tests.StarWars.Client;

public record Human
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }
}
