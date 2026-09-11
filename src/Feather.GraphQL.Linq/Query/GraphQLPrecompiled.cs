using System.ComponentModel;
using JetBrains.Annotations;

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
}
