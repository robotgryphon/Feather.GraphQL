using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Feather.GraphQL.Linq.Execution;
using Feather.GraphQL.Linq.Expressions;
using Feather.GraphQL.Linq.Filtering;

namespace Feather.GraphQL.Linq.Query;

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
internal sealed class GraphQLQueryProvider(IGraphQLQueryExecutor? executor, GraphQLQueryOptions options)
    : IQueryProvider
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
        var operation = Operation(plan);

        switch (plan.ResultOperator)
        {
            case QueryResultOperator.Count:
                return (TResult)(object)checked((int)await Executor
                    .ExecuteCountAsync(operation, cancellationToken).ConfigureAwait(false));

            case QueryResultOperator.LongCount:
                return (TResult)(object)await Executor
                    .ExecuteCountAsync(operation, cancellationToken).ConfigureAwait(false);

            case QueryResultOperator.Any:
                return (TResult)(object)await Runner.For(plan.ElementType)
                    .AnyAsync(Executor, operation, cancellationToken).ConfigureAwait(false);

            default:
                return ResultMaterializer.Reduce(plan, await Rows<TResult>(plan, operation, cancellationToken)
                    .ConfigureAwait(false));
        }
    }

    public IReadOnlyList<TElement> ExecuteSequence<TElement>(Expression expression)
        => Block(ExecuteSequenceAsync<TElement>(expression, CancellationToken.None));

    public async ValueTask<IReadOnlyList<TElement>> ExecuteSequenceAsync<TElement>(
        Expression expression,
        CancellationToken cancellationToken)
    {
        var plan = Plan(expression);

        return await Rows<TElement>(plan, Operation(plan), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Yields the chain's rows as the transport reads them.
    /// </summary>
    /// <remarks>
    /// What <c>AsAsyncEnumerable</c> is for. The buffered path is faster when the whole sequence
    /// is wanted, which is why every other terminal takes it; this one exists for the caller who
    /// would rather not hold the sequence at all, or who means to stop partway.
    /// </remarks>
    public IAsyncEnumerable<TElement> StreamAsync<TElement>(
        Expression expression,
        CancellationToken cancellationToken)
    {
        var plan = Plan(expression);

        return Runner.For(plan.ElementType).StreamAsync<TElement>(
            Executor, Operation(plan), ResultMaterializer.Shaper(plan), cancellationToken);
    }

    private ValueTask<IReadOnlyList<TOut>> Rows<TOut>(
        GraphQLQueryPlan plan,
        GraphQLOperation operation,
        CancellationToken cancellationToken)
        => Runner.For(plan.ElementType)
            .RowsAsync<TOut>(Executor, operation, ResultMaterializer.Shaper(plan), cancellationToken);

    /// <summary>The half of the plan a transport reads.</summary>
    private static GraphQLOperation Operation(GraphQLQueryPlan plan)
        => new(plan.Query, plan.Variables, plan.RootField, plan.Paging);

    /// <summary>What this chain was told about the schema. Shared by every queryable in it.</summary>
    public GraphQLQueryOptions Options { get; } = options;

    /// <summary>
    /// The document the compiler already printed for this chain, when it could.
    /// </summary>
    /// <remarks>
    /// Held on the provider rather than on the options, because one options instance can be
    /// shared by several queries — <c>For&lt;T&gt;(executor, options)</c> — while a provider is
    /// created once per chain, which is exactly the scope a precompiled document is valid for.
    /// </remarks>
    public string? PrecompiledDocument { get; set; }

    /// <summary>
    /// The whole plan, when the compiler could produce one.
    /// </summary>
    /// <remarks>
    /// Only for a chain that binds nothing: everything in a plan but the variables is a fact
    /// about the chain's shape, and a chain whose shape is fully known needs no walking. Held on
    /// the provider for the same reason the document is — a provider is created once per chain,
    /// which is exactly the scope a precompiled anything is valid for.
    /// </remarks>
    public GraphQLQueryPlan? PrecompiledPlan { get; set; }

    /// <summary>
    /// The plan for this chain: the compiler's when it printed one, and translated otherwise.
    /// </summary>
    /// <remarks>
    /// A precompiled plan that binds a filter is still missing its values, because those live in
    /// the expression tree the caller's own code rebuilt on this call. Reading them is all that is
    /// left to do — the shape was decided at build time. A predicate that does not come apart the
    /// way the compiler expected falls through to being lowered whole, so the two halves
    /// disagreeing costs speed rather than correctness.
    /// </remarks>
    private GraphQLQueryPlan Plan(Expression expression)
    {
        if (PrecompiledPlan is not { } precompiled)
            return new GraphQLQueryTranslator(Options).Translate(expression, PrecompiledDocument);

        if (precompiled.Filter is not { } filter)
            return precompiled;

        if (QueryChain.Parse(expression).MergedPredicate() is { } predicate
            && FilterHoles.Collect(predicate, precompiled.FilterHoles) is { } values)
        {
            return precompiled with { Variables = filter(values) };
        }

        return new GraphQLQueryTranslator(Options).Translate(expression, PrecompiledDocument);
    }

    private IGraphQLQueryExecutor Executor
        => executor ?? throw new GraphQLTranslationException("FGQL016",
            "This queryable was created without an executor and can only be translated. Call "
            + "ToGraphQLQuery() to read the document, or pass an IGraphQLQueryExecutor — "
            + "CreateQueryable() over an HttpClient supplies one — for a queryable that runs.");

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

    /// <summary>
    /// Calls the transport with the queried element type, which is only a <see cref="Type"/> here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The terminal's own generic parameter is the <em>result</em> type, and a projection makes
    /// that something else entirely — an anonymous type the rows were never deserialized as. So
    /// the queried type has to be closed over at runtime, and this is the cheap way to do it: one
    /// object per element type, constructed once and cached, with a generic method the compiler
    /// resolves statically at each call site. No method is built by reflection.
    /// </para>
    /// <para>
    /// The projection is applied on this side rather than pushed across the seam, because a
    /// transport has no business knowing a query had a <c>Select</c>.
    /// </para>
    /// </remarks>
    private abstract class Runner
    {
        private static readonly ConcurrentDictionary<Type, Runner> _cache = new();

        public static Runner For(Type queried)
            => _cache.GetOrAdd(queried, static type =>
                (Runner)Activator.CreateInstance(typeof(Runner<>).MakeGenericType(type))!);

        public abstract ValueTask<IReadOnlyList<TOut>> RowsAsync<TOut>(
            IGraphQLQueryExecutor executor,
            GraphQLOperation operation,
            Func<object?, object?>? shaper,
            CancellationToken cancellationToken);

        public abstract IAsyncEnumerable<TOut> StreamAsync<TOut>(
            IGraphQLQueryExecutor executor,
            GraphQLOperation operation,
            Func<object?, object?>? shaper,
            CancellationToken cancellationToken);

        public abstract ValueTask<bool> AnyAsync(
            IGraphQLQueryExecutor executor,
            GraphQLOperation operation,
            CancellationToken cancellationToken);
    }

    private sealed class Runner<TElement> : Runner
    {
        public override async ValueTask<IReadOnlyList<TOut>> RowsAsync<TOut>(
            IGraphQLQueryExecutor executor,
            GraphQLOperation operation,
            Func<object?, object?>? shaper,
            CancellationToken cancellationToken)
        {
            var rows = await executor.ExecuteAsync<TElement>(operation, cancellationToken)
                .ConfigureAwait(false);

            // No projection: the queried type is the result type, and the list the transport
            // already built is the answer.
            if (shaper is null)
                return (IReadOnlyList<TOut>)(object)rows;

            var shaped = new TOut[rows.Count];

            for (int i = 0; i < rows.Count; i++)
                shaped[i] = (TOut)shaper(rows[i])!;

            return shaped;
        }

        public override async IAsyncEnumerable<TOut> StreamAsync<TOut>(
            IGraphQLQueryExecutor executor,
            GraphQLOperation operation,
            Func<object?, object?>? shaper,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var rows = executor.StreamAsync<TElement>(operation, cancellationToken);

            await foreach (var row in rows.WithCancellation(cancellationToken).ConfigureAwait(false))
                yield return shaper is null ? (TOut)(object)row! : (TOut)shaper(row)!;
        }

        public override async ValueTask<bool> AnyAsync(
            IGraphQLQueryExecutor executor,
            GraphQLOperation operation,
            CancellationToken cancellationToken)
        {
            // The translation already asked for a page of one, so this reads a row or nothing.
            var rows = await executor.ExecuteAsync<TElement>(operation, cancellationToken)
                .ConfigureAwait(false);

            return rows.Count > 0;
        }
    }
}
