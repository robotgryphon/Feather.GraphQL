using Feather.GraphQL.Primitives;
using JetBrains.Annotations;

namespace Feather.GraphQL;

/// <summary>
/// A query the server answered by reporting what was wrong with it.
/// </summary>
/// <remarks>
/// <para>
/// The failure with something to say. A reply that carried an <c>errors</c> array is the server's
/// own account of what happened — a field that does not exist, a variable of the wrong type, a
/// resolver that threw — and that account is worth more to a caller than anything the client
/// could infer, so it is carried through rather than summarised away.
/// </para>
/// <para>
/// It lives beside the reader that builds those errors rather than beside
/// <see cref="GraphQLException"/>, because <see cref="GraphQLError"/> is a reply's own shape:
/// parsed out of the same bytes as the rows, by the same pass. A transport reports failures in
/// this vocabulary by throwing this or deriving from it; one that has nothing of its own to add
/// can throw it as it stands.
/// </para>
/// <para>
/// Catch <see cref="GraphQLException"/> to handle any failed query. Catch this one when the
/// server's errors are what you mean to read — and note that a query can fail without producing
/// any, which is exactly the case this type would have made indistinguishable had the array
/// stayed on the base.
/// </para>
/// </remarks>
[PublicAPI]
public class GraphQLErrorsException : GraphQLException
{
    /// <summary>The <c>errors</c> array, parsed. Empty when the reply carried none.</summary>
    public GraphQLError[] Errors { get; }

    /// <param name="errors">What the server reported, if anything.</param>
    public GraphQLErrorsException(GraphQLError[]? errors)
        : base(Describe(errors) ?? "The GraphQL query failed.")
        => Errors = errors ?? [];

    /// <param name="errors">What the server reported, if anything.</param>
    /// <param name="message">
    /// What to say instead. A transport that knows more than the errors do — or that has none to
    /// go on — says it here.
    /// </param>
    public GraphQLErrorsException(GraphQLError[]? errors, string message)
        : base(message)
        => Errors = errors ?? [];

    /// <param name="errors">What the server reported, if anything.</param>
    /// <param name="message">What to say instead.</param>
    /// <param name="innerException">What was being done when it failed.</param>
    public GraphQLErrorsException(GraphQLError[]? errors, string message, Exception? innerException)
        : base(message, innerException)
        => Errors = errors ?? [];

    /// <summary>
    /// Leads with the server's own words, because that is what a reader needs first.
    /// </summary>
    /// <param name="errors">What the server reported, if anything.</param>
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
