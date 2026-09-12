using System.Text.Json;
using System.Text.Json.Serialization;

namespace Feather.GraphQL.Serialization;

/// <summary>
/// Reads a whole reply into <see cref="ParsedReply{TElement}"/> in one pass over the bytes.
/// </summary>
/// <remarks>
/// <para>
/// The root field's name is a runtime value, so no declared type can name it and the reply
/// cannot be deserialized into one. Reading it as a <see cref="JsonElement"/> first and pulling
/// the rows back out afterwards is what this replaces: at a thousand rows that was a document
/// build, a large-object-heap allocation and a second pass — about 250 µs spent arriving where
/// the first pass already was.
/// </para>
/// <para>
/// The rows themselves are read by handing the reader to the array's own converter rather than
/// by calling <see cref="JsonSerializer"/> again. That distinction is worth more than it looks:
/// re-entering the serializer with a reader costs about 110 µs per thousand rows, because a
/// reader may in general have more segments coming and the entry point has to allow for it.
/// Inside a converter the reader is already known to hold the whole document.
/// </para>
/// <para>
/// The envelope is read here too, rather than by a type wrapping this one, because a wrapper
/// would have to be closed over the element type to be deserialized in a single pass — and the
/// element type is not known until there is a plan.
/// </para>
/// </remarks>
internal sealed class ParsedReplyConverter<TElement> : JsonConverter<ParsedReply<TElement>>
{
    private JsonConverter<TElement[]>? _elements;

    public override ParsedReply<TElement> Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType is not JsonTokenType.StartObject)
            throw new JsonException("A GraphQL reply must be a JSON object.");

        var reply = new ParsedReply<TElement>();

        while (reader.Read() && reader.TokenType is JsonTokenType.PropertyName)
        {
            bool isData = reader.ValueTextEquals("data"u8);
            bool isErrors = !isData && reader.ValueTextEquals("errors"u8);

            reader.Read();

            if (isData)
                ReadData(ref reader, reply, options);
            else if (isErrors)
                ReadErrors(ref reader, reply);
            else
                reader.Skip();
        }

        return reply;
    }

    /// <summary>
    /// Reads the <c>data</c> object's single member: the root field the document asked for.
    /// </summary>
    /// <remarks>
    /// Its name is recorded rather than matched. What was asked for is in the operation, which
    /// the reader has and this does not — and keeping it that way is what lets one converter
    /// serve every query shape.
    /// </remarks>
    private void ReadData(ref Utf8JsonReader reader, ParsedReply<TElement> reply, JsonSerializerOptions options)
    {
        if (reader.TokenType is not JsonTokenType.StartObject)
        {
            reader.Skip();
            return;
        }

        reply.HasData = true;

        while (reader.Read() && reader.TokenType is JsonTokenType.PropertyName)
        {
            if (reply.Field is not null)
            {
                reader.Read();
                reader.Skip();
                continue;
            }

            reply.Field = reader.GetString();
            reader.Read();
            ReadField(ref reader, reply, options);
        }
    }

    /// <summary>Reads the root field's value: the rows, or the connection wrapping them.</summary>
    private void ReadField(ref Utf8JsonReader reader, ParsedReply<TElement> reply, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.StartArray:
                reply.Kind = JsonValueKind.Array;
                reply.Items = Elements(ref reader, reply, options);
                return;

            case JsonTokenType.StartObject:
                reply.Kind = JsonValueKind.Object;
                ReadConnection(ref reader, reply, options);
                return;

            case JsonTokenType.Null:
                reply.Kind = JsonValueKind.Null;
                return;

            default:
                // Recorded rather than rejected, so the reader can name the field and its kind in
                // the diagnostic the way it always has.
                reply.Kind = reader.TokenType switch
                {
                    JsonTokenType.String => JsonValueKind.String,
                    JsonTokenType.Number => JsonValueKind.Number,
                    JsonTokenType.True => JsonValueKind.True,
                    _ => JsonValueKind.False
                };

                reader.Skip();
                return;
        }
    }

    /// <summary>
    /// Reads a paging wrapper: the rows under <c>nodes</c> or <c>items</c>, and
    /// <c>totalCount</c> when the connection exposes it.
    /// </summary>
    /// <remarks>
    /// Both spellings are accepted, and which one was found is recorded. The two are exactly
    /// what distinguishes cursor paging from offset paging on the wire, so a reply that used the
    /// other one is the mismatch worth reporting — by the reader, which knows what was declared.
    /// </remarks>
    private void ReadConnection(ref Utf8JsonReader reader, ParsedReply<TElement> reply, JsonSerializerOptions options)
    {
        while (reader.Read() && reader.TokenType is JsonTokenType.PropertyName)
        {
            bool nodes = reader.ValueTextEquals("nodes"u8);
            bool items = !nodes && reader.ValueTextEquals("items"u8);
            bool total = !nodes && !items && reader.ValueTextEquals("totalCount"u8);

            reader.Read();

            if ((nodes || items) && reader.TokenType is JsonTokenType.StartArray)
            {
                reply.Wrapper = nodes ? "nodes" : "items";
                reply.Items = Elements(ref reader, reply, options);
            }
            else if (total && reader.TokenType is JsonTokenType.Number)
            {
                reply.TotalCount = reader.GetInt64();
            }
            else
            {
                reader.Skip();
            }
        }
    }

    /// <summary>
    /// Reads the rows, unless the reply has already said they are not worth reading.
    /// </summary>
    /// <remarks>
    /// Servers conventionally put <c>errors</c> before <c>data</c>, which makes this the usual
    /// case rather than a rare one: a failed query costs a tokenize of whatever partial payload
    /// came with it, and no deserialization at all.
    /// </remarks>
    private TElement[]? Elements(ref Utf8JsonReader reader, ParsedReply<TElement> reply, JsonSerializerOptions options)
    {
        if (reply.HasErrors)
        {
            reader.Skip();
            return null;
        }

        // Resolved once per options instance, which is once per process: a converter is itself
        // cached against the options it was created for.
        _elements ??= (JsonConverter<TElement[]>)options.GetConverter(typeof(TElement[]));

        return _elements.Read(ref reader, typeof(TElement[]), options);
    }

    /// <summary>Notes that the reply failed, and drops any rows read before it said so.</summary>
    private static void ReadErrors(ref Utf8JsonReader reader, ParsedReply<TElement> reply)
    {
        if (reader.TokenType is JsonTokenType.StartArray)
        {
            // A copy, so emptiness can be answered without moving the reader off the array it
            // still has to be skipped from. Utf8JsonReader is a struct; this is the whole trick.
            var peek = reader;

            if (peek.Read() && peek.TokenType is not JsonTokenType.EndArray)
            {
                reply.HasErrors = true;
                reply.Items = null;
            }
        }

        reader.Skip();
    }

    public override void Write(Utf8JsonWriter writer, ParsedReply<TElement> value, JsonSerializerOptions options)
        => throw new NotSupportedException("A reply is read, never written.");
}