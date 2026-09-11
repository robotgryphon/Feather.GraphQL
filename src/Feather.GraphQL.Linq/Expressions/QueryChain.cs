using System.Linq.Expressions;
using System.Reflection;
using Feather.GraphQL.Linq.Filtering;

namespace Feather.GraphQL.Linq.Expressions;

/// <summary>
/// A LINQ method chain decomposed into the parts GraphQL can carry.
/// </summary>
/// <remarks>
/// Chains are walked from the terminal call back to the source, so the lists come out in
/// source order. Anything that is not a recognised operator throws <c>FGQL001</c> rather than
/// being dropped — silently ignoring a <c>Take</c> in a chain the caller believed was fully
/// translated is the failure mode this design exists to prevent.
/// </remarks>
internal sealed class QueryChain
{
    public required Type ElementType { get; init; }
    public List<LambdaExpression> Predicates { get; } = [];
    public List<(LambdaExpression Key, bool Descending)> Ordering { get; } = [];
    public LambdaExpression? Projection { get; set; }
    public int? Skip { get; set; }
    public int? Take { get; set; }

    /// <summary>Cursor paging's backwards read, set only by <c>Last</c>.</summary>
    public int? TakeLast { get; set; }

    public QueryResultOperator ResultOperator { get; set; } = QueryResultOperator.Sequence;

    /// <summary>What this schema calls its arguments; HotChocolate's names until told otherwise.</summary>
    public GraphQLArgumentNames Arguments { get; set; } = GraphQLArgumentNames.Default;

    public bool HasFilter => Predicates.Count > 0;
    public bool HasOrdering => Ordering.Count > 0;
    public bool HasPaging => Skip.HasValue || Take.HasValue || TakeLast.HasValue;

    /// <summary>True when the result is one element (or its absence) rather than a sequence.</summary>
    public bool IsScalarResult => ResultOperator is not QueryResultOperator.Sequence;

    /// <summary>True when the chain asks for a count rather than for rows.</summary>
    public bool IsCount => ResultOperator is QueryResultOperator.Count or QueryResultOperator.LongCount;

    public static QueryChain Parse(Expression expression)
    {
        var calls = new Stack<MethodCallExpression>();

        var current = expression;
        while (current is MethodCallExpression call)
        {
            if (call.Method.DeclaringType != typeof(Queryable)
                && call.Method.DeclaringType != typeof(Enumerable)
                && call.Method.DeclaringType != typeof(GraphQLArgumentExtensions)
                && call.Method.DeclaringType != typeof(GraphQLWhereExtensions))
                throw GraphQLTranslationException.UnsupportedOperator(call.Method.Name, call);

            calls.Push(call);
            current = call.Arguments[0];
        }

        // The element type comes from the chain's *source*, not its tip: after a Select the tip is
        // the projected (often anonymous) type, while the fields being selected still belong to
        // the queried element.
        var chain = new QueryChain { ElementType = FindElementType(current) };

        while (calls.Count > 0)
            chain.Apply(calls.Pop());

        return chain;
    }

    private void Apply(MethodCallExpression call)
    {
        switch (call.Method.Name)
        {
            case nameof(Queryable.Where):
                Predicates.Add(UnwrapLambda(call.Arguments[1]));
                break;

            case nameof(Queryable.OrderBy):
            case nameof(Queryable.ThenBy):
                Ordering.Add((UnwrapLambda(call.Arguments[1]), false));
                break;

            case nameof(Queryable.OrderByDescending):
            case nameof(Queryable.ThenByDescending):
                Ordering.Add((UnwrapLambda(call.Arguments[1]), true));
                break;

            case nameof(Queryable.Select):
                Projection = UnwrapLambda(call.Arguments[1]);
                break;

            case nameof(Queryable.Skip):
                Skip = ConstantInt(call.Arguments[1], call);
                break;

            case nameof(Queryable.Take):
                Take = ConstantInt(call.Arguments[1], call);
                break;

            case nameof(Queryable.First):
                ApplyResult(QueryResultOperator.First, call);
                break;

            case nameof(Queryable.FirstOrDefault):
                ApplyResult(QueryResultOperator.FirstOrDefault, call);
                break;

            case nameof(Queryable.Single):
                ApplyResult(QueryResultOperator.Single, call);
                break;

            case nameof(Queryable.SingleOrDefault):
                ApplyResult(QueryResultOperator.SingleOrDefault, call);
                break;

            case nameof(Queryable.Last):
                ApplyResult(QueryResultOperator.Last, call);
                break;

            case nameof(Queryable.LastOrDefault):
                ApplyResult(QueryResultOperator.LastOrDefault, call);
                break;

            case nameof(Queryable.Any):
                ApplyResult(QueryResultOperator.Any, call);
                break;

            case nameof(Queryable.Count):
                ApplyResult(QueryResultOperator.Count, call);
                break;

            case nameof(Queryable.LongCount):
                ApplyResult(QueryResultOperator.LongCount, call);
                break;

            // A predicate over the server's filter input rather than over the element type.
            // It joins the same list: what differs is the shape it is written against.
            case "WhereShape":
                Predicates.Add(UnwrapLambda(call.Arguments[1]));

                // An argument name given inline is the same setting WithGraphQLArguments makes,
                // so the two share one slot and the later call in the chain wins.
                if (PartialEvaluator.Reduce(call.Arguments[2]) is ConstantExpression
                    { Value: string argumentName })
                    Arguments = Arguments with { Filter = argumentName };

                break;

            // Not an operator: it configures how the others are printed. Last call wins, so a
            // chain assembled from parts can override what an earlier part assumed.
            case nameof(GraphQLArgumentExtensions.WithGraphQLArguments):
                Arguments = PartialEvaluator.Reduce(call.Arguments[1]) is ConstantExpression
                    { Value: GraphQLArgumentNames names }
                    ? names
                    : throw GraphQLTranslationException.UnsupportedPredicate(
                        "argument names must be a constant", call);
                break;

            default:
                throw GraphQLTranslationException.UnsupportedOperator(call.Method.Name, call);
        }
    }

