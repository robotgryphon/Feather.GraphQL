using Feather.GraphQL.Linq.Filtering;
using Feather.GraphQL.Linq.Query;
using JetBrains.Annotations;

namespace Feather.GraphQL.Linq.Providers;

/// <summary>
/// Turns an <see cref="HttpClient"/> into executable queryables — either one at a time with
/// <c>CreateQueryable&lt;T&gt;</c>, or through a reusable source.
/// </summary>
/// <remarks>
/// The whole of the HTTP transport's public surface. There is no registration step and no
/// container to configure: wherever a client comes from — <c>new</c>, a typed client, an
/// <c>IHttpClientFactory</c> — it becomes a source here, and how that source reaches the code
/// using it is the app's business rather than this library's.
/// </remarks>
[PublicAPI]
public static class HttpClientGraphQLQueryableExtensions
{
    extension(HttpClient client)
    {
        /// <summary>
        /// Creates a source that posts to the client's <see cref="HttpClient.BaseAddress"/>.
        /// </summary>
        /// <remarks>
        /// Headers, auth and retry are configured on the client, the way they would be for any
        /// other use of it. For a server that serves GraphQL from a path below the base address,
        /// use the overload that takes one.
        /// </remarks>
        public IGraphQLQueryableSource CreateQueryable()
            => client.CreateQueryable(null, HotChocolateFilterProvider.Instance);

        /// <summary>
        /// Creates a source that posts to <paramref name="endpointPath"/> on the client's
        /// <see cref="HttpClient.BaseAddress"/>.
        /// </summary>
        /// <param name="endpointPath">
        /// The path GraphQL is served from — <c>"api/graphql"</c>, say. Null or empty posts to
        /// the base address unchanged, for a client already pointed straight at the endpoint.
        /// An absolute URI is used as-is and ignores the base address.
        /// </param>
        /// <param name="filterProvider">The dialect predicates are lowered to.</param>
        /// <remarks>
        /// Resolution is <see cref="HttpClient"/>'s own, which means a base address is treated as
        /// a document rather than a directory: <c>https://host/v1</c> plus <c>graphql</c> is
        /// <c>https://host/graphql</c>, not <c>https://host/v1/graphql</c>. Give the base address
        /// a trailing slash to keep its path.
        /// </remarks>
        public IGraphQLQueryableSource CreateQueryable(
            string? endpointPath,
            IFilterTranslationProvider? filterProvider = null)
        {
            ArgumentNullException.ThrowIfNull(client);

            return new GraphQLQueryableSource(
                new HttpGraphQLQueryExecutor(client, endpointPath, filterProvider));
        }

        /// <summary>
        /// Starts a query over <typeparamref name="T"/> against the client's
        /// <see cref="HttpClient.BaseAddress"/>, without naming a source.
        /// </summary>
        /// <typeparam name="T">A type carrying <c>[GenerateQueryable]</c>.</typeparam>
        /// <remarks>
        /// The short way in: a client becomes a query in one step. Each call builds its own
        /// source, so hold one from <c>AsGraphQLQueryableSource</c> instead when several query
        /// types share an endpoint and the executor is worth building once.
        /// </remarks>
        public IQueryable<T> CreateQueryable<T>()
            => client.CreateQueryable<T>(null, HotChocolateFilterProvider.Instance);

        /// <summary>
        /// Starts a query over <typeparamref name="T"/> against <paramref name="endpointPath"/>
        /// on the client's <see cref="HttpClient.BaseAddress"/>.
        /// </summary>
        /// <typeparam name="T">A type carrying <c>[GenerateQueryable]</c>.</typeparam>
        /// <param name="endpointPath">
        /// The path GraphQL is served from. Null or empty posts to the base address unchanged;
        /// an absolute URI is used as-is. Resolution is <see cref="HttpClient"/>'s own — see
        /// <see cref="HttpGraphQLQueryExecutor"/>.
        /// </param>
        /// <param name="filterProvider">The dialect predicates are lowered to.</param>
        public IQueryable<T> CreateQueryable<T>(
            string? endpointPath,
            IFilterTranslationProvider? filterProvider = null)
            => client.CreateQueryable(endpointPath, filterProvider).Queryable<T>();
    }
}
