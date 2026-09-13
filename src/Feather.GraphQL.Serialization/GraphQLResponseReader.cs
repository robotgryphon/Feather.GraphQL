using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Feather.GraphQL.Metadata;
using Feather.GraphQL.Primitives;

namespace Feather.GraphQL.Serialization;

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
    public static GraphQLReply<TData> Read<TData>(ReadOnlyMemory<byte> body)
    {
        if (body.Length == 0)
            return new GraphQLReply<TData>();

        try
        {
            return JsonSerializer.Deserialize(body.Span, Contract<TData>.Current)
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
    /// The contract one payload type's replies are read through, resolved once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read through <see cref="GraphQLJsonContextRegistry"/>, which is where a source-generated
    /// <c>JsonSerializerContext</c> registers itself. Deserializing with plain reflection instead
    /// — which this used to do — meant a caller who had done the work to declare contracts got
    /// them used for a composed query and ignored for a hand-written one, and meant this path
    /// could not run under NativeAOT at all.
    /// </para>
    /// <para>
    /// Resolving the contract from the options on every reply is not free, and this is the
    /// per-request path. Keyed on the options instance so that a late registration, which
    /// replaces them, is picked up rather than ignored; the pair is one field so a racing reader
    /// sees both or neither, and two threads resolving the same contract is harmless.
    /// </para>
    /// </remarks>
    private static class Contract<TData>
    {
        private static (JsonSerializerOptions Options, JsonTypeInfo<GraphQLReply<TData>> Info)? _current;

        public static JsonTypeInfo<GraphQLReply<TData>> Current
        {
            get
            {
                var options = GraphQLJsonContextRegistry.Options;

                if (_current is { } cached && ReferenceEquals(cached.Options, options))
                    return cached.Info;

                var info = GraphQLReplyContract.Create<TData>(options);
                _current = (options, info);

                return info;
            }
        }
    }




    /// <summary>
    /// Reads a reply's <c>errors</c> and nothing else.
    /// </summary>
    /// <remarks>
    /// For a caller that has already read the reply some other way and needs the errors it was
    /// told were there — the LINQ transport, which deserializes rows through a contract of its
    /// own and gets back a flag rather than the errors themselves.
    /// </remarks>
    public static GraphQLError[]? ErrorsIn(ReadOnlyMemory<byte> body) => body.Length == 0 ? null : Errors(body);

    /// <summary>
    /// Rescans a reply for its <c>errors</c>, ignoring everything else in it.
    /// </summary>
    /// <remarks>
    /// A second pass, and deliberately so: it runs only on a reply that was already going to
    /// fail. Buying this guarantee with a pass over every successful reply — scanning the
    /// envelope first and reading the payload after — is what this reader exists to avoid, and
    /// costs more than the payload-shaped reads it protects.
    /// </remarks>
    private static GraphQLError[]? Errors(ReadOnlyMemory<byte> body)
    {
        try
        {
            var reader = new Utf8JsonReader(body.Span);

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

                return JsonSerializer.Deserialize(ref reader, GraphQLErrorContext.Default.GraphQLErrorArray);
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
