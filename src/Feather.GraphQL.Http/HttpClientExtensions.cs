using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Json;
using Feather.GraphQL.Http.Request;
using Feather.GraphQL.Http.Response;
using Feather.GraphQL.Response;

namespace Feather.GraphQL.Http;

public static class HttpClientExtensions
{
    extension(HttpClient client)
    {
        /// <summary>
        /// Posts a precompiled GraphQL query string. Any values the query needs must already be
        /// present in its text — this path carries no variables payload.
        /// </summary>
        public async ValueTask<HttpResponseMessage> SendGraphQLQueryAsync(
                [StringSyntax("GraphQL")] string query,
                CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(query);
            cancellationToken.ThrowIfCancellationRequested();

            return await client.PostGraphQueryAsync(new GraphQLRequest(query), cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
        }

        /// <summary>
        /// Posts a precompiled GraphQL mutation string. Any values the mutation needs must
        /// already be present in its text — this path carries no variables payload.
        /// </summary>
        public async ValueTask<HttpResponseMessage> SendMutationAsync(
                [StringSyntax("GraphQL")] string mutation,
                CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(mutation);
            cancellationToken.ThrowIfCancellationRequested();

            return await client.PostGraphQueryAsync(new GraphQLRequest(mutation), cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
        }

        /// <summary>
        /// Posts a request the LINQ integration translated. Internal: a request carries a
        /// variables payload, and building one by hand is not part of the public surface.
        /// </summary>
        /// <param name="request">The translated request.</param>
        /// <param name="endpoint">
        /// Where to post. A relative URI is resolved against the client's
        /// <see cref="HttpClient.BaseAddress"/> by <see cref="HttpClient"/> itself; null posts to
        /// the base address as-is.
        /// </param>
        /// <param name="cancellationToken">Cancels the request.</param>
        internal async ValueTask<HttpResponseMessage> SendGraphQLRequestAsync(GraphQLRequest request,
                Uri? endpoint = null,
                CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentException.ThrowIfNullOrEmpty(request.Query);

            cancellationToken.ThrowIfCancellationRequested();

            return await client.PostGraphQueryAsync(request, endpoint, cancellationToken)
                    .ConfigureAwait(false);
        }

        private async ValueTask<HttpResponseMessage> PostGraphQueryAsync(GraphQLRequest request,
                Uri? endpoint = null,
                CancellationToken cancellationToken = default)
        {
            var req = request.AsHttpPost();
            req.RequestUri = endpoint ?? client.BaseAddress;

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
