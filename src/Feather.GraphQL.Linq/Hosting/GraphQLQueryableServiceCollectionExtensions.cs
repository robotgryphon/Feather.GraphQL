using Feather.GraphQL.Linq.Filtering;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;

namespace Feather.GraphQL.Linq.Hosting;

[PublicAPI]
public static class GraphQLQueryableServiceCollectionExtensions
{
    /// <summary>
    /// Registers a GraphQL endpoint under the <typeparamref name="TSchema"/> marker.
    /// </summary>
    /// <returns>
    /// The <see cref="IHttpClientBuilder"/> for the underlying typed client, so handlers compose
    /// the standard way. That is also the answer to per-request headers now that the client is
    /// hidden: a <c>DelegatingHandler</c>, not a leaked <see cref="HttpClient"/>.
    /// </returns>
    public static IHttpClientBuilder AddGraphQLQueryable<TSchema>(
        this IServiceCollection services,
        Uri endpoint,
        IFilterTranslationProvider? filterProvider = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(endpoint);

        services.AddSingleton(filterProvider ?? HotChocolateFilterProvider.Instance);

        return services
            .AddHttpClient<IGraphQLQueryableProvider<TSchema>, GraphQLQueryableProvider<TSchema>>(
                client => client.BaseAddress = endpoint);
    }
}
