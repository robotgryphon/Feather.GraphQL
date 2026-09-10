using System.Text.Json;
using Feather.GraphQL.Linq.Filtering;
using Feather.GraphQL.Linq.Query;
using JetBrains.Annotations;

namespace Feather.GraphQL.Linq.Execution;

/// <summary>
/// What turns a translated query into a response body — the transport seam.
/// </summary>
/// <remarks>
/// <para>
/// This library translates LINQ to GraphQL; it does not decide how the result travels, and
/// names no transport type anywhere in this assembly. An implementation over
/// <c>HttpClient</c> ships as <c>Feather.GraphQL.Linq.Providers.HttpClient</c>, and anything
/// else — a websocket, an in-process schema, a recorded fixture — is one class away.
/// </para>
/// <para>
/// Narrow on purpose: it hands back the raw <c>data</c> element rather than anything typed, so
/// materialization stays one implementation shared by every transport.
/// </para>
/// </remarks>
[PublicAPI]
public interface IGraphQLQueryExecutor
{
    /// <summary>The provider used to lower predicates, so one endpoint means one dialect.</summary>
    IFilterTranslationProvider FilterProvider { get; }

    /// <summary>
    /// Runs the plan's document and returns the <c>data</c> element of the response.
    /// </summary>
    /// <remarks>
    /// Errors reported by the server are the implementation's to raise; returning a
    /// <c>data</c> element means the query succeeded.
    /// </remarks>
    ValueTask<JsonElement> ExecuteAsync(GraphQLQueryPlan plan, CancellationToken cancellationToken);
}
