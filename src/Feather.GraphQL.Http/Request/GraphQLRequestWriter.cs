using System.Buffers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Feather.GraphQL.Http.Request;

/// <summary>
/// Writes a request as the UTF-8 bytes that go on the wire.
/// </summary>
/// <remarks>
/// <para>
/// Written straight out rather than serialized. The envelope has four members and this assembly
/// knows all of them, so there is nothing for a contract to describe: no type to resolve, no
/// naming policy to apply, no reflection and nothing for NativeAOT to be unable to see.
/// </para>
/// <para>
/// The members that carry nothing are omitted rather than written as null. Both are legal, but
/// a shorter body is a shorter body — and a server that treats a present <c>operationName</c> of
/// null as "run the operation called null" is a real thing to have been sending.
/// </para>
/// </remarks>
internal static class GraphQLRequestWriter
{
    /// <summary>Serializes the request.</summary>
    /// <remarks>
    /// Eagerly, into an array, rather than onto the request stream as it is sent. That gives the
    /// content a known length — so a server sees <c>Content-Length</c> rather than a chunked body
    /// — and keeps the cost where it can be seen, both by a profiler and by a benchmark whose
    /// transport would otherwise never read the body at all.
    /// </remarks>
    public static byte[] ToUtf8(GraphQLRequest request)
    {
        using var buffer = new PooledBufferWriter(512);

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("query"u8, request.Query);

            if (request.OperationName is { Length: > 0 } operation)
                writer.WriteString("operationName"u8, operation);

            if (request.Variables is { IsEmpty: false } variables)
            {
                writer.WritePropertyName("variables"u8);
                variables.WriteTo(writer);
            }

            WriteMap(writer, "extensions"u8, request.Extensions);

            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteMap(
        Utf8JsonWriter writer,
        ReadOnlySpan<byte> name,
        IReadOnlyDictionary<string, object?>? map)
    {
        if (map is not { Count: > 0 })
            return;

        writer.WritePropertyName(name);
        writer.WriteStartObject();

        foreach (var (key, value) in map)
        {
            writer.WritePropertyName(key);

            // Extensions only, and they are whatever a caller put in them — so this is the one
            // place left that has to ask an object what it is before it can write it.
            switch (value)
            {
                case null:
                    writer.WriteNullValue();
                    break;

                case JsonNode node:
                    node.WriteTo(writer);
                    break;

                default:
                    JsonSerializer.Serialize(writer, value, value.GetType(), JsonSerializerOptions.Web);
                    break;
            }
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// An <see cref="IBufferWriter{T}"/> over a rented array.
    /// </summary>
    /// <remarks>
    /// <see cref="ArrayBufferWriter{T}"/> would do, but it allocates its own array and grows by
    /// allocating another. A request body is written and immediately copied into the content, so
    /// nothing it was written through needs to outlive the call.
    /// </remarks>
    private sealed class PooledBufferWriter(int capacity) : IBufferWriter<byte>, IDisposable
    {
        private byte[] _buffer = ArrayPool<byte>.Shared.Rent(capacity);
        private int _written;

        public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);

        public void Advance(int count) => _written += count;

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            Ensure(sizeHint);

            return _buffer.AsMemory(_written);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            Ensure(sizeHint);

            return _buffer.AsSpan(_written);
        }

        public void Dispose() => ArrayPool<byte>.Shared.Return(_buffer);

        private void Ensure(int sizeHint)
        {
            if (sizeHint < 1)
                sizeHint = 1;

            if (_buffer.Length - _written >= sizeHint)
                return;

            byte[] larger = ArrayPool<byte>.Shared.Rent(Math.Max(_buffer.Length * 2, _written + sizeHint));
            _buffer.AsSpan(0, _written).CopyTo(larger);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = larger;
        }
    }
}