    /// <summary>
    /// Records the terminal operator, folding its optional predicate overload into the chain's
    /// <c>Where</c> clauses — <c>First(p =&gt; …)</c> filters exactly as <c>Where(…).First()</c> does.
    /// </summary>
    private void ApplyResult(QueryResultOperator @operator, MethodCallExpression call)
    {
        if (IsScalarResult)
            throw GraphQLTranslationException.UnsupportedOperator(call.Method.Name, call);

        if (call.Arguments.Count > 1)
            Predicates.Add(UnwrapLambda(call.Arguments[1]));

        ResultOperator = @operator;
    }

    private static LambdaExpression UnwrapLambda(Expression expression)
        => expression switch
        {
            UnaryExpression { NodeType: ExpressionType.Quote, Operand: LambdaExpression lambda } => lambda,
            LambdaExpression lambda => lambda,
            _ => throw GraphQLTranslationException.UnsupportedPredicate(
                $"expected a lambda but found '{expression.NodeType}'", expression)
        };

    private static int ConstantInt(Expression expression, Expression call)
        => PartialEvaluator.Reduce(expression) is ConstantExpression { Value: int value }
            ? value
            : throw GraphQLTranslationException.UnsupportedPredicate(
                "paging argument must be a constant integer", call);

    private static Type FindElementType(Expression expression)
    {
        var queryable = expression.Type.GetInterfaces()
            .Append(expression.Type)
            .FirstOrDefault(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IQueryable<>));

        if (queryable is not null)
            return queryable.GetGenericArguments()[0];

        throw new GraphQLTranslationException("FGQL001",
            $"'{expression.Type.Name}' is not an IQueryable<T>.", expression);
    }

    /// <summary>Merges every <c>Where</c> in the chain with <c>&amp;&amp;</c>, rebound to one parameter.</summary>
    /// <exception cref="GraphQLTranslationException">
    /// The chain mixes predicates written against different types — an element-type <c>Where</c>
    /// and a filter-shape one, say. They lower to different field paths, so merging them would
    /// silently produce a filter matching neither.
    /// </exception>
    public LambdaExpression? MergedPredicate()
    {
        if (Predicates.Count == 0)
            return null;

        var first = Predicates[0];
        if (Predicates.Count == 1)
            return first;

        var parameter = first.Parameters[0];

        foreach (var predicate in Predicates)
        {
            if (predicate.Parameters[0].Type != parameter.Type)
                throw new GraphQLTranslationException("FGQL020",
                    $"This chain filters over both '{parameter.Type.Name}' and "
                    + $"'{predicate.Parameters[0].Type.Name}'. Pick one shape: either the queried "
                    + "type or the filter input modelled with Where<TFilter>.",
                    predicate);
        }
        var body = first.Body;

        for (int i = 1; i < Predicates.Count; i++)
        {
            var next = Predicates[i];
            var rebound = ParameterRebinder.Rebind(next.Body, next.Parameters[0], parameter);
            body = Expression.AndAlso(body, rebound);
        }

        return Expression.Lambda(body, parameter);
    }
}
