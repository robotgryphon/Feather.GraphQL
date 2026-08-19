using Feather.GraphQL.Request;
using JetBrains.Annotations;

namespace Feather.GraphQL.Linq.Hosting;

/// <summary>
/// Composes and sends GraphQL queries against one endpoint, hiding <see cref="HttpClient"/> from
/// consumers.
/// </summary>
/// <typeparam name="TSchema">
/// A marker type naming the endpoint. Not optional: an app talking to two GraphQL APIs needs two
/// providers, and <c>Queryable&lt;Person&gt;()</c> has no other way to say which. Cheap to design
/// in, a breaking change to every call site to retrofit.
/// </typeparam>
/// <remarks>
/// A convenience layer, not a prerequisite — <see cref="Query.GraphQLQueryable.For{T}"/> needs
/// nothing injected.
/// </remarks>
[PublicAPI]
public interface IGraphQLQueryableProvider<TSchema>
{
    /// <summary>Starts a query over a type carrying <c>[GenerateQueryable]</c>.</summary>
    IQueryable<T> Queryable<T>();

    /// <summary>Translates and posts a chain composed from <see cref="Queryable{T}"/>.</summary>
    Task<HttpResponseMessage> SendAsync<T>(IQueryable<T> query, CancellationToken cancellationToken = default);

    /// <summary>Posts an already-translated request.</summary>
    Task<HttpResponseMessage> SendAsync(GraphQLRequest request, CancellationToken cancellationToken = default);

    /// <summary>Translates a chain, posts it, and materializes the result.</summary>
    /// <remarks>
    /// The terminal to await before handing results to a component that materializes an
    /// <see cref="IQueryable{T}"/> synchronously — pass it <c>result.AsQueryable()</c>.
    /// </remarks>
    Task<T[]> ToArrayAsync<T>(IQueryable<T> query, CancellationToken cancellationToken = default);

    /// <inheritdoc cref="ToArrayAsync{T}"/>
    Task<List<T>> ToListAsync<T>(IQueryable<T> query, CancellationToken cancellationToken = default);
}
