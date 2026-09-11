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
internal readonly struct ResponseBuffer(byte[] buffer, int length) : IDisposable
{
    /// <summary>The bytes that were read.</summary>
    public ReadOnlyMemory<byte> Bytes { get; } = buffer.AsMemory(0, length);

    public void Dispose() => ArrayPool<byte>.Shared.Return(buffer);

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
                return new ResponseBuffer(buffer, written);

            written += read;
        }
    }
}
