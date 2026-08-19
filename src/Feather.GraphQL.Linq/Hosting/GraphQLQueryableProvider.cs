using Feather.GraphQL.Http;
using Feather.GraphQL.Linq.Filtering;
using Feather.GraphQL.Linq.Query;
using Feather.GraphQL.Request;

namespace Feather.GraphQL.Linq.Hosting;

internal sealed class GraphQLQueryableProvider<TSchema>(
    HttpClient client,
    IFilterTranslationProvider filterProvider) : IGraphQLQueryableProvider<TSchema>
{
    public IQueryable<T> Queryable<T>() => GraphQLQueryable.For<T>();

    public Task<HttpResponseMessage> SendAsync<T>(IQueryable<T> query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return SendAsync(query.ToGraphQLRequest(filterProvider), cancellationToken);
    }

    public Task<HttpResponseMessage> SendAsync(GraphQLRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return client.SendGraphQLQueryAsync(request, cancellationToken).AsTask();
    }
}
