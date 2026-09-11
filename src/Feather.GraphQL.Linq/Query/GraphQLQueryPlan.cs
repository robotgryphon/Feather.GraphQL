using System.Linq.Expressions;
using System.Diagnostics.CodeAnalysis;
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
    IReadOnlyDictionary<string, object?> Variables,
    Type ElementType,
    string RootField,
    PagingKind Paging,
    LambdaExpression? Projection,
    QueryResultOperator ResultOperator)
{
    /// <summary>
    /// The payload of a chain that bound no arguments, which is most of them.
    /// </summary>
    /// <remarks>
    /// Shared rather than allocated per query. A payload is read and never written — the
    /// transport serializes it and the plan is discarded — so one empty dictionary answers every
    /// query that has nothing to say.
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, object?> NoVariables =
        new Dictionary<string, object?>(0, StringComparer.Ordinal);
}
