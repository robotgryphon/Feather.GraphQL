using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Feather.GraphQL.Linq.Execution;
using Feather.GraphQL.Linq.Expressions;

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

    /// <summary>What this chain was told about the schema. Shared by every queryable in it.</summary>
    public GraphQLQueryOptions Options { get; } = options;

    private GraphQLQueryPlan Plan(Expression expression)
        => new GraphQLQueryTranslator(Options).Translate(expression);

    /// <summary>
    /// Hands the transport the two things it reads. The rest of the plan stays this side of the
    /// seam, where the materializer needs it.
    /// </summary>
    private ValueTask<System.Text.Json.JsonElement> Run(
        GraphQLQueryPlan plan,
        CancellationToken cancellationToken)
        => Executor.ExecuteAsync(plan.Query, plan.Variables, cancellationToken);

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
}