using System.Linq.Expressions;
using Feather.GraphQL.Linq.Expressions;
using Feather.GraphQL.Linq.Metadata;
using Feather.GraphQL.Linq.Query;

namespace Feather.GraphQL.Linq.Execution;

/// <summary>
/// Turns the rows a transport returned into the result the chain's terminal asked for.
/// </summary>
/// <remarks>
/// The projection is applied here rather than deserialized into: an anonymous type has no
/// accessible setters and no parameterless constructor, so the rows are read as the queried type
/// and the <c>Select</c> runs over them. That also keeps one rule for field naming — the one the
/// selection set was built from.
/// </remarks>
internal static class ResultMaterializer
{
    /// <summary>
    /// The projection, as a function of one materialized row — or null when the chain had no
    /// <c>Select</c> and the queried type is already the result.
    /// </summary>
    /// <remarks>
    /// A generated shaper when one was emitted for this projection, and otherwise the lambda,
    /// compiled into an adapter that takes and returns <see cref="object"/>. The adapter is what
    /// makes the difference: invoking the lambda's own delegate means
    /// <see cref="Delegate.DynamicInvoke"/>, which builds an argument array and reflects over the
    /// signature for every row.
    /// </remarks>
    public static Func<object?, object?>? Shaper(GraphQLQueryPlan plan)
    {
        if (plan.Projection is not { } projection)
            return null;

        return GraphQLProjectionRegistry.Find(projection) ?? Adapt(projection);
    }

    /// <summary>Compiles <c>TElement -&gt; TOut</c> into <c>object -&gt; object</c>.</summary>
    private static Func<object?, object?> Adapt(LambdaExpression projection)
    {
        var source = Expression.Parameter(typeof(object), "source");

        var body = Expression.Convert(
            Expression.Invoke(projection, Expression.Convert(source, projection.Parameters[0].Type)),
            typeof(object));

        return Expression.Lambda<Func<object?, object?>>(body, source).Compile();
    }

    /// <summary>Reduces the rows the way the chain's terminal operator asks.</summary>
    /// <remarks>
    /// The server was already told to return at most what the operator needs — one row for
    /// <c>First</c>, two for <c>Single</c> — so this is a reduction over a narrow page, not over
    /// a collection that was fetched whole and then thrown away.
    /// </remarks>
    public static TOut Reduce<TOut>(GraphQLQueryPlan plan, IReadOnlyList<TOut> rows)
    {
        switch (plan.ResultOperator)
        {
            case QueryResultOperator.First when rows.Count > 0:
            case QueryResultOperator.FirstOrDefault when rows.Count > 0:
            case QueryResultOperator.Single when rows.Count == 1:
            case QueryResultOperator.SingleOrDefault when rows.Count == 1:
                return rows[0];

            case QueryResultOperator.Last when rows.Count > 0:
            case QueryResultOperator.LastOrDefault when rows.Count > 0:
                return rows[^1];

            case QueryResultOperator.FirstOrDefault:
            case QueryResultOperator.SingleOrDefault when rows.Count == 0:
            case QueryResultOperator.LastOrDefault:
                return default!;

            case QueryResultOperator.First:
            case QueryResultOperator.Last:
            case QueryResultOperator.Single when rows.Count == 0:
                throw new InvalidOperationException("The query returned no elements.");

            case QueryResultOperator.Single:
            case QueryResultOperator.SingleOrDefault:
                throw new InvalidOperationException("The query returned more than one element.");

            default:
                throw GraphQLTranslationException.UnsupportedOperator(plan.ResultOperator.ToString());
        }
    }
}
