using System.Text.Json;
using System.Text.Json.Serialization;
using Feather.GraphQL.Primitives;

namespace Feather.GraphQL.Http.Response;

/// <summary>
/// The shape of a GraphQL reply on the wire: <c>data</c>, <c>errors</c>, <c>extensions</c>.
/// </summary>
/// <remarks>
/// <para>
/// Internal, and a plain deserialization target. It carries nothing about the transport — status
/// and headers live on the <see cref="HttpResponseMessage"/>, where the caller already has them
/// and where they do not have to be invented for a JSON reader that never sees them.
/// </para>
/// <para>
/// <c>data</c> is read as a <see cref="JsonElement"/> rather than straight into the caller's
/// type, so that "the server sent no data" is a question with an exact answer. Deserializing
/// directly would leave a value type at its default — and <c>default(JsonElement)</c> is
/// indistinguishable from a legitimately empty one.
/// </para>
/// </remarks>
internal sealed class GraphQLResponseBody
{
    [JsonPropertyName("data")]
    public JsonElement Data { get; init; }

    [JsonPropertyName("errors")]
    public GraphQLError[]? Errors { get; init; }

    [JsonPropertyName("extensions")]
    public IReadOnlyDictionary<string, object>? Extensions { get; init; }

    /// <summary>True when the reply carried a <c>data</c> field with something in it.</summary>
    public bool HasData => Data.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null);
}
