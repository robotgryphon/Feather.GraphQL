using System.Text.Json;
using System.Text.Json.Serialization;
using Feather.GraphQL.Metadata;

namespace Feather.GraphQL.Linq.Execution;

/// <summary>
/// Yields a reply's rows one at a time, deserializing each only when it is asked for.
/// </summary>
/// <remarks>
/// <para>
/// Streamed out of the reply's bytes rather than off the socket, and the distinction is worth
/// being honest about: this does not shorten the wait for the first row, because the reply is
/// buffered — into pooled memory — before any of it is read. What it does give is the two things
/// that actually cost something. The rows never all exist at once, so a caller that processes
/// and discards them holds one at a time instead of the whole sequence. And abandoning the
/// sequence stops the work: rows past the last one asked for are never deserialized, which over
/// a thousand rows is most of the cost of the query.
/// </para>
/// <para>
/// Reading off the socket was the obvious alternative and does not work.
/// <c>DeserializeAsyncEnumerable</c> streams a <em>top-level</em> array and then insists on the
/// end of the document, so handed the tail of a reply it reads the rows and throws on the
/// <c>}</c> that closes <c>data</c>. Driving a reader across buffer boundaries by hand avoids
/// that, but has to prove each element is complete before deserializing it — a tokenize per row
/// on top of the read, which costs more than the buffering it saves.
/// </para>
/// <para>
/// Each row is read through a fresh <see cref="Utf8JsonReader"/> over what is left of the reply.
/// A reader is a ref struct and cannot live in an iterator at all, let alone across a yield, so
/// the position is carried as an index and the reader is rebuilt per row — which costs nothing
/// measurable and keeps every row on the single-span fast path.
/// </para>
/// </remarks>
internal static class GraphQLReplyStream
{
    public static IAsyncEnumerable<TElement> Rows<TElement>(
        ReadOnlyMemory<byte> reply,
        GraphQLOperation operation,
        CancellationToken cancellationToken)
    {
        var options = GraphQLJsonContextRegistry.Options;

        // Located once, before anything is yielded, so a reply that failed or named the wrong
        // field raises at the first MoveNext rather than partway through the rows.
        int start = Locate(reply.Span, operation);

        return new RowSequence<TElement>(
            reply,
            start,
            (JsonConverter<TElement>)options.GetConverter(typeof(TElement)),
            options,
            cancellationToken);
    }

    /// <summary>
    /// Walks to the first row, returning its index — or the reply's length when there are none.
    /// </summary>
    private static int Locate(ReadOnlySpan<byte> reply, GraphQLOperation operation)
    {
        if (reply.IsEmpty)
            throw new GraphQLReplyFailedException(carriedErrors: false);

        var reader = new Utf8JsonReader(reply);

        if (!reader.Read() || reader.TokenType is not JsonTokenType.StartObject)
            throw new JsonException("A GraphQL reply must be a JSON object.");

        bool hasData = false;
        int start = -1;

        while (reader.Read() && reader.TokenType is JsonTokenType.PropertyName)
        {
            bool isData = reader.ValueTextEquals("data"u8);
            bool isErrors = !isData && reader.ValueTextEquals("errors"u8);

            reader.Read();

            if (isErrors)
            {
                var peek = reader;

                if (peek.Read() && peek.TokenType is not JsonTokenType.EndArray)
                    throw new GraphQLReplyFailedException(carriedErrors: true);

                reader.Skip();
                continue;
            }

            if (!isData)
            {
                reader.Skip();
                continue;
            }

            if (reader.TokenType is not JsonTokenType.StartObject)
            {
                reader.Skip();
                continue;
            }

            hasData = true;
            start = InData(ref reader, operation, reply.Length);
        }

        if (!hasData)
            throw new GraphQLReplyFailedException(carriedErrors: false);

        return start;
    }

    /// <summary>Walks the <c>data</c> object to the root field's rows.</summary>
    private static int InData(ref Utf8JsonReader reader, GraphQLOperation operation, int end)
    {
        int start = -1;
        bool found = false;

        while (reader.Read() && reader.TokenType is JsonTokenType.PropertyName)
        {
            if (found)
            {
                reader.Read();
                reader.Skip();
                continue;
            }

            if (!reader.ValueTextEquals(operation.RootField))
                throw new GraphQLTranslationException("FGQL018",
                    $"The response has no '{operation.RootField}' field.");

            found = true;
            reader.Read();
            start = AtField(ref reader, operation, end);
        }

        return found
            ? start
            : throw new GraphQLTranslationException("FGQL018",
                $"The response has no '{operation.RootField}' field.");
    }

