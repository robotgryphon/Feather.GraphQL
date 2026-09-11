namespace Feather.GraphQL.Linq.Expressions;

/// <summary>
/// The operator that terminated a chain, and therefore the shape of the result.
/// </summary>
/// <remarks>
/// Kept separate from the composition operators because a result operator changes what the
/// server is asked for, not just how the answer is reduced: <c>First</c> becomes a page of one,
/// <c>Count</c> becomes a <c>totalCount</c> selection.
/// </remarks>
internal enum QueryResultOperator
{
    /// <summary>No result operator — the chain yields the sequence itself.</summary>
    Sequence = 0,
    First,
    FirstOrDefault,
    Single,
    SingleOrDefault,
    Last,
    LastOrDefault,
    Any,
    Count,
    LongCount
}