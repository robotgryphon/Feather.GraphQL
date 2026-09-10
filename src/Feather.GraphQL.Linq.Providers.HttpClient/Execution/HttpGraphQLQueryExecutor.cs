using System.Text.Json;
using Feather.GraphQL.Http;
using Feather.GraphQL.Http.Request;
using Feather.GraphQL.Http.Response;
using Feather.GraphQL.Linq.Execution;
using Feather.GraphQL.Linq.Filtering;
using Feather.GraphQL.Linq.Query;
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
    /// <param name="filterProvider">The dialect predicates are lowered to.</param>
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
        GraphQLQueryPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        // Checked here rather than in the constructor: a base address can legitimately be set on
        // the client after this executor was built.
        if (_client.BaseAddress is null && Endpoint is not { IsAbsoluteUri: true })
            throw new InvalidOperationException(
                "The HttpClient has no BaseAddress, so a relative GraphQL endpoint cannot be "
                + "resolved. Set BaseAddress, or pass an absolute endpoint path.");

        var request = new GraphQLRequest(plan.Query, plan.Variables);

        using var response = await _client.SendGraphQLRequestAsync(request, Endpoint, cancellationToken)
            .ConfigureAwait(false);

        var body = await response.Content
            .ReadAsGraphQLAsync<JsonElement>(cancellationToken)
            .ConfigureAwait(false);

        // A GraphQL error is not an HTTP error: servers routinely answer 200 with an errors array,
        // so the status code is checked only after the body has had its say.
        if (body?.Errors is { Length: > 0 })
            throw new GraphQLResponseException(body);

        response.EnsureSuccessStatusCode();

        if (body is null || body.Data.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            throw new GraphQLTranslationException("FGQL017",
                "The response carried neither data nor errors.");

        return body.Data;
    }
}
