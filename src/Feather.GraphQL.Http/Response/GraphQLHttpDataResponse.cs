using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using GraphQL;

namespace Feather.GraphQL.Http.Response;

public record GraphQLHttpDataResponse<T>(
        HttpResponseHeaders ResponseHeaders,
        HttpStatusCode StatusCode
) : IGraphQLHttpDataResponse<T>
{
    [JsonPropertyName("data")]
    public T? Data { get; init; }

    [JsonPropertyName("errors")]
    public GraphQLError[]? Errors { get; init; }

    [JsonPropertyName("extensions")]
    public IReadOnlyDictionary<string, object>? Extensions { get; init; }
}
