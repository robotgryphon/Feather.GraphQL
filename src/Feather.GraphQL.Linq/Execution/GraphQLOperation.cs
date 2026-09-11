using System.Diagnostics.CodeAnalysis;
using JetBrains.Annotations;

namespace Feather.GraphQL.Linq.Execution;

/// <summary>
/// One query, as a transport sees it: what to send, and where in the reply the rows will be.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is something a transport actually reads. The rest of the translated plan —
/// the element type, the projection, the result operator — stays behind the seam, because the
/// materializer is what consumes it and passing it across would make it public to describe a
/// contract that never touches it.
/// </para>
/// <para>
/// The root field and its wrapper are on this side of the line because finding the rows is the
/// reading half of a transport's job. A reply is a document whose shape the query decided, and
/// naming the two places it can put the rows is what lets that document be read in one pass
/// instead of being turned into a tree first and searched afterwards.
/// </para>
/// </remarks>
/// <param name="Query">The printed document, parameterized: this is what goes on the wire.</param>
/// <param name="Variables">Every argument the chain bound, keyed by variable name.</param>
/// <param name="RootField">The field on the schema's <c>Query</c> type holding the result.</param>
/// <param name="Paging">How the server pages this field, as the query was told it does.</param>
[PublicAPI]
public sealed record GraphQLOperation(
    [property: StringSyntax("GraphQL")] string Query,
    IGraphQLVariables Variables,
    string RootField,
    PagingKind Paging)
{
    /// <summary>
    /// The member the rows sit under inside the root field, or null when the root field is
    /// itself the list.
    /// </summary>
    /// <remarks>
    /// Derived rather than passed, so the one decision about how a paging kind appears on the
    /// wire lives in one place. Emitting <c>nodes</c> and then reading <c>items</c> is the bug
    /// this leaves no room for.
    /// </remarks>
    public string? Wrapper => Paging switch
    {
        PagingKind.Cursor => "nodes",
        PagingKind.Offset => "items",
        _ => null
    };
}
