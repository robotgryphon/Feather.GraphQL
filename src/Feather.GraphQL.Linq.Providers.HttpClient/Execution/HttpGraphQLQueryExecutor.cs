using System.Runtime.CompilerServices;
using Feather.GraphQL.Http;
using Feather.GraphQL.Http.Response;
using Feather.GraphQL.Http.Request;
using Feather.GraphQL.Linq.Execution;
using Feather.GraphQL.Linq.Filtering;
using JetBrains.Annotations;

namespace Feather.GraphQL.Linq.Providers;

/// <summary>
/// Runs translated queries over an <see cref="HttpClient"/> bound to one endpoint.
/// </summary>
/// <remarks>
/// The only place the LINQ provider and the HTTP client meet. Everything either side knows about
/// the other passes through here: a plan goes in, a <c>data</c> element comes out.
/// </remarks>
[PublicAPI]
public sealed class HttpGraphQLQueryExecutor : IGraphQLQueryExecutor
{
    private readonly HttpClient _client;

    /// <summary>
    /// The endpoint this executor posts to. Relative unless an absolute path was supplied, and
    /// null when the client's base address is itself the endpoint.
    /// </summary>
    public Uri? Endpoint { get; }

    /// <summary>The dialect predicates are lowered to; HotChocolate's when unspecified.</summary>
    public IFilterTranslationProvider FilterProvider { get; }

    /// <param name="client">The client to post through. Its base address names the host.</param>
    /// <param name="endpointPath">
    /// The path GraphQL is served from, resolved against the client's
    /// <see cref="HttpClient.BaseAddress"/>.
    /// Null or empty posts to the base address unchanged, for a client already pointed straight
    /// at the endpoint. An absolute URI is used as-is and ignores the base address.
    /// </param>
    /// <param name="filterProvider">
    /// The dialect predicates are lowered to. Only used when a query does not name one of its
    /// own; <c>GraphQLHttpQueryOptions.FilterProvider</c> is the usual way in.
    /// </param>
    /// <remarks>
    /// Resolution is <see cref="HttpClient"/>'s own, which means a base address is treated as a
    /// document rather than a directory: <c>https://host/v1</c> plus <c>graphql</c> is
    /// <c>https://host/graphql</c>, not <c>https://host/v1/graphql</c>. Give the base address a
    /// trailing slash to keep its path.
    /// </remarks>
    public HttpGraphQLQueryExecutor(
        HttpClient client,
        string? endpointPath = null,
        IFilterTranslationProvider? filterProvider = null)
    {
        ArgumentNullException.ThrowIfNull(client);

        _client = client;
        Endpoint = string.IsNullOrWhiteSpace(endpointPath)
            ? null
            : new Uri(endpointPath, UriKind.RelativeOrAbsolute);
        FilterProvider = filterProvider ?? HotChocolateFilterProvider.Instance;
    }

    public async ValueTask<IReadOnlyList<TElement>> ExecuteAsync<TElement>(
        GraphQLOperation operation,
        CancellationToken cancellationToken)
    {
        var response = await PostAsync(operation, cancellationToken).ConfigureAwait(false);

        // Buffered into pooled memory, then read in one span. The reply is never turned into a
        // document on the way: the reader walks to the root field and deserializes the rows
        // where it finds them.
        using var body = await ResponseBuffer
            .ReadAsync(response.Content, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var rows = GraphQLReplyReader.ReadRows<TElement>(body.Bytes.Span, operation);

            response.Dispose();

            return rows;
        }
        catch (GraphQLReplyFailedException failure)
        {
            throw Failed(failure, body.Bytes, response);
        }
    }

    public async IAsyncEnumerable<TElement> StreamAsync<TElement>(
        GraphQLOperation operation,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var response = await PostAsync(operation, cancellationToken).ConfigureAwait(false);

        using var body = await ResponseBuffer
            .ReadAsync(response.Content, cancellationToken)
            .ConfigureAwait(false);

        // Enumerated by hand rather than with `await foreach`, because a yield cannot sit inside
        // a try that has a catch, and a reply that failed still has to become this transport's
        // own exception.
        await using var rows = GraphQLReplyReader
            .StreamRows<TElement>(body.Bytes, operation, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        while (true)
        {
            bool moved;

            try
            {
                moved = await rows.MoveNextAsync().ConfigureAwait(false);
            }
            catch (GraphQLReplyFailedException failure)
            {
                throw Failed(failure, body.Bytes, response);
            }

            if (!moved)
                break;

            yield return rows.Current;
        }

        response.Dispose();
    }

    public async ValueTask<long> ExecuteCountAsync(
        GraphQLOperation operation,
        CancellationToken cancellationToken)
    {
        var response = await PostAsync(operation, cancellationToken).ConfigureAwait(false);

        using var body = await ResponseBuffer
            .ReadAsync(response.Content, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            long total = GraphQLReplyReader.ReadCount(body.Bytes.Span, operation);

            response.Dispose();

            return total;
        }
        catch (GraphQLReplyFailedException failure)
        {
            throw Failed(failure, body.Bytes, response);
        }
    }

    /// <summary>
    /// Turns a reply the reader could not read into the transport's own exception.
    /// </summary>
    /// <remarks>
    /// The reader reports that a reply failed but not what it said, because the LINQ half of the
    /// library does not reference the assembly a <c>GraphQLError</c> is defined in. Reading them
    /// is a second pass, over a reply whose payload was deliberately never deserialized.
    /// </remarks>
    private static Exception Failed(
        GraphQLReplyFailedException failure,
        ReadOnlyMemory<byte> body,
        HttpResponseMessage response)
    {
        if (failure.CarriedErrors)
            return new GraphQLHttpException(GraphQLResponseReader.ErrorsIn(body) ?? [], response);

        // A reply with neither data nor errors is usually a server that failed outside GraphQL,
        // and its status says so more usefully than anything in the body does.
        response.EnsureSuccessStatusCode();

        return new GraphQLHttpException(errors: null, response);
    }

    /// <summary>
    /// Posts the operation.
    /// </summary>
    /// <remarks>
    /// Not disposed here: when the read throws, <c>GraphQLException</c> carries this response so
    /// the caller can read the body that explains the failure. Disposal passes to the exception
    /// with it.
    /// </remarks>
    private async ValueTask<HttpResponseMessage> PostAsync(
        GraphQLOperation operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Query);

        // Checked here rather than in the constructor: a base address can legitimately be set on
        // the client after this executor was built.
        if (_client.BaseAddress is null && Endpoint is not { IsAbsoluteUri: true })
            throw new InvalidOperationException(
                "The HttpClient has no BaseAddress, so a relative GraphQL endpoint cannot be "
                + "resolved. Set BaseAddress, or pass an absolute endpoint path.");

        var request = new GraphQLRequest(operation.Query, operation.Variables);

        return await _client
            .SendGraphQLRequestAsync(request, Endpoint, cancellationToken)
            .ConfigureAwait(false);
    }
}
