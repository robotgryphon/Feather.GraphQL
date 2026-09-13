using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using JetBrains.Annotations;

namespace Feather.GraphQL.Serialization;

/// <summary>
/// A request body under construction, in pooled memory.
/// </summary>
/// <remarks>
/// <para>
/// A compiled query's body is almost entirely constant: everything up to the first variable value
/// — the envelope, the document, the property names — is known when the query is compiled and can
/// be a UTF-8 literal in the generated code. What is left to do per request is copy those literals
/// and write the values between them, which is what this is for.
/// </para>
/// <para>
/// That is why the raw and the value writers are named differently. <see cref="WriteRaw"/> copies
/// bytes that are already JSON; the <c>Write</c> overloads turn a CLR value into JSON. Confusing
/// the two would put an unescaped string on the wire, so they do not share a name.
/// </para>
/// <para>
/// The buffer is rented and must be given back — but the bytes are only valid until it is, so a
/// body is disposed after the request completes rather than after it is built.
/// </para>
/// <para>
/// It sits beside the reply readers rather than in the transport because both halves of the
/// library write requests: the HTTP package builds a body from a template, and the generated
/// filter of a precompiled plan builds one with no transport in sight.
/// </para>
/// </remarks>
[PublicAPI]
public sealed class PooledBody : IDisposable
{
    /// <summary>The longest a single byte can become once escaped: <c>\uXXXX</c>.</summary>
    private const int LongestEscape = 6;

    private byte[] _buffer;
    private int _written;
    private bool _returned;

    private PooledBody(int capacity) => _buffer = ArrayPool<byte>.Shared.Rent(capacity);

