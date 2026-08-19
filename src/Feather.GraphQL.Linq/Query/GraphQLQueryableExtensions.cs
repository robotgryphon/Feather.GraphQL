using System.Text.Json;
using Feather.GraphQL.Http;
using Feather.GraphQL.Linq.Filtering;
using Feather.GraphQL.Linq.Materialization;
using Feather.GraphQL.Request;
using JetBrains.Annotations;

namespace Feather.GraphQL.Linq.Query;

/// <summary>The terminals: translate a chain to a request, send it, and read the result back.</summary>
[PublicAPI]
public static class GraphQLQueryableExtensions
{
    extension<T>(IQueryable<T> source)
    {
        /// <summary>
        /// Translates the chain into a <see cref="GraphQLRequest"/>. Pure — no client, no I/O,
        /// and the unit most worth testing.
        /// </summary>
        public GraphQLRequest ToGraphQLRequest(IFilterTranslationProvider? provider = null)
            => Translate(source, provider).Request;

        /// <summary>
        /// Translates the chain and posts it. The response is returned untouched: reading it is
        /// the caller's business, with <c>ReadAsGraphQLAsync&lt;T&gt;()</c> or otherwise.
        /// </summary>
        public Task<HttpResponseMessage> SendGraphQLAsync(
            HttpClient client,
            IFilterTranslationProvider? provider = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(client);

            var request = source.ToGraphQLRequest(provider);
            return client.SendGraphQLQueryAsync(request, cancellationToken).AsTask();
        }

        /// <summary>
        /// Translates the chain, posts it, and materializes the result.
        /// </summary>
        /// <remarks>
        /// This is the terminal to reach for when handing results to something that expects data
        /// rather than a query — a Blazor component taking <c>IQueryable&lt;T&gt;</c> and calling
        /// <c>ToArray()</c>, say. Await this, then pass <c>result.AsQueryable()</c>: the I/O has
        /// to finish before the synchronous enumeration starts, and there is nowhere else for it
        /// to finish. See <see cref="GraphQLQueryable"/> on why enumerating directly cannot work.
        /// </remarks>
        /// <exception cref="GraphQLResponseException">The response carried GraphQL errors.</exception>
        /// <exception cref="GraphQLTranslationException">
        /// The chain has no GraphQL translation, or the response does not match it (FGQL015).
        /// </exception>
        public async Task<T[]> ToArrayAsync(
            HttpClient client,
            IFilterTranslationProvider? provider = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(client);

            var query = Translate(source, provider);

            using var message = await client.SendGraphQLQueryAsync(query.Request, cancellationToken)
                .ConfigureAwait(false);

            return await MaterializeAsync<T>(message, query, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Translates the chain, posts it, and materializes the result as a list.
        /// </summary>
        /// <remarks>See <c>ToArrayAsync</c>; this is the same terminal in the shape most callers want.</remarks>
        /// <exception cref="GraphQLResponseException">The response carried GraphQL errors.</exception>
        /// <exception cref="GraphQLTranslationException">
        /// The chain has no GraphQL translation, or the response does not match it (FGQL015).
        /// </exception>
        public async Task<List<T>> ToListAsync(
            HttpClient client,
            IFilterTranslationProvider? provider = null,
            CancellationToken cancellationToken = default)
            => [.. await source.ToArrayAsync(client, provider, cancellationToken).ConfigureAwait(false)];
    }

    internal static TranslatedQuery Translate<T>(IQueryable<T> source, IFilterTranslationProvider? provider)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new GraphQLQueryTranslator(provider ?? HotChocolateFilterProvider.Instance)
            .Translate(source.Expression);
    }

    /// <summary>
    /// Reads one response into elements.
    /// </summary>
    /// <remarks>
    /// GraphQL errors are checked before the status code: a server that reports a bad query as
    /// 400 still describes what was wrong in the body, and <see cref="GraphQLResponseException"/>
    /// carries that where <see cref="HttpRequestException"/> would drop it. A failure with no
    /// GraphQL body left to read stays an HTTP failure.
    /// </remarks>
    internal static async Task<T[]> MaterializeAsync<T>(
        HttpResponseMessage message,
        TranslatedQuery query,
        CancellationToken cancellationToken)
    {
        if (!message.IsSuccessStatusCode && !IsGraphQLContent(message))
            message.EnsureSuccessStatusCode();

        var response = await message.AsDataResponse<JsonElement>(cancellationToken).ConfigureAwait(false);

        if (response.Errors is { Length: > 0 })
            throw new GraphQLResponseException(response);

        message.EnsureSuccessStatusCode();

        return ResultMaterializer.Materialize<T>(query, response.Data);
    }

    private static bool IsGraphQLContent(HttpResponseMessage message)
        => message.Content.Headers.ContentType?.MediaType is { } mediaType
            && GraphQLHttpConstants.RESPONSE_CONTENT_TYPES
                .Any(type => string.Equals(type, mediaType, StringComparison.OrdinalIgnoreCase));
}
