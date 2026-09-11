using Feather.GraphQL.Linq.Query;
using JetBrains.Annotations;
using Microsoft.Extensions.Options;

namespace Feather.GraphQL.Linq.Providers;

/// <summary>
/// Turns an <see cref="HttpClient"/> into executable queryables.
/// </summary>
/// <remarks>
/// The whole of the HTTP transport's public surface, and one call deep. There is no registration
/// step and no container to configure: wherever a client comes from — <c>new</c>, a typed client,
/// an <c>IHttpClientFactory</c> — it becomes a query here, and how the client reaches the code
/// using it is the app's business rather than this library's.
/// </remarks>
[PublicAPI]
public static class HttpClientGraphQLQueryableExtensions
{
    extension(HttpClient client)
    {
        /// <summary>
        /// Starts a query over <typeparamref name="T"/>, queried through
        /// <paramref name="rootField"/> and posted to this client.
        /// </summary>
        /// <typeparam name="T">The type the field returns.</typeparam>
        /// <param name="rootField">The field on the schema's <c>Query</c> type.</param>
        /// <param name="configure">
        /// Everything else the schema requires: the endpoint path, the filter and sort input
        /// names, how it pages, the filter dialect.
        /// </param>
        /// <remarks>
        /// Headers, auth and retry are configured on the client, the way they would be for any
        /// other use of it.
        /// </remarks>
        /// <example>
        /// <code>
        /// client.CreateQueryable&lt;Country&gt;("countries", o => o.EndpointPath = "api/graphql");
        /// </code>
        /// </example>
        public IQueryable<T> CreateQueryable<T>(
            string rootField,
            Action<GraphQLHttpQueryOptions>? configure = null)
        {
            var options = new GraphQLHttpQueryOptions();
            configure?.Invoke(options);
            options.RootField = rootField;

            return client.CreateQueryable<T>(options);
        }

        /// <summary>
        /// Starts a query from options built elsewhere — shared across calls, or read from a
        /// container.
        /// </summary>
        /// <remarks>
        /// The options are used as given, <see cref="GraphQLQueryOptions.RootField"/> included.
        /// Sharing one instance across query types means sharing its root field too, so set that
        /// per query unless every query really does read the same field.
        /// </remarks>
        public IQueryable<T> CreateQueryable<T>(GraphQLHttpQueryOptions options)
        {
            ArgumentNullException.ThrowIfNull(client);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentException.ThrowIfNullOrWhiteSpace(options.RootField);

            return GraphQLQueryable.For<T>(
                new HttpGraphQLQueryExecutor(client, options.EndpointPath), options);
        }

        /// <summary>
        /// Starts a query over <typeparamref name="T"/> using options an app configured with
        /// <c>services.Configure&lt;GraphQLHttpQueryOptions&gt;(…)</c>.
        /// </summary>
        /// <remarks>
        /// The root field is this call's, so one configured options object serves every query
        /// type against the endpoint.
        /// </remarks>
        public IQueryable<T> CreateQueryable<T>(
            string rootField,
            IOptions<GraphQLHttpQueryOptions> options)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentException.ThrowIfNullOrWhiteSpace(rootField);

            var value = options.Value;

            // A copy, because the container's instance is shared and the root field is not.
            var scoped = new GraphQLHttpQueryOptions
            {
                RootField = rootField,
                EndpointPath = value.EndpointPath,
                FilterInput = value.FilterInput,
                SortInput = value.SortInput,
                Paging = value.Paging,
                FilterProvider = value.FilterProvider
            };

            return client.CreateQueryable<T>(scoped);
        }
    }
}