    /// <summary>Reads the root field's value: the rows, or the connection wrapping them.</summary>
    private static int AtField(ref Utf8JsonReader reader, GraphQLOperation operation, int end)
    {
        if (reader.TokenType is JsonTokenType.Null)
        {
            reader.Skip();
            return end;
        }

        if (operation.Wrapper is not { } wrapper)
        {
            if (reader.TokenType is not JsonTokenType.StartArray)
                throw new GraphQLTranslationException("FGQL018",
                    $"'{operation.RootField}' returned {Kind(reader.TokenType)}, not a list of elements.");

            int start = (int)reader.TokenStartIndex + 1;
            reader.Skip();

            return start;
        }

        if (reader.TokenType is not JsonTokenType.StartObject)
            throw new GraphQLTranslationException("FGQL018",
                $"'{operation.RootField}' returned no '{wrapper}'. The declared PagingKind."
                + $"{operation.Paging} does not match how the server pages this field.");

        return InConnection(ref reader, operation, wrapper, end);
    }

    /// <summary>Walks a connection to its <c>nodes</c> or <c>items</c>.</summary>
    private static int InConnection(
        ref Utf8JsonReader reader,
        GraphQLOperation operation,
        string wrapper,
        int end)
    {
        int start = -1;

        while (reader.Read() && reader.TokenType is JsonTokenType.PropertyName)
        {
            bool isWrapper = start < 0 && reader.ValueTextEquals(wrapper);

            reader.Read();

            if (!isWrapper)
            {
                reader.Skip();
                continue;
            }

            if (reader.TokenType is JsonTokenType.Null)
            {
                start = end;
                continue;
            }

            start = (int)reader.TokenStartIndex + 1;
            reader.Skip();
        }

        return start >= 0
            ? start
            : throw new GraphQLTranslationException("FGQL018",
                $"'{operation.RootField}' returned no '{wrapper}'. The declared PagingKind."
                + $"{operation.Paging} does not match how the server pages this field.");
    }

    private static JsonValueKind Kind(JsonTokenType token) => token switch
    {
        JsonTokenType.StartObject => JsonValueKind.Object,
        JsonTokenType.String => JsonValueKind.String,
        JsonTokenType.Number => JsonValueKind.Number,
        JsonTokenType.True => JsonValueKind.True,
        _ => JsonValueKind.False
    };

    /// <summary>
    /// The rows, as a sequence that reads one only when it is asked for.
    /// </summary>
    /// <remarks>
    /// Hand-written rather than an <c>async</c> iterator because nothing here awaits: the bytes
    /// are already in hand, and a state machine around a synchronous read would be pure overhead
    /// on the per-row path. <see cref="IAsyncEnumerable{T}"/> is the shape the caller wants, not
    /// a claim that the work is asynchronous.
    /// </remarks>
    private sealed class RowSequence<TElement>(
        ReadOnlyMemory<byte> reply,
        int start,
        JsonConverter<TElement> converter,
        JsonSerializerOptions options,
        CancellationToken bound) : IAsyncEnumerable<TElement>
    {
        public IAsyncEnumerator<TElement> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            => new Enumerator(
                reply,
                start,
                converter,
                options,
                cancellationToken == default ? bound : cancellationToken);

        private sealed class Enumerator(
            ReadOnlyMemory<byte> reply,
            int start,
            JsonConverter<TElement> converter,
            JsonSerializerOptions options,
            CancellationToken cancellationToken) : IAsyncEnumerator<TElement>
        {
            private int _index = start;
            private bool _done;

            public TElement Current { get; private set; } = default!;

            public ValueTask<bool> MoveNextAsync()
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (_done || _index >= reply.Length)
                    return new ValueTask<bool>(false);

                if (!TryRead(reply.Span, ref _index, converter, options, out var row))
                {
                    _done = true;
                    return new ValueTask<bool>(false);
                }

                Current = row;
                return new ValueTask<bool>(true);
            }

            public ValueTask DisposeAsync() => default;
        }

        /// <summary>
        /// Reads the row at <paramref name="index"/>, leaving it on the one after.
        /// </summary>
        /// <remarks>
        /// Separate from the enumerator because a <see cref="Utf8JsonReader"/> is a ref struct
        /// and cannot be a field. Between rows the position is just an index, which is all the
        /// state a JSON array needs: the separators between values are unambiguous bytes, so
        /// stepping over them takes no parsing at all.
        /// </remarks>
        private static bool TryRead(
            ReadOnlySpan<byte> reply,
            ref int index,
            JsonConverter<TElement> converter,
            JsonSerializerOptions options,
            out TElement row)
        {
            row = default!;

            while (index < reply.Length)
            {
                byte current = reply[index];

                if (current is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)',')
                {
                    index++;
                    continue;
                }

                if (current is (byte)']')
                    return false;

                break;
            }

            if (index >= reply.Length)
                return false;

            var reader = new Utf8JsonReader(reply[index..]);

            if (!reader.Read())
                return false;

            row = converter.Read(ref reader, typeof(TElement), options)!;
            index += (int)reader.BytesConsumed;

            return true;
        }
    }
}
