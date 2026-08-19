using Feather.GraphQL.Http;
using Feather.GraphQL.Linq.Filtering;
using Feather.GraphQL.Request;
using JetBrains.Annotations;

namespace Feather.GraphQL.Linq.Query;

/// <summary>The v1 terminals: translate a chain to a request, and send it.</summary>
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
        {
            ArgumentNullException.ThrowIfNull(source);

            return new GraphQLQueryTranslator(provider ?? HotChocolateFilterProvider.Instance)
                .Translate(source.Expression);
        }

        /// <summary>
        /// Translates the chain and posts it. The response is returned untouched: reading it is
        /// the caller's business, with <c>ReadAsGraphQLAsync&lt;T&gt;()</c> or otherwise.
        /// </summary>
        public Task<HttpResponseMessage> SendGraphQLAsync(
            HttpClient client,
            IFilterTranslationProvider? provider = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(client);

            var request = source.ToGraphQLRequest(provider);
            return client.SendGraphQLQueryAsync(request, cancellationToken).AsTask();
        }
    }
}
