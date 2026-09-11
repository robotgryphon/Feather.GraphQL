using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Feather.GraphQL.Http.Request;
using Feather.GraphQL.Http.Response;

namespace Feather.GraphQL.Http;

public static class HttpExtensions
{
    private static readonly JsonSerializerOptions SERIALIZER_OPTS = new JsonSerializerOptions()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true
    };

    extension<T>(T request)
    {
        private HttpRequestMessage AsHttpPost()
        {
            var message = new HttpRequestMessage { Method = HttpMethod.Post, Content = request.AsHttpMessageContent() };
            message.AddGraphQLRequestHeaders();
            return message;
        }

        private StringContent AsHttpMessageContent()
        {
            string body = JsonSerializer.Serialize(request, SERIALIZER_OPTS);

            var content = new StringContent(body, Encoding.UTF8, "application/json");

            // Explicitly setting content header to avoid issues with some GraphQL servers
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            return content;
        }
    }

    extension(HttpRequestMessage message)
    {
        private void AddGraphQLRequestHeaders()
        {
            foreach (string contentType in GraphQLHttpConstants.RESPONSE_CONTENT_TYPES)
                message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(contentType));

            message.Headers.AcceptCharset.Add(new StringWithQualityHeaderValue("utf-8"));

            var a = typeof(HttpExtensions).Assembly;
            message.Headers.UserAgent.Add(new ProductInfoHeaderValue(a.GetName().Name!, a.GetName().Version!.ToString()));
        }
    }

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

    extension(HttpResponseMessage response)
    {
        /// <summary>
        /// Reads the reply's <c>data</c> into <typeparamref name="TData"/>.
        /// </summary>
        /// <typeparam name="TData">
        /// The shape of the <c>data</c> field — not of the whole reply. A query for
        /// <c>person</c> deserializes into a type with a <c>Person</c> member, not into
        /// <c>Person</c> itself.
        /// </typeparam>
        /// <param name="cancellationToken">Cancels the read.</param>
        /// <returns>The <c>data</c> field, deserialized.</returns>
        /// <exception cref="GraphQLHttpException">
        /// The reply carried an <c>errors</c> array, or carried no <c>data</c> at all. The
        /// exception holds the parsed errors and this response, still undisposed. Catch its base
        /// <see cref="GraphQLException"/> to handle a failed query without naming the transport.
        /// </exception>
        /// <remarks>
        /// Errors are checked before the status code, because a GraphQL server routinely reports
        /// them with <c>200 OK</c> — and when it does, its own message is far more useful than
        /// the status.
        /// </remarks>
        public async ValueTask<TData> ReadGraphQLAsync<TData>(
                CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(response);

            var body = await response.Content
                    .ReadFromJsonAsync<GraphQLResponseBody>(cancellationToken)
                    .ConfigureAwait(false);

            if (body?.Errors is { Length: > 0 } errors)
                throw new GraphQLHttpException(errors, response);

            response.EnsureSuccessStatusCode();

            if (body is not { HasData: true })
                throw new GraphQLHttpException(errors: null, response);

            // The transport asks for the element itself; handing it back saves a round trip
            // through the serializer for the one caller that wants the raw tree.
            if (typeof(TData) == typeof(JsonElement))
                return (TData)(object)body.Data;

            return body.Data.Deserialize<TData>(JsonSerializerOptions.Web)
                ?? throw new GraphQLHttpException(errors: null, response);
        }
    }
}
