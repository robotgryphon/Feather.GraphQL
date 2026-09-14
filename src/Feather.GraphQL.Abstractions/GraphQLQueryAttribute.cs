using System.Diagnostics.CodeAnalysis;
using JetBrains.Annotations;

namespace Feather.GraphQL;

/// <summary>
/// Declares a query the compiler writes the implementation for.
/// </summary>
/// <remarks>
/// <para>
/// The shape <c>[GeneratedRegex]</c> and <c>[LoggerMessage]</c> use: the method stands in for the
/// work, and the compiler supplies what it does. What makes it worth having here is where the
/// values come from — they are the method's own parameters, handed over by the caller. Nothing is
/// composed, so no expression tree is built, nothing is walked, and nothing is read back out of a
/// closure.
/// </para>
/// <para>
/// There are two ways to say what the query is, and the method says which by its own shape:
/// </para>
/// <example>
/// <code>
/// // A document, on a partial method: the compiler writes the body.
/// [GraphQLQuery("query($code: String!) { countries(where: { code: { eq: $code } }) { name } }")]
/// public static partial Task&lt;Country[]&gt; ByCodeAsync(
///     HttpClient client, string code, CancellationToken cancellationToken = default);
///
/// // A chain, as the body: the compiler replaces every call with what it compiles to.
/// [GraphQLQuery]
/// private static Task&lt;Country[]&gt; ByCodeAsync(
///     HttpClient client, string code, CancellationToken cancellationToken)
///     =&gt; client.CreateQueryable&lt;Country&gt;("countries")
///         .Where(c =&gt; c.Code == code)
///         .ToArrayAsync(cancellationToken);
/// </code>
/// </example>
/// <para>
/// They compile to the same thing. A document is read as written; a chain is translated at build
/// time into the document it stands for — and either way the request, the variables payload and
/// the reader are all written out, so the query runs without composing anything, without
/// resolving a serializer contract, and without reflecting over the type it answers with.
/// </para>
/// <para>
/// Which to write is a question of what the query is. A document says exactly what goes over the
/// wire, which is what you want when the server's schema is the thing you are working against. A
/// chain keeps the query in C# — refactored with the type it queries, checked by the compiler
/// against it — which is what you want the rest of the time.
/// </para>
/// <para>
/// Both run under NativeAOT without qualification: there is no <c>Expression.Compile</c>, no
/// <c>MakeGenericType</c>, and nothing reflected. The document is a literal, the variables are
/// written by generated code, and the reply is read by a reader generated from the query's own
/// shape.
/// </para>
/// <para>
/// A method whose body is a chain may also await it and go on: <c>(await chain.ToArrayAsync(ct))
/// .Summarise()</c> compiles, with everything written around the await run over the rows where
/// they arrive. One await, of the chain and nothing else — a body that wraps the chain without
/// awaiting it is <c>FGQL015</c>, since the wrapper is code the replacement would drop.
/// </para>
/// <para>
/// A chain the compiler cannot compile — <c>FGQL015</c> says which and why — keeps its body and
/// keeps working, translated at run time as any other chain is. A document it cannot read is
/// <c>FGQL016</c>, and an error: a partial method has nothing to fall back to.
/// </para>
/// </remarks>
/// <param name="document">
/// The operation to send, for a query written as a document. Every variable it declares must have
/// a parameter of the same name on the method; the remaining parameters are the
/// <see cref="System.Net.Http.HttpClient"/> to post through and, optionally, a
/// <see cref="System.Threading.CancellationToken"/>.
/// </param>
[AttributeUsage(AttributeTargets.Method)]
[PublicAPI]
public sealed class GraphQLQueryAttribute([StringSyntax("GraphQL")] string? document = null) : Attribute
{
    /// <summary>
    /// The operation to send, or null when the method's body is the query.
    /// </summary>
    /// <remarks>
    /// Which of the two it is decides how the method is implemented, and the compiler reads that
    /// from here rather than from a second attribute: a query is one idea, and telling it in
    /// GraphQL or in LINQ is not two.
    /// </remarks>
    public string? Document { get; } = document;
}
