using JetBrains.Annotations;

namespace Feather.GraphQL.Linq.Query;

/// <summary>
/// Hands out queryables bound to one GraphQL endpoint.
/// </summary>
/// <remarks>
/// <para>
/// This is where the endpoint stops being the caller's problem. A queryable from here carries
/// its own execution path, so <c>ToList()</c>, <c>First()</c> and <c>foreach</c> mean what they
/// mean everywhere else in .NET — there is no separate "now send it" step to forget, and no
/// terminal that compiles but cannot run.
/// </para>
/// <para>
/// One endpoint per instance, and no type parameter naming it. An app talking to two GraphQL
/// APIs registers two sources under different service keys and injects them with
/// <c>[FromKeyedServices]</c> — the same way it would separate any other two implementations of
/// one contract.
/// </para>
/// <para>
/// How a source is built is a transport package's business: this assembly knows nothing about
/// HTTP. <c>Feather.GraphQL.Linq.Providers.HttpClient</c> supplies
/// <c>AddGraphQLQueryable</c> and the executor behind it.
/// </para>
/// </remarks>
[PublicAPI]
public interface IGraphQLQueryableSource
{
    /// <summary>
    /// Starts a query over a type carrying <c>[GenerateQueryable]</c>. Compose it with LINQ and
    /// finish it with any supported terminal.
    /// </summary>
    IQueryable<T> Queryable<T>();
}
