using System.Collections;
using System.Linq.Expressions;
using JetBrains.Annotations;

namespace Feather.GraphQL.Linq.Query;

/// <summary>
/// Where a GraphQL query starts.
/// </summary>
/// <remarks>
/// One form, because there is only one thing a chain is for. It names the field and the shape the
/// compiler reads; the transport comes from the method the chain is the body of, and the chain
/// itself never runs.
/// </remarks>
[PublicAPI]
public static class GraphQLQueryable
{
    /// <summary>
    /// Starts a query over a root field.
    /// </summary>
    /// <remarks>
    /// No transport, because a chain never runs one. The compiler reads the root field from this
    /// call and the client from the method the chain is the body of, so what a chain needs at run
    /// time is decided before run time — and taking an executor here would be asking for
    /// something nothing would ever use.
    /// </remarks>
    /// <param name="rootField">The field on the schema's <c>Query</c> type.</param>
    /// <param name="configure">Anything else the schema requires.</param>
    public static IQueryable<T> For<T>(string rootField, Action<GraphQLQueryOptions>? configure = null)
        => Create<T>(Configured(rootField, configure));

    /// <inheritdoc cref="For{T}(string, Action{GraphQLQueryOptions})"/>
    /// <param name="options">Used as given, including its root field; one with none is FGQL011.</param>
    public static IQueryable<T> For<T>(GraphQLQueryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return Create<T>(options);
    }

    private static IQueryable<T> Create<T>(GraphQLQueryOptions options)
        => new GraphQLQueryable<T>(new GraphQLQueryProvider(options));

    private static GraphQLQueryOptions Configured(string rootField, Action<GraphQLQueryOptions>? configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootField);

        var options = new GraphQLQueryOptions { RootField = rootField };
        configure?.Invoke(options);

        return options;
    }
}

/// <inheritdoc cref="GraphQLQueryable"/>
internal sealed class GraphQLQueryable<T> : IQueryable<T>, IOrderedQueryable<T>
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
}
