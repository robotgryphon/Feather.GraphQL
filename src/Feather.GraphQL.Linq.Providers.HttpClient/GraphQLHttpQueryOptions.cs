using Feather.GraphQL.Linq.Query;
using JetBrains.Annotations;

namespace Feather.GraphQL.Linq.Providers;

/// <summary>
/// Everything a query needs to know about the schema, plus where to post it.
/// </summary>
/// <remarks>
/// One options object rather than two, so a call site configures a query in one delegate. The
/// path is the only member the transport adds; the rest describes the schema and is understood
/// by the translator alone.
/// </remarks>
[PublicAPI]
public sealed class GraphQLHttpQueryOptions : GraphQLQueryOptions
{
    /// <summary>
    /// The path GraphQL is served from, resolved against the client's
    /// <see cref="HttpClient.BaseAddress"/>. Null or empty posts to the base address unchanged,
    /// for a client already pointed straight at the endpoint. An absolute URI is used as-is and
    /// ignores the base address.
    /// </summary>
    /// <remarks>
    /// Resolution is <see cref="HttpClient"/>'s own, which means a base address is treated as a
    /// document rather than a directory: <c>https://host/v1</c> plus <c>graphql</c> is
    /// <c>https://host/graphql</c>, not <c>https://host/v1/graphql</c>. Give the base address a
    /// trailing slash to keep its path.
    /// </remarks>
    public string? EndpointPath { get; set; }
}