    /// <summary>Takes a body from the pool.</summary>
    /// <param name="capacity">
    /// What the body is expected to need. A generated caller knows the length of its own constant
    /// parts and can say so, which is the difference between renting once and renting twice.
    /// </param>
    public static PooledBody Rent(int capacity = 512)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        return new PooledBody(capacity);
    }

    /// <summary>
    /// What has been written so far.
    /// </summary>
    /// <remarks>
    /// Valid until <see cref="Dispose"/>, and not after. It is handed to the transport as the
    /// request content without being copied, which is the point of the type.
    /// </remarks>
    public ReadOnlyMemory<byte> Written => _buffer.AsMemory(0, _written);

    /// <summary>Copies bytes that are already JSON.</summary>
    /// <remarks>
    /// Nothing is validated or escaped. This is for the parts a compiler wrote, which are correct
    /// by construction — never for anything that came from a caller at run time.
    /// </remarks>
    public void WriteRaw(ReadOnlySpan<byte> json)
    {
        Ensure(json.Length);

        json.CopyTo(_buffer.AsSpan(_written));
        _written += json.Length;
    }

    /// <summary>Writes a string as a JSON string, escaped.</summary>
    /// <remarks>
    /// Escaped by the same encoder <c>Utf8JsonWriter</c> uses, rather than by a table written
    /// here: what belongs in this type is where the bytes go, not which of them are dangerous.
    /// </remarks>
    public void Write(string? value)
    {
        if (value is null)
        {
            WriteRaw("null"u8);

            return;
        }

        // Room for the text as it stands, before knowing whether any of it escapes. The common
        // case is that none of it does, and then this is the only reservation made.
        Ensure(Encoding.UTF8.GetMaxByteCount(value.Length) + 2);

        _buffer[_written++] = (byte)'"';

        int raw = Encoding.UTF8.GetBytes(value, _buffer.AsSpan(_written));

        if (JavaScriptEncoder.Default.FindFirstCharacterToEncodeUtf8(_buffer.AsSpan(_written, raw)) < 0)
            _written += raw;
        else
            Escape(raw);

        Ensure(1);

        _buffer[_written++] = (byte)'"';
    }

    /// <summary>Writes a boolean.</summary>
    public void Write(bool value) => WriteRaw(value ? "true"u8 : "false"u8);

    /// <summary>Writes a 32-bit integer.</summary>
    public void Write(int value) => Format(value);

    /// <summary>Writes a 64-bit integer.</summary>
    public void Write(long value) => Format(value);

    /// <summary>Writes a double.</summary>
    public void Write(double value) => Format(value);

    /// <summary>Writes a decimal.</summary>
    public void Write(decimal value) => Format(value);

    /// <summary>Writes a GUID, as the JSON string a server expects.</summary>
    public void Write(Guid value) => Quoted(value, "D");

    /// <summary>Writes a date and time, as round-trip text.</summary>
    public void Write(DateTime value) => Quoted(value, "O");

    /// <inheritdoc cref="Write(DateTime)"/>
    public void Write(DateTimeOffset value) => Quoted(value, "O");

    /// <inheritdoc cref="Write(DateTime)"/>
    public void Write(DateOnly value) => Quoted(value, "O");

    /// <inheritdoc cref="Write(DateTime)"/>
    public void Write(TimeOnly value) => Quoted(value, "O");

    /// <summary>Writes a JSON null.</summary>
    public void WriteNull() => WriteRaw("null"u8);

    /// <summary>Gives the buffer back.</summary>
    /// <remarks>
    /// Idempotent, because a body is disposed on a path that may also have disposed it already —
    /// and returning one array to the pool twice hands the same memory to two callers.
    /// </remarks>
    public void Dispose()
    {
        if (_returned)
            return;

        _returned = true;
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = [];
        _written = 0;
    }

    /// <summary>
    /// Escapes the raw bytes just transcoded, which are sitting unclaimed past the write point.
    /// </summary>
    /// <remarks>
    /// They are copied out before the buffer is grown, because growing it is what would move them
    /// — and they are the input to the encoding that is about to overwrite them.
    /// </remarks>
    private void Escape(int raw)
    {
        byte[] scratch = ArrayPool<byte>.Shared.Rent(raw);

        try
        {
            _buffer.AsSpan(_written, raw).CopyTo(scratch);

            Ensure(raw * LongestEscape);

            var status = JavaScriptEncoder.Default.EncodeUtf8(
                scratch.AsSpan(0, raw),
                _buffer.AsSpan(_written),
                out int consumed,
                out int written,
                isFinalBlock: true);

            if (status != OperationStatus.Done || consumed != raw)
                throw new InvalidOperationException("A value could not be encoded as JSON text.");

            _written += written;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    /// <summary>Writes a value that formats itself as UTF-8, growing until it fits.</summary>
    /// <remarks>
    /// Invariant culture always: a decimal point is a decimal point on the wire, whatever the
    /// thread happens to be set to.
    /// </remarks>
    private void Format<T>(T value, ReadOnlySpan<char> format = default)
        where T : IUtf8SpanFormattable
    {
        while (true)
        {
            if (value.TryFormat(_buffer.AsSpan(_written), out int written, format, CultureInfo.InvariantCulture))
            {
                _written += written;

                return;
            }

            Ensure(Math.Max(_buffer.Length - _written + 1, 64));
        }
    }

    /// <inheritdoc cref="Format{T}"/>
    private void Quoted<T>(T value, ReadOnlySpan<char> format)
        where T : IUtf8SpanFormattable
    {
        Ensure(1);
        _buffer[_written++] = (byte)'"';

        Format(value, format);

        Ensure(1);
        _buffer[_written++] = (byte)'"';
    }

    private void Ensure(int more)
    {
        ObjectDisposedException.ThrowIf(_returned, this);

        if (_buffer.Length - _written >= more)
            return;

        byte[] larger = ArrayPool<byte>.Shared.Rent(Math.Max(_buffer.Length * 2, _written + more));

        _buffer.AsSpan(0, _written).CopyTo(larger);
        ArrayPool<byte>.Shared.Return(_buffer);

        _buffer = larger;
    }
}
