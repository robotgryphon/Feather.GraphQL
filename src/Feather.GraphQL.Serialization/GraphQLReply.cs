using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Feather.GraphQL.Primitives;
using JetBrains.Annotations;

namespace Feather.GraphQL.Serialization;

/// <summary>
/// The shape of a GraphQL reply on the wire: <c>data</c>, as the caller's own type, and the
/// <c>errors</c> that may have come instead of it.
/// </summary>
/// <remarks>
/// <para>
/// It carries nothing about the transport. Status and headers live on the
/// <see cref="HttpResponseMessage"/>, where the caller already has them and where they do not
/// have to be invented for a JSON reader that never sees them.
/// </para>
/// <para>
/// Generic in the payload on purpose, so that the whole reply is one deserialization. Declaring
/// <c>data</c> as a <see cref="JsonElement"/> and deserializing it into the caller's type
/// afterwards — the obvious way to write this — reads the reply three times over: once into a
/// document, again to copy the <c>data</c> subtree into a document of its own, and a third time
/// after writing that subtree back out to UTF-8. At a thousand rows that costs around 380 µs and
/// puts the reply on the large object heap.
/// </para>
/// </remarks>
/// <remarks>
/// The setters are settable rather than <c>init</c> because the contract for this type is built
/// by hand — see <c>GraphQLReplyContract</c> — and a metadata setter is an ordinary delegate,
/// which cannot reach an init-only property.
/// </remarks>
internal sealed class GraphQLReply<TData>
{
    [JsonPropertyName("data")]
    public TData? Data { get; set; }

    [JsonPropertyName("errors")]
    public GraphQLError[]? Errors { get; set; }
}
