using System.Text.Json;
using System.Text.Json.Serialization;
using Feather.GraphQL.Primitives;

namespace Feather.GraphQL.Http.Response;

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
internal sealed class GraphQLReply<TData>
{
    [JsonPropertyName("data")]
    public TData? Data { get; init; }

    [JsonPropertyName("errors")]
    public GraphQLError[]? Errors { get; init; }
}

/// <summary>
/// Reads a reply out of the bytes a server sent.
/// </summary>
internal static class GraphQLResponseReader
{
    /// <summary>
    /// Reads the reply.
    /// </summary>
    /// <remarks>
    /// An empty body reads as neither data nor errors rather than as malformed JSON, which is
    /// what a server that answered with nothing at all has actually said.
    /// </remarks>
    public static GraphQLReply<TData> Read<TData>(byte[] body)
    {
        if (body.Length == 0)
            return new GraphQLReply<TData>();

        try
        {
            // The span overload, not the one taking a reader: given the whole document at once
            // the serializer takes a single-buffer path that a Utf8JsonReader — which must allow
            // for more segments to come — cannot, and which is worth about a third of the time
            // spent here on a large reply.
            return JsonSerializer.Deserialize<GraphQLReply<TData>>(body, JsonSerializerOptions.Web)
                ?? new GraphQLReply<TData>();
        }
        catch (JsonException) when (Errors(body) is { Length: > 0 } errors)
        {
            // The payload did not fit the caller's type, and the reply also carried errors —
            // which explain why, and are the better thing to report. Reached only when a server
            // sent them after data: errors sent first are read before the payload is, and the
            // payload never throws in isolation once it has been read.
            return new GraphQLReply<TData> { Errors = errors };
        }
    }

    /// <summary>
    /// Rescans a reply for its <c>errors</c>, ignoring everything else in it.
    /// </summary>
    /// <remarks>
    /// A second pass, and deliberately so: it runs only on a reply that was already going to
    /// fail. Buying this guarantee with a pass over every successful reply — scanning the
    /// envelope first and reading the payload after — is what this reader exists to avoid, and
    /// costs more than the payload-shaped reads it protects.
    /// </remarks>
    private static GraphQLError[]? Errors(byte[] body)
    {
        try
        {
            var reader = new Utf8JsonReader(body);

            if (!reader.Read() || reader.TokenType is not JsonTokenType.StartObject)
                return null;

            while (reader.Read() && reader.TokenType is JsonTokenType.PropertyName)
            {
                bool isErrors = reader.ValueTextEquals("errors"u8);

                reader.Read();

                if (!isErrors)
                {
                    reader.Skip();
                    continue;
                }

                return JsonSerializer.Deserialize<GraphQLError[]>(ref reader, JsonSerializerOptions.Web);
            }
        }
        catch (JsonException)
        {
            // The body is malformed past the point the payload failed at. The caller's own
            // exception describes that, and is the one worth raising.
        }

        return null;
    }
}
