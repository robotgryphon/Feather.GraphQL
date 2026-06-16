using System.Net;
using System.Net.Http.Headers;
using Feather.GraphQL.Response;
using JetBrains.Annotations;

namespace Feather.GraphQL.Http.Response;

[PublicAPI]
public static class GraphQLHttpResponseExtensions
{
    extension(IGraphQLResponse response)
    {
        public IGraphQLHttpResponse ToGraphQLHttpResponse(HttpResponseHeaders responseHeaders,
                HttpStatusCode statusCode)
            => new GraphQLHttpResponse(responseHeaders, statusCode)
            {
                    Errors = response.Errors,
                    Extensions = response.Extensions
            };
    }
}
