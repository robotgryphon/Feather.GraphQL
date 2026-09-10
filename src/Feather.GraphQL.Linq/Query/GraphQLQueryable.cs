using System.Collections;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Feather.GraphQL.Linq.Execution;
using Feather.GraphQL.Linq.Expressions;
using JetBrains.Annotations;

namespace Feather.GraphQL.Linq.Query;

/// <summary>
/// Entry point for composing a GraphQL query with LINQ, without dependency injection.
/// </summary>
/// <remarks>
/// A queryable from here has no executor, so it can be translated but not run —
/// <see cref="GraphQLQueryableExtensions.ToGraphQLQuery"/> and
/// <see cref="GraphQLQueryableExtensions.ToQueryPlan"/> are its terminals. Inject
/// <see cref="IGraphQLQueryableSource"/> for one that executes.
/// </remarks>
[PublicAPI]
public static class GraphQLQueryable
{
    /// <summary>Starts a translate-only query over a type carrying <c>[GenerateQueryable]</c>.</summary>
    public static IQueryable<T> For<T>() => new GraphQLQueryable<T>(new GraphQLQueryProvider(executor: null));
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

/// <summary>
/// Captures composition, then executes it against the endpoint the queryable was created from.
/// </summary>
/// <remarks>
/// Sync terminals block on the async path, as EF Core's do. That is a deliberate trade: an
/// <see cref="IQueryable"/> has no async contract, and refusing to answer <c>ToList()</c> would
/// reintroduce the runtime surprise this provider exists to remove. The async terminals in
/// <see cref="GraphQLAsyncQueryableExtensions"/> are there for callers who would rather not
/// block a thread on I/O.
/// </remarks>
internal sealed class GraphQLQueryProvider(IGraphQLQueryExecutor? executor) : IQueryProvider
{
    public IQueryable CreateQuery(Expression expression)
    {
        var elementType = expression.Type.GetInterfaces().Append(expression.Type)
            .First(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IQueryable<>))
            .GetGenericArguments()[0];

        return (IQueryable)Activator.CreateInstance(
            typeof(GraphQLQueryable<>).MakeGenericType(elementType), this, expression)!;
    }

    public IQueryable<TElement> CreateQuery<TElement>(Expression expression)
        => new GraphQLQueryable<TElement>(this, expression);

    public object Execute(Expression expression) => Execute<object>(expression);

    public TResult Execute<TResult>(Expression expression)
        => Block(ExecuteAsync<TResult>(expression, CancellationToken.None));

    /// <summary>Runs a chain whose terminal is a result operator, such as <c>First</c>.</summary>
    public async ValueTask<TResult> ExecuteAsync<TResult>(
        Expression expression,
        CancellationToken cancellationToken)
    {
        var plan = Plan(expression);
        var data = await Run(plan, cancellationToken).ConfigureAwait(false);

        return plan.ResultOperator switch
        {
            QueryResultOperator.Count => (TResult)(object)checked((int)ResultMaterializer.Count(plan, data)),
            QueryResultOperator.LongCount => (TResult)(object)ResultMaterializer.Count(plan, data),
            QueryResultOperator.Any => (TResult)(object)ResultMaterializer.Any(plan, data),
            _ => ResultMaterializer.Reduce(plan, ResultMaterializer.Rows<TResult>(plan, data))
        };
    }

    public IReadOnlyList<TElement> ExecuteSequence<TElement>(Expression expression)
        => Block(ExecuteSequenceAsync<TElement>(expression, CancellationToken.None));

    public async ValueTask<IReadOnlyList<TElement>> ExecuteSequenceAsync<TElement>(
        Expression expression,
        CancellationToken cancellationToken)
    {
        var plan = Plan(expression);
        var data = await Run(plan, cancellationToken).ConfigureAwait(false);

        return ResultMaterializer.Rows<TElement>(plan, data);
    }

    private GraphQLQueryPlan Plan(Expression expression)
        => new GraphQLQueryTranslator(Executor.FilterProvider).Translate(expression);

    private ValueTask<System.Text.Json.JsonElement> Run(
        GraphQLQueryPlan plan,
        CancellationToken cancellationToken)
        => Executor.ExecuteAsync(plan, cancellationToken);

    private IGraphQLQueryExecutor Executor
        => executor ?? throw new GraphQLTranslationException("FGQL016",
            "This queryable came from GraphQLQueryable.For<T>(), which has no executor and can "
            + "only be translated. Call ToGraphQLQuery() to read the document, or inject "
            + "IGraphQLQueryableSource for a queryable that executes.");

    /// <summary>
    /// The sync-over-async bridge. Isolated in one method so there is exactly one place to look
    /// when a sync terminal misbehaves.
    /// </summary>
    private static TResult Block<TResult>(ValueTask<TResult> task)
        => task.IsCompletedSuccessfully
            ? task.Result
            : ConfiguredBlock(task);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static TResult ConfiguredBlock<TResult>(ValueTask<TResult> task)
        => task.AsTask().ConfigureAwait(false).GetAwaiter().GetResult();
}
