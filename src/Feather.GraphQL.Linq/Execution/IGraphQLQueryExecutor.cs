using Feather.GraphQL.Linq.Filtering;
using JetBrains.Annotations;

namespace Feather.GraphQL.Linq.Execution;

/// <summary>
/// Runs a translated query and returns what came back.
/// </summary>
/// <remarks>
/// <para>
/// The seam between the LINQ provider and whatever carries a request. An operation goes in; rows
/// come out. Nothing about how a reply was parsed crosses it — <see cref="GraphQLReplyReader"/>
/// is the reading half, and an implementation is expected to use it rather than invent one.
/// </para>
/// <para>
/// Reading is buffered by default and streamed on request, because the two have genuinely
/// different costs and neither is right for both callers. Buffering the reply and reading it in
/// one span is about 13% faster over a thousand rows and about 15% faster over a handful, which
/// is what <c>ToArray</c>, <c>ToList</c> and every result operator want — they materialize the
/// whole sequence anyway. Streaming gives that back in exchange for rows that do not all have to
/// exist at once, and for the ability to stop early, which is what <c>await foreach</c> wants.
/// </para>
/// <para>
/// One consequence of streaming is worth stating: a server may send <c>errors</c> after
/// <c>data</c>, and a stream that has already yielded rows cannot take them back. Enumerating to
/// the end still raises, so the terminals are unaffected; a caller that breaks early may have
/// read rows from a query that then failed.
/// </para>
/// </remarks>
[PublicAPI]
public interface IGraphQLQueryExecutor
{
    /// <summary>The provider used to lower predicates, so one endpoint means one dialect.</summary>
    IFilterTranslationProvider FilterProvider { get; }

    /// <summary>
    /// Runs an operation and reads every row it returned.
    /// </summary>
    /// <typeparam name="TElement">
    /// The queried element type — the shape of one row on the wire, before any projection. A
    /// projection is applied after this returns, because an anonymous type cannot be
    /// deserialized into.
    /// </typeparam>
    /// <remarks>
    /// Errors reported by the server are the implementation's to raise; returning rows means the
    /// query succeeded.
    /// </remarks>
    ValueTask<IReadOnlyList<TElement>> ExecuteAsync<TElement>(
        GraphQLOperation operation,
        CancellationToken cancellationToken);

    /// <summary>
    /// Runs an operation and yields its rows as they are read.
    /// </summary>
    /// <remarks>
    /// The sequence is read lazily: abandoning it stops the read, and the rows that were never
    /// reached are never deserialized.
    /// </remarks>
    IAsyncEnumerable<TElement> StreamAsync<TElement>(
        GraphQLOperation operation,
        CancellationToken cancellationToken);

    /// <summary>
    /// Runs a count operation and reads the <c>totalCount</c> it asked the connection for.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="ExecuteAsync{TElement}"/> because a count is a different
    /// question, not a different shape of the same one: the document selects a number and no
    /// rows, so there is no element type for one to be read as.
    /// </remarks>
    ValueTask<long> ExecuteCountAsync(
        GraphQLOperation operation,
        CancellationToken cancellationToken);
}
