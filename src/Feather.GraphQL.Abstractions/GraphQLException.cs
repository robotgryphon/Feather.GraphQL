using JetBrains.Annotations;

namespace Feather.GraphQL;

/// <summary>
/// A GraphQL query that failed, whatever carried it and whatever went wrong.
/// </summary>
/// <remarks>
/// <para>
/// A GraphQL error is not a transport error — an HTTP server routinely reports one with
/// <c>200 OK</c>, and a websocket with a perfectly healthy socket. So failure cannot be read off
/// the transport, and making it an exception is what lets the success path return the data itself
/// rather than a wrapper every caller has to unpack and check.
/// </para>
/// <para>
/// Abstract, and deliberately empty. It is the type to catch when all that matters is that the
/// query did not answer; everything about <em>why</em> belongs to a derived type, because the
/// reasons do not generalise. The server's own <c>errors</c> array is one such reason and not the
/// only one — a reply can fail by carrying no <c>data</c>, by not being a GraphQL answer at all,
/// or by never arriving — so it lives on <c>GraphQLErrorsException</c>, in the package that reads
/// replies, rather than here where it would have to be empty for every failure that had nothing
/// to do with the server's opinion of the query.
/// </para>
/// <para>
/// Keeping it empty is also what keeps it free: a package that only needs to say a query failed
/// takes no dependency on the shape of a reply, and the error model lives with the reader that
/// builds it.
/// </para>
/// </remarks>
[PublicAPI]
public abstract class GraphQLException : Exception
{
    /// <param name="message">What went wrong, in the terms the thrower knows.</param>
    protected GraphQLException(string message)
        : base(message)
    {
    }

    /// <param name="message">What went wrong, in the terms the thrower knows.</param>
    /// <param name="innerException">What was being done when it did.</param>
    protected GraphQLException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
