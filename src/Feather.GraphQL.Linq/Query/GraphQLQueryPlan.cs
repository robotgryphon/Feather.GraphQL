using System.Linq.Expressions;
using System.Diagnostics.CodeAnalysis;
using Feather.GraphQL.Linq.Execution;
using Feather.GraphQL.Linq.Expressions;

namespace Feather.GraphQL.Linq.Query;

/// <summary>
/// Everything one execution needs: the operation to run, and how to read what comes back.
/// </summary>
/// <remarks>
/// <para>
/// Translation and materialization are two halves of one decision — emitting <c>nodes</c> and
/// then looking for <c>items</c> is the bug this type exists to make unrepresentable. The
/// translator produces both halves at once, so they cannot drift.
/// </para>
/// <para>
/// Internal: a transport is handed the document and its variables and nothing else, because
/// that is all it reads. The rest of this — element type, paging, projection, result operator —
/// is consumed by the materializer after the transport has returned.
/// </para>
/// </remarks>
/// <param name="Query">The printed document, parameterized: this is what gets sent.</param>
/// <param name="Variables">Every argument the chain bound, keyed by variable name.</param>
/// <param name="ElementType">The queried element type, from the chain's source.</param>
/// <param name="RootField">The field on the schema's <c>Query</c> type holding the result.</param>
/// <param name="Paging">Which wrapper, if any, sits between the root field and the elements.</param>
/// <param name="Projection">The <c>Select</c> lambda, applied to each materialized element.</param>
/// <param name="ResultOperator">How the sequence is reduced to the caller's result.</param>
internal sealed record GraphQLQueryPlan(
    [property: StringSyntax("GraphQL")] string Query,
    IGraphQLVariables Variables,
    Type ElementType,
    string RootField,
    PagingKind Paging,
    LambdaExpression? Projection,
    QueryResultOperator ResultOperator)
{
    /// <summary>
    /// The projection, already resolved to the function that applies it.
    /// </summary>
    /// <remarks>
    /// Set only by a precompiled plan, which has a key instead of a lambda: the compiler computed
    /// the key, so there is nothing left for <see cref="Projection"/> to be derived from and
    /// nothing to derive it for. When this is null the materializer resolves
    /// <see cref="Projection"/> the way it always has.
    /// </remarks>
    public Func<object?, object?>? Shaper { get; init; }

    /// <summary>
    /// Builds this query's filter from the values its predicate binds.
    /// </summary>
    /// <remarks>
    /// Set only by a precompiled plan whose filter the compiler could print. The shape is fixed
    /// in generated code; the values are read out of the expression tree on each execution, which
    /// is the one part of a predicate that cannot be known any earlier.
    /// </remarks>
    public Func<IReadOnlyList<object?>, IGraphQLVariables>? Filter { get; init; }

    /// <summary>How many values <see cref="Filter"/> expects.</summary>
    public int FilterHoles { get; init; }

}
