using System.Diagnostics.CodeAnalysis;

namespace Feather.GraphQL.Http.Request;

/// <summary>
/// The GraphQL over HTTP request body: the query text plus its variables, operation name and
/// extensions.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not part of the public API. Callers reach a server one of two ways — compose a
/// chain with the LINQ integration, or hand <c>SendGraphQLQueryAsync</c> a precompiled query
/// string — and this is the wire shape both funnel into.
/// </para>
/// <para>
/// A plain record, and it used to be a <see cref="Dictionary{TKey,TValue}"/> subclass. That
/// shape cost about 76 ns and 384 bytes on every request: serializing it meant walking a
/// dictionary of <see cref="object"/> through the serializer's polymorphic path, resolving a
/// contract for each value's runtime type, and writing three members that were almost always
/// null. It also let a request carry members GraphQL has no meaning for, which nothing wanted.
/// </para>
/// </remarks>
/// <param name="Query">The query text. Required; everything else is optional.</param>
/// <param name="Variables">
/// The values the document's variables take, as something that writes itself. It was an
/// <c>IReadOnlyDictionary&lt;string, object?&gt;</c>, which cost a dictionary per request, a box
/// per value, and a type switch per value on the way out — none of it telling the writer
/// anything the translator had not already decided.
/// </param>
/// <param name="OperationName">Which operation to run, for a document declaring more than one.</param>
/// <param name="Extensions">Anything a server understands beyond the specification.</param>
internal sealed record GraphQLRequest(
    [property: StringSyntax("GraphQL")] string Query,
    IGraphQLVariables? Variables = null,
    string? OperationName = null,
    IReadOnlyDictionary<string, object?>? Extensions = null);
