using System.Linq.Expressions;
using System.Reflection;

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

    public bool HasFilter => Predicates.Count > 0;
    public bool HasOrdering => Ordering.Count > 0;
    public bool HasPaging => Skip.HasValue || Take.HasValue;

    public static QueryChain Parse(Expression expression)
    {
        var calls = new Stack<MethodCallExpression>();

        var current = expression;
        while (current is MethodCallExpression call)
        {
            if (call.Method.DeclaringType != typeof(Queryable) && call.Method.DeclaringType != typeof(Enumerable))
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

            default:
                throw GraphQLTranslationException.UnsupportedOperator(call.Method.Name, call);
        }
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
    public LambdaExpression? MergedPredicate()
    {
        if (Predicates.Count == 0)
            return null;

        var first = Predicates[0];
        if (Predicates.Count == 1)
            return first;

        var parameter = first.Parameters[0];
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

internal sealed class ParameterRebinder(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
{
    public static Expression Rebind(Expression body, ParameterExpression from, ParameterExpression to)
        => new ParameterRebinder(from, to).Visit(body)!;

    protected override Expression VisitParameter(ParameterExpression node)
        => node == from ? to : base.VisitParameter(node);
}
