using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Feather.GraphQL.Linq.Metadata;

namespace Feather.GraphQL.Linq.Execution;

/// <summary>
/// Reads the rows out of a server's reply.
/// </summary>
/// <remarks>
/// <para>
/// The reading half of a transport's job, written once here rather than once per transport. A
/// transport sends bytes and receives bytes; turning the second lot into rows is the same work
/// whatever carried them, and it is work that has to know the shape the query asked for — which
/// is what the <see cref="GraphQLOperation"/> says.
/// </para>
/// <para>
/// Every check the reply has to pass lives here, so the seam can carry rows and nothing else. A
/// reply with no rows to give raises <see cref="GraphQLReplyFailedException"/>, which the
/// transport turns into its own exception.
/// </para>
/// <para>
/// Internal, and shared with the <c>HttpClient</c> transport rather than published. It is how
/// the one transport in this repository reads a reply, not a contract anything depends on: a
/// transport written elsewhere implements the seam and reads replies however it likes. Exposing
/// this would have meant publishing the failure signal with it, and two more types on the public
/// surface to describe an implementation detail of the transport that ships beside it.
/// </para>
/// </remarks>
internal static class GraphQLReplyReader
{
    /// <summary>Reads the rows the root field carried.</summary>
    /// <exception cref="GraphQLReplyFailedException">
    /// The reply reported errors, or carried no <c>data</c>.
    /// </exception>
    /// <exception cref="GraphQLTranslationException">
    /// <c>FGQL018</c>: the reply is not the shape the query asked for.
    /// </exception>
    public static IReadOnlyList<TElement> ReadRows<TElement>(
        ReadOnlySpan<byte> reply,
        GraphQLOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var parsed = Parse<TElement>(reply);

        RequireField(parsed, operation);

        if (parsed.Kind is JsonValueKind.Null or JsonValueKind.Undefined)
            return [];

        if (operation.Wrapper is { } wrapper)
        {
            if (parsed.Wrapper != wrapper)
                throw new GraphQLTranslationException("FGQL018",
                    $"'{operation.RootField}' returned no '{wrapper}'. The declared PagingKind."
                    + $"{operation.Paging} does not match how the server pages this field.");
        }
        else if (parsed.Kind is not JsonValueKind.Array)
        {
            throw new GraphQLTranslationException("FGQL018",
                $"'{operation.RootField}' returned {parsed.Kind}, not a list of elements.");
        }

        return parsed.Items ?? [];
    }

    /// <summary>Reads the <c>totalCount</c> a count query asked the connection for.</summary>
    /// <remarks>
    /// Its own method because a count is its own question: the document selects
    /// <c>totalCount</c> and no rows at all, so there is nothing for
    /// <see cref="ReadRows{TElement}"/> to return and no element type to read one as.
    /// </remarks>
    /// <exception cref="GraphQLTranslationException">
    /// <c>FGQL009</c>: the connection exposed no <c>totalCount</c>.
    /// </exception>
    public static long ReadCount(ReadOnlySpan<byte> reply, GraphQLOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        // The element type is irrelevant to a count, and the document selected no rows for one
        // to be read from; object names a contract that exists without claiming otherwise.
        var parsed = Parse<object>(reply);

        RequireField(parsed, operation);

        if (parsed.Kind is JsonValueKind.Null or JsonValueKind.Undefined)
            return 0;

        return parsed.TotalCount ?? throw new GraphQLTranslationException("FGQL009",
            $"'{operation.RootField}' returned no 'totalCount'. The server must expose it on the "
            + "connection for Count() to work — HotChocolate needs IncludeTotalCount on the "
            + "paging attribute.");
    }

    /// <summary>
    /// Streams the rows the root field carried, reading no further ahead than it has to.
    /// </summary>
    /// <remarks>
    /// The reply is still buffered whole, so this does not shorten the wait for the first row.
    /// What it gives is that the rows never all exist at once, and that abandoning the sequence
    /// stops the work — rows past the last one asked for are never deserialized.
    /// </remarks>
    public static IAsyncEnumerable<TElement> StreamRows<TElement>(
        ReadOnlyMemory<byte> reply,
        GraphQLOperation operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        return GraphQLReplyStream.Rows<TElement>(reply, operation, cancellationToken);
    }

    /// <summary>
    /// The contract one element type's replies are read through, resolved once.
    /// </summary>
    /// <remarks>
    /// Resolving it from the options on every query is not free — the serializer has to look it
    /// up before it can start — and this is the per-request path. Keyed on the options instance
    /// so that a late <c>JsonSerializerContext</c> registration, which replaces them, is picked
    /// up rather than ignored. The pair is one field so a racing reader sees both or neither;
    /// two threads resolving the same contract at once is harmless.
    /// </remarks>
    private static class Contract<TElement>
    {
        private static (JsonSerializerOptions Options, JsonTypeInfo<ParsedReply<TElement>> Info)? _current;

        public static JsonTypeInfo<ParsedReply<TElement>> For(JsonSerializerOptions options)
        {
            if (_current is { } cached && ReferenceEquals(cached.Options, options))
                return cached.Info;

            var info = (JsonTypeInfo<ParsedReply<TElement>>)options.GetTypeInfo(typeof(ParsedReply<TElement>));
            _current = (options, info);

            return info;
        }
    }

    private static ParsedReply<TElement> Parse<TElement>(ReadOnlySpan<byte> reply)
    {
        var parsed = reply.IsEmpty
            ? new ParsedReply<TElement>()
            : JsonSerializer.Deserialize(reply, Contract<TElement>.For(GraphQLJsonContextRegistry.Options))
                ?? new ParsedReply<TElement>();

        if (parsed.HasErrors)
            throw new GraphQLReplyFailedException(carriedErrors: true);

        if (!parsed.HasData)
            throw new GraphQLReplyFailedException(carriedErrors: false);

        return parsed;
    }

    private static void RequireField<TElement>(ParsedReply<TElement> parsed, GraphQLOperation operation)
    {
        if (parsed.Field != operation.RootField)
            throw new GraphQLTranslationException("FGQL018",
                $"The response has no '{operation.RootField}' field.");
    }
}
