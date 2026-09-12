using System.Text.Json;

namespace Feather.GraphQL.Serialization;

/// <summary>
/// A reply, as the converter below builds it.
/// </summary>
/// <remarks>
/// Never crosses the transport seam. It is a pile of facts about one document — which field was
/// found, what kind of value it held, whether a wrapper was in the way — and every one of them
/// exists to answer a question the reader asks immediately afterwards. A transport is handed
/// rows, not this.
///
/// One type for both halves of the library, which is why it lives here. The LINQ execution path
/// needs the field name and the value kind to decide what the chain asked for; a compiled query
/// needs the rows and the count and nothing else. Reading a reply twice as two nearly-identical
/// shapes was the duplication this package exists to end.
/// </remarks>
internal sealed class ParsedReply<TElement>
{
    /// <summary>True when the reply carried a <c>data</c> object.</summary>
    public bool HasData { get; set; }

    /// <summary>True when the reply carried a non-empty <c>errors</c> array.</summary>
    public bool HasErrors { get; set; }

    /// <summary>The single field <c>data</c> carried, or null when it carried none.</summary>
    public string? Field { get; set; }

    /// <summary>What that field's value was: a list, a paging wrapper, or something else.</summary>
    public JsonValueKind Kind { get; set; }

    /// <summary>The member the rows were found under, when one was in the way.</summary>
    public string? Wrapper { get; set; }

    /// <summary>The connection's <c>totalCount</c>, when the reply carried one.</summary>
    public long? TotalCount { get; set; }

    /// <summary>The rows, in the array the serializer built them into.</summary>
    public TElement[]? Items { get; set; }

    /// <summary>
    /// The errors, when the payload could not be read and they explained why.
    /// </summary>
    /// <remarks>
    /// Only a reader that gave up part-way fills this: the LINQ path is told <em>that</em> the
    /// reply failed and rescans for the reasons, while a transport reading rows directly wants
    /// them here rather than making the same pass twice.
    /// </remarks>
    public Primitives.GraphQLError[]? Errors { get; set; }
}