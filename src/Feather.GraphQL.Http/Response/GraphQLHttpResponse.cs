using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using GraphQL;
using GraphQL.Response;

namespace Feather.GraphQL.Http.Response;

public readonly record struct GraphQLHttpResponse(
        HttpResponseHeaders ResponseHeaders,
        HttpStatusCode StatusCode
) : IGraphQLHttpResponse, IGraphQLResponse
{
    [JsonPropertyName("errors")]
    public GraphQLError[]? Errors { get; init; }

    [JsonPropertyName("extensions")]
    public IReadOnlyDictionary<string, object>? Extensions { get; init; }
}
