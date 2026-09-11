using System.Collections;
using System.Linq.Expressions;
using Feather.GraphQL.Linq.Execution;
using JetBrains.Annotations;

namespace Feather.GraphQL.Linq.Query;

/// <summary>
/// Where a GraphQL query starts.
/// </summary>
/// <remarks>
/// Two forms, differing only in whether the query can run. Without an executor it translates and
/// nothing more — <see cref="GraphQLQueryableExtensions.ToGraphQLQuery"/> is its terminal, and
/// anything that would execute is <c>FGQL016</c>. With one, every LINQ terminal works.
/// </remarks>
[PublicAPI]
public static class GraphQLQueryable
{
    /// <summary>
    /// Starts a translate-only query over <typeparamref name="T"/>, queried through
    /// <paramref name="rootField"/>.
    /// </summary>
    /// <param name="rootField">The field on the schema's <c>Query</c> type.</param>
    /// <param name="configure">
    /// Anything else the schema requires: the filter and sort input names, how it pages, the
    /// filter dialect.
    /// </param>
    public static IQueryable<T> For<T>(
        string rootField,
        Action<GraphQLQueryOptions>? configure = null)
        => Create<T>(executor: null, Configured(rootField, configure));

    /// <summary>
    /// Starts a query that runs through <paramref name="executor"/>.
    /// </summary>
    /// <param name="executor">
    /// The transport. <c>Feather.GraphQL.Linq.Providers.HttpClient</c> supplies one over
    /// <c>HttpClient</c>; anything else — a websocket, an in-process schema, a recorded fixture —
    /// is one class.
    /// </param>
    /// <param name="rootField">The field on the schema's <c>Query</c> type.</param>
    /// <param name="configure">Anything else the schema requires.</param>
    public static IQueryable<T> For<T>(
        IGraphQLQueryExecutor executor,
        string rootField,
        Action<GraphQLQueryOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(executor);

        return Create<T>(executor, Configured(rootField, configure));
    }

    /// <summary>
    /// Starts a query from options built elsewhere — shared across calls, or read from a
    /// container.
    /// </summary>
    /// <remarks>
    /// <paramref name="options"/> is used as given, including its
    /// <see cref="GraphQLQueryOptions.RootField"/>; a query with none is <c>FGQL011</c>.
    /// </remarks>
    public static IQueryable<T> For<T>(IGraphQLQueryExecutor executor, GraphQLQueryOptions options)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(options);

        return Create<T>(executor, options);
    }

    private static IQueryable<T> Create<T>(IGraphQLQueryExecutor? executor, GraphQLQueryOptions options)
        => new GraphQLQueryable<T>(new GraphQLQueryProvider(executor, options));

    private static GraphQLQueryOptions Configured(string rootField, Action<GraphQLQueryOptions>? configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootField);

        var options = new GraphQLQueryOptions { RootField = rootField };
        configure?.Invoke(options);

        return options;
    }
}

/// <inheritdoc cref="GraphQLQueryable"/>
internal sealed class GraphQLQueryable<T> : IQueryable<T>, IOrderedQueryable<T>, IAsyncEnumerable<T>
{
    private readonly GraphQLQueryProvider _provider;

    public Type ElementType => typeof(T);
    public Expression Expression { get; }
    public IQueryProvider Provider => _provider;

    public GraphQLQueryable(GraphQLQueryProvider provider)
    {
        _provider = provider;
        Expression = Expression.Constant(this);
    }

    internal GraphQLQueryable(GraphQLQueryProvider provider, Expression expression)
    {
        _provider = provider;
        Expression = expression;
    }

    /// <summary>
    /// Enumerating executes, which is the whole point: <c>ToList</c>, <c>ToArray</c> and
    /// <c>foreach</c> are ordinary LINQ terminals here, not traps that throw.
    /// </summary>
    public IEnumerator<T> GetEnumerator() => _provider.ExecuteSequence<T>(Expression).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public async IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        var rows = await _provider.ExecuteSequenceAsync<T>(Expression, cancellationToken)
            .ConfigureAwait(false);

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return row;
        }
    }
}
