using System.Text.Json;
using Feather.GraphQL.Http;
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

    public async ValueTask<JsonElement> ExecuteAsync(
        string query,
        IReadOnlyDictionary<string, object?> variables,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentNullException.ThrowIfNull(variables);

        // Checked here rather than in the constructor: a base address can legitimately be set on
        // the client after this executor was built.
        if (_client.BaseAddress is null && Endpoint is not { IsAbsoluteUri: true })
            throw new InvalidOperationException(
                "The HttpClient has no BaseAddress, so a relative GraphQL endpoint cannot be "
                + "resolved. Set BaseAddress, or pass an absolute endpoint path.");

        var request = new GraphQLRequest(query, variables);

        // Not `using`: when the read throws, GraphQLException carries this response so the caller
        // can read the body that explains the failure. Disposal passes to the exception with it.
        var response = await _client.SendGraphQLRequestAsync(request, Endpoint, cancellationToken)
            .ConfigureAwait(false);

        var data = await response.ReadGraphQLAsync<JsonElement>(cancellationToken).ConfigureAwait(false);
        response.Dispose();

        return data;
    }
}
