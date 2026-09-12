using Feather.GraphQL.Primitives;
using JetBrains.Annotations;

namespace Feather.GraphQL.Http;

/// <summary>
/// A GraphQL query that failed over HTTP.
/// </summary>
/// <remarks>
/// Adds the one thing this transport can supply and no other can: the reply itself. Catch
/// <see cref="GraphQLException"/> to handle a failed query whatever carried it,
/// <see cref="GraphQLErrorsException"/> to read what the server reported, and this to read the
/// status, the headers or the body.
/// </remarks>
[PublicAPI]
public sealed class GraphQLHttpException : GraphQLErrorsException
{
    /// <summary>
    /// The reply — status, headers and body. Live and undisposed when thrown, because a failure
    /// is exactly when the raw body matters; disposing it is the handler's job.
    /// </summary>
    public HttpResponseMessage Response { get; }

    public GraphQLHttpException(GraphQLError[]? errors, HttpResponseMessage response)
        : base(errors, Describe(errors) ?? NotGraphQL(response))
    {
        ArgumentNullException.ThrowIfNull(response);

        Response = response;
    }

    /// <summary>
    /// The message for a reply that was not a GraphQL answer at all — a gateway error page, say.
    /// The status is all there is to go on, so it leads.
    /// </summary>
    private static string NotGraphQL(HttpResponseMessage response)
        => $"The GraphQL response carried neither data nor errors ({(int)response.StatusCode} "
            + $"{response.ReasonPhrase ?? response.StatusCode.ToString()}).";
}
