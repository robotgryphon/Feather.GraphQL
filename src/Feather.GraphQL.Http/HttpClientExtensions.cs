using System.Net.Http.Json;
using Feather.GraphQL.Http.Request;
using Feather.GraphQL.Http.Response;
using GraphQL.Request;
using GraphQL.Response;

namespace Feather.GraphQL.Http;

public static class HttpClientExtensions
{
    extension(HttpClient client)
    {
        public async ValueTask<HttpResponseMessage> SendGraphQLQueryAsync(GraphQLRequest request,
                CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request?.Query);

            cancellationToken.ThrowIfCancellationRequested();

            return await client.PostGraphQueryAsync(request, cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask<HttpResponseMessage> SendMutationAsync(GraphQLRequest request,
                CancellationToken cancellationToken = default)
            => await client.PostGraphQueryAsync(request, cancellationToken).ConfigureAwait(false);

        private async ValueTask<HttpResponseMessage> PostGraphQueryAsync(GraphQLRequest request,
                CancellationToken cancellationToken = default)
        {
            var req = request.AsHttpPost();
            req.RequestUri ??= client.BaseAddress;

            return await client
                    .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
        }
    }

    extension(HttpResponseMessage responseMessage)
    {
        public async ValueTask<IGraphQLHttpDataResponse<TResponse>> AsDataResponse<TResponse>(
                CancellationToken cancellationToken = default)
        {
            var data = await responseMessage.Content.ReadAsGraphQLAsync<TResponse>(cancellationToken)
                    .ConfigureAwait(false);

            return new GraphQLHttpDataResponse<TResponse>(responseMessage.Headers, responseMessage.StatusCode)
            {
                    Data = data!.Data, Errors = data.Errors, Extensions = data.Extensions
            };
        }
    }

    extension(HttpContent response)
    {
        public async ValueTask<IGraphQLDataResponse<TResponse>?> ReadAsGraphQLAsync<TResponse>(
                CancellationToken cancellationToken = default)
        {
            var httpResponse = await response
                    .ReadFromJsonAsync<GraphQLHttpDataResponse<TResponse>>(cancellationToken)
                    .ConfigureAwait(false);

            return httpResponse;
        }
    }
}
