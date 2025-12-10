using System.Text.Json.Serialization;

namespace Feather.GraphQL.Tests.StarWars.Client;

public class HumanResponse
{
    [JsonPropertyName("human")]
    public Human? Human { get; init; }
}
