using Feather.GraphQL.Linq.Execution;
using JetBrains.Annotations;

namespace Feather.GraphQL.Linq.Query;

/// <summary>
/// A source over one executor: every queryable it hands out runs through that transport.
/// </summary>
/// <remarks>
/// Transport-agnostic by construction. A transport package builds one of these around its own
/// <see cref="IGraphQLQueryExecutor"/> and registers it; there is nothing to subclass.
/// </remarks>
[PublicAPI]
public sealed class GraphQLQueryableSource : IGraphQLQueryableSource
{
    private readonly GraphQLQueryProvider _provider;

    public GraphQLQueryableSource(IGraphQLQueryExecutor executor)
    {
        ArgumentNullException.ThrowIfNull(executor);
        _provider = new GraphQLQueryProvider(executor);
    }

    public IQueryable<T> Queryable<T>() => new GraphQLQueryable<T>(_provider);
}
