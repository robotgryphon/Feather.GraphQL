using System.Buffers;

namespace Feather.GraphQL.Http.Response;

/// <summary>
/// A reply's bytes, held in a pooled buffer for as long as it takes to read them.
/// </summary>
/// <remarks>
/// <para>
/// The reply has to be buffered before it is parsed — handed one span, the serializer is
/// substantially faster than it can be over a stream whose segments arrive one at a time — but
/// buffering it with <c>ReadAsByteArrayAsync</c> allocates an array the size of the reply on
/// every request. At a thousand rows that is 160 KB of garbage per query, for a buffer nothing
/// outlives the read.
/// </para>
/// <para>
/// Nothing may hold the span past the read. The one shape that would is a
/// <see cref="System.Text.Json.JsonElement"/>, and it does not: deserializing one copies the
/// bytes into a document of its own.
/// </para>
/// </remarks>
internal readonly struct ResponseBuffer : IDisposable
{
    private readonly byte[]? _rented;

    private ResponseBuffer(ReadOnlyMemory<byte> bytes, byte[]? rented)
    {
        Bytes = bytes;
        _rented = rented;
    }

    /// <summary>The bytes that were read.</summary>
    public ReadOnlyMemory<byte> Bytes { get; }

    /// <summary>Returns the rental, if this was one. Borrowed bytes are nobody's to free.</summary>
    public void Dispose()
    {
        if (_rented is not null)
            ArrayPool<byte>.Shared.Return(_rented);
    }

    /// <summary>Reads a response's content into a pooled buffer.</summary>
    /// <remarks>
    /// <c>Content-Length</c> sizes the rental when the server sent one, which is the usual case
    /// and the one where the buffer is filled by a single read. Without it the buffer grows by
    /// doubling, as a list would.
    /// </remarks>
    public static async ValueTask<ResponseBuffer> ReadAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        // Already an array behind the stream: borrow it rather than copying it. Content that
        // arrived over a socket is not, so this is the in-memory case — a test double, or a
        // caller who asked HttpClient to buffer the reply before handing it over.
        if (stream is MemoryStream buffered && buffered.TryGetBuffer(out var segment))
            return new ResponseBuffer(segment.AsMemory(), rented: null);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(
            content.Headers.ContentLength is > 0 and <= int.MaxValue and var declared
                ? (int)declared
                : 4096);

        int written = 0;

        while (true)
        {
            if (written == buffer.Length)
            {
                byte[] larger = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                buffer.AsSpan(0, written).CopyTo(larger);
                ArrayPool<byte>.Shared.Return(buffer);
                buffer = larger;
            }

            int read = await stream
                .ReadAsync(buffer.AsMemory(written), cancellationToken)
                .ConfigureAwait(false);

            if (read == 0)
                return new ResponseBuffer(buffer.AsMemory(0, written), buffer);

            written += read;
        }
    }
}
