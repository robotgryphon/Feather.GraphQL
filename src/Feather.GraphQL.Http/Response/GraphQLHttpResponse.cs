using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using Feather.GraphQL.Primitives;

namespace Feather.GraphQL.Http.Response;

internal readonly record struct GraphQLHttpResponse(
        HttpResponseHeaders ResponseHeaders,
        HttpStatusCode StatusCode
) : IGraphQLHttpResponse
{
    [JsonPropertyName("errors")]
    public GraphQLError[]? Errors { get; init; }

    [JsonPropertyName("extensions")]
    public IReadOnlyDictionary<string, object>? Extensions { get; init; }
}
