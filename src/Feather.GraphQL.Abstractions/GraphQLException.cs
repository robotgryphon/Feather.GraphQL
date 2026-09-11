using Feather.GraphQL.Primitives;
using JetBrains.Annotations;

namespace Feather.GraphQL;

/// <summary>
/// A GraphQL query that failed, whatever carried it.
/// </summary>
/// <remarks>
/// <para>
/// A GraphQL error is not a transport error — an HTTP server routinely reports one with
/// <c>200 OK</c>, and a websocket with a perfectly healthy socket. So failure cannot be read off
/// the transport, and making it an exception is what lets the success path return the data
/// itself rather than a wrapper every caller has to unpack and check.
/// </para>
/// <para>
/// Abstract, and holding only what every transport can supply: the errors the server reported.
/// A transport derives from it to add what is particular to itself — the HTTP one carries the
/// <c>HttpResponseMessage</c> — so code that cares only that the query failed catches this, and
/// code that wants the reply catches the derived type.
/// </para>
/// </remarks>
[PublicAPI]
public abstract class GraphQLException : Exception
{
    /// <summary>The <c>errors</c> array, parsed. Empty when the reply carried none.</summary>
    public GraphQLError[] Errors { get; }

    protected GraphQLException(GraphQLError[]? errors, string message)
        : base(message)
        => Errors = errors ?? [];

    protected GraphQLException(GraphQLError[]? errors, string message, Exception? innerException)
        : base(message, innerException)
        => Errors = errors ?? [];

    /// <summary>
    /// Leads with the server's own words, because that is what a reader needs first.
    /// </summary>
    /// <returns>
    /// A summary of <paramref name="errors"/>, or null when there were none — in which case the
    /// transport knows better than this type what went wrong, and supplies its own message.
    /// </returns>
    protected static string? Describe(GraphQLError[]? errors)
    {
        if (errors is not { Length: > 0 })
            return null;

        string first = errors[0].Message;

        return errors.Length == 1
            ? $"The GraphQL server reported an error: {first}"
            : $"The GraphQL server reported {errors.Length} errors, the first being: {first}";
    }
}
