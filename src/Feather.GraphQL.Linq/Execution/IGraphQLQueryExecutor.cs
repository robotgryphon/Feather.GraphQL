using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Feather.GraphQL.Linq.Filtering;
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
/// Narrow on purpose, in both directions: a document and its variables go in, the raw
/// <c>data</c> element comes back. Nothing about how the answer is shaped or materialized
/// crosses the seam, so that stays one implementation shared by every transport.
/// </para>
/// </remarks>
[PublicAPI]
public interface IGraphQLQueryExecutor
{
    /// <summary>The provider used to lower predicates, so one endpoint means one dialect.</summary>
    IFilterTranslationProvider FilterProvider { get; }

    /// <summary>
    /// Runs a translated query and returns the <c>data</c> element of the response.
    /// </summary>
    /// <param name="query">
    /// The document, parameterized: every argument the chain contributed is bound to a variable,
    /// so this text is constant per query shape. That is what makes it a usable APQ key — hash
    /// it here if the server supports persisted queries.
    /// </param>
    /// <param name="variables">The values those variables take, keyed by name.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Errors reported by the server are the implementation's to raise; returning a
    /// <c>data</c> element means the query succeeded. What comes back is read by the caller —
    /// a transport does not know, and need not know, what shape the answer takes.
    /// </remarks>
    ValueTask<JsonElement> ExecuteAsync(
        [StringSyntax("GraphQL")] string query,
        IReadOnlyDictionary<string, object?> variables,
        CancellationToken cancellationToken);
}
