using System.ComponentModel;
using JetBrains.Annotations;

using Feather.GraphQL.Linq.Expressions;

namespace Feather.GraphQL.Linq.Query;

/// <summary>
/// The seam generated interceptors call. Not part of the surface anyone writes against.
/// </summary>
/// <remarks>
/// <para>
/// Public because an interceptor is emitted into the <em>consumer's</em> assembly, so it can only
/// reach public members — <c>InternalsVisibleTo</c> cannot name a package's consumers. Hidden
/// from IntelliSense instead, which is as close to internal as this can get.
/// </para>
/// <para>
/// Calling it by hand is not dangerous so much as pointless: a document that does not match the
/// chain would produce a request for fields the materializer is not expecting.
/// </para>
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
[PublicAPI]
public static class GraphQLPrecompiled
{
    private static int _attached;

    /// <summary>
    /// How many chains have run with a document the compiler printed.
    /// </summary>
    /// <remarks>
    /// A precompiled document is byte-identical to the one the runtime would have produced, so
    /// nothing about the request says which path printed it. The count is the only observable
    /// difference, and it is what the tests assert on — the same reason
    /// <c>PartialEvaluator.CompiledSubtrees</c> exists.
    /// </remarks>
    internal static int Attachments => Volatile.Read(ref _attached);

    /// <summary>
    /// Hands a chain the document the compiler printed for it.
    /// </summary>
    /// <remarks>
    /// Silently does nothing for a queryable that is not one of this library's. The generator
    /// only emits this call for chains it traced to an entry point, so that cannot happen from
    /// generated code; it keeps a hand-written call from throwing.
    /// </remarks>
    /// <param name="source">The queryable the chain starts at.</param>
    /// <param name="document">The printed, parameterized document.</param>
    /// <returns><paramref name="source"/>, unchanged.</returns>
    public static IQueryable<T> Attach<T>(IQueryable<T> source, string document)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.Provider is GraphQLQueryProvider provider)
        {
            provider.PrecompiledDocument = document;
            Interlocked.Increment(ref _attached);
        }

        return source;
    }

    /// <summary>
    /// Hands a chain its whole plan, for a chain that binds nothing at runtime.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A document alone still leaves the chain to be walked on every execution, to find the
    /// element type, the root field, the paging kind and the terminal operator. None of those
    /// depend on a value, so for a chain that binds none the walk produces a constant — and the
    /// compiler can produce it instead.
    /// </para>
    /// <para>
    /// The numbers are <c>PagingKind</c> and the internal result operator. The generator mirrors
    /// both and a test asserts the two still agree, because a reordering would make every
    /// precompiled plan quietly wrong rather than loudly broken.
    /// </para>
    /// </remarks>
    /// <param name="source">The queryable the chain starts at.</param>
    /// <param name="document">The printed document. It binds no variables.</param>
    /// <param name="rootField">The field the query reads from.</param>
    /// <param name="paging">The <c>PagingKind</c> the root field was declared with.</param>
    /// <param name="resultOperator">The terminal that reduces the sequence.</param>
    public static IQueryable<T> AttachPlan<T>(
        IQueryable<T> source,
        string document,
        string rootField,
        int paging,
        int resultOperator)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.Provider is GraphQLQueryProvider provider)
        {
            provider.PrecompiledPlan = new GraphQLQueryPlan(
                document,
                GraphQLQueryPlan.NoVariables,
                typeof(T),
                rootField,
                (PagingKind)paging,
                Projection: null,
                (QueryResultOperator)resultOperator);

            Interlocked.Increment(ref _attached);
        }

        return source;
    }
}
