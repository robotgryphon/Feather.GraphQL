using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using Feather.GraphQL.Serialization;

namespace Feather.GraphQL.Http;

public static class HttpExtensions
{
    /// <summary>
    /// The request headers, rendered once.
    /// </summary>
    /// <remarks>
    /// Every one of these was rebuilt per request: three media types and a charset parsed from
    /// their strings, and a user agent that read the assembly's name twice — <see
    /// cref="Assembly.GetName()"/> builds a fresh <see cref="AssemblyName"/> each call — to
    /// format a value that cannot change while the process is running. Together that was around
    /// 700 ns and 2.4 KB per request, which on a small reply was more than the rest of the
    /// client's work put together.
    /// </remarks>
    private static readonly string ACCEPT = string.Join(", ", GraphQLHttpConstants.RESPONSE_CONTENT_TYPES);

    private static readonly string USER_AGENT = UserAgent();

    private const string JSON_CONTENT_TYPE = "application/json";

    private static string UserAgent()
    {
        var assembly = typeof(HttpExtensions).Assembly.GetName();

        return $"{assembly.Name}/{assembly.Version}";
    }

    extension(HttpRequestMessage message)
    {
        /// <summary>
        /// Adds the headers every GraphQL request carries.
        /// </summary>
        /// <remarks>
        /// Added unparsed. The values are constants this assembly wrote itself, so there is
        /// nothing for the header parser to find — and a parsed value is an object the whole
        /// process would then share, which a caller reaching into <c>Headers.Accept</c> could
        /// mutate out from under every later request.
        /// </remarks>
        private void AddGraphQLRequestHeaders()
        {
            message.Headers.TryAddWithoutValidation("Accept", ACCEPT);
            message.Headers.TryAddWithoutValidation("Accept-Charset", "utf-8");
            message.Headers.TryAddWithoutValidation("User-Agent", USER_AGENT);
        }
    }

    extension(HttpClient client)
    {
        /// <summary>
        /// Posts a GraphQL query string with the values its variables take.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The shape other clients take, and the one a caller with a document already has in
        /// hand expects. What it costs is what an ordinary dictionary costs — a box per value and
        /// a type test to write it — and the body itself is built the way the library builds
        /// every body, in pooled memory with nothing allocated per request that is not the
        /// request.
        /// </para>
        /// <para>
        /// The values are written by type rather than serialized, so this stays on the path a
        /// NativeAOT build can see. A type the table below does not name is refused, plainly, at
        /// the moment of writing — the alternative is a reflection fallback that would work in
        /// development and fail once published.
        /// </para>
        /// </remarks>
        /// <param name="query">The operation to send.</param>
        /// <param name="variables">
        /// The values its variables take. Strings, booleans, the numeric types, <c>Guid</c>, the
        /// date and time types, enums, nested dictionaries, and sequences of any of those.
        /// </param>
        /// <param name="cancellationToken">Cancels the request.</param>
        /// <exception cref="NotSupportedException">A value is of a type this cannot write.</exception>
        public async ValueTask<HttpResponseMessage> SendGraphQLQueryAsync(
                [StringSyntax("GraphQL")] string query,
                IReadOnlyDictionary<string, object?> variables,
                CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(query);
            ArgumentNullException.ThrowIfNull(variables);
            cancellationToken.ThrowIfCancellationRequested();

            using var body = PooledBody.Rent(query.Length + 64);

            body.WriteRaw("{\"query\":"u8);
            body.Write(query);

            if (variables.Count > 0)
            {
                body.WriteRaw(",\"variables\":"u8);
                WriteMap(body, variables);
            }

            body.WriteRaw("}"u8);

            return await client.PostGraphQLBodyAsync(body.Written, null, cancellationToken)
                    .ConfigureAwait(false);
        }

        /// <summary>
        /// Posts a precompiled GraphQL query string. Any values the query needs must already be
        /// present in its text — this path carries no variables payload.
        /// </summary>
        public async ValueTask<HttpResponseMessage> SendGraphQLQueryAsync(
                [StringSyntax("GraphQL")] string query,
                CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(query);
            cancellationToken.ThrowIfCancellationRequested();

            using var body = PooledBody.Rent(query.Length + 16);

            body.WriteRaw("{\"query\":"u8);
            body.Write(query);
            body.WriteRaw("}"u8);

            return await client.PostGraphQLBodyAsync(body.Written, null, cancellationToken)
                    .ConfigureAwait(false);
        }

        /// <summary>
        /// Posts a body that is already the bytes to send.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The entry point a compiled query uses. Its body was assembled from constants the
        /// compiler printed and the values the caller passed, so there is nothing left for this to
        /// serialize — the bytes go to the transport as they are, without being copied into an
        /// array first.
        /// </para>
        /// <para>
        /// The body must outlive the send, which it does: the request is awaited here, and a
        /// caller disposes the body afterwards. Returning its buffer to the pool before this
        /// returns would hand the same memory to someone else while it was still being read.
        /// </para>
        /// </remarks>
        /// <param name="body">The request body, as UTF-8 JSON.</param>
        /// <param name="endpoint">
        /// Where to post. A relative URI is resolved against the client's
        /// <see cref="HttpClient.BaseAddress"/> by <see cref="HttpClient"/> itself; null posts to
        /// the base address as-is.
        /// </param>
        /// <param name="cancellationToken">Cancels the request.</param>
        public async ValueTask<HttpResponseMessage> PostGraphQLBodyAsync(
                ReadOnlyMemory<byte> body,
                Uri? endpoint = null,
                CancellationToken cancellationToken = default)
        {
            if (body.IsEmpty)
                throw new ArgumentException("A request body cannot be empty.", nameof(body));

            cancellationToken.ThrowIfCancellationRequested();

            var content = new ReadOnlyMemoryContent(body);

            content.Headers.TryAddWithoutValidation("Content-Type", JSON_CONTENT_TYPE);

            var message = new HttpRequestMessage
            {
                Method = HttpMethod.Post,
                Content = content,
                RequestUri = endpoint ?? client.BaseAddress
            };

            message.AddGraphQLRequestHeaders();

            // Headers only, for the reason the translated path gives: the reply is buffered into
            // pooled memory by whoever reads it, and letting HttpClient buffer it first would mean
            // holding the same bytes twice.
            return await client
                    .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
        }
    }

    /// <summary>Writes a map of variables as a JSON object.</summary>
    /// <remarks>
    /// The separators are written here. A <c>Utf8JsonWriter</c> would have tracked what it had
    /// written and put the commas in; bytes do not, so the count does.
    /// </remarks>
    private static void WriteMap(PooledBody body, IReadOnlyDictionary<string, object?> map)
    {
        body.WriteRaw("{"u8);

        int written = 0;

        foreach (var pair in map)
        {
            if (written++ > 0)
                body.WriteRaw(","u8);

            body.Write(pair.Key);
            body.WriteRaw(":"u8);

            WriteValue(body, pair.Value);
        }

        body.WriteRaw("}"u8);
    }

    /// <summary>
    /// Writes one variable's value as the JSON a server expects for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A type test per case, and no serializer. That is what keeps this path trimmable: asking
    /// <c>System.Text.Json</c> to write an <see cref="object"/> means asking it to find a
    /// contract for whatever the runtime type turns out to be, which is the one thing an
    /// ahead-of-time build cannot do.
    /// </para>
    /// <para>
    /// <see cref="string"/> is tested before <see cref="IEnumerable"/> because it is one, and a
    /// string written as an array of characters is a bug that would reach a server rather than a
    /// compiler.
    /// </para>
    /// </remarks>
    private static void WriteValue(PooledBody body, object? value)
    {
        switch (value)
        {
            case null: body.WriteNull(); return;
            case string text: body.Write(text); return;
            case bool flag: body.Write(flag); return;
            case int i: body.Write(i); return;
            case long l: body.Write(l); return;
            case short s: body.Write((int)s); return;
            case sbyte sb: body.Write((int)sb); return;
            case byte b: body.Write((int)b); return;
            case ushort us: body.Write((int)us); return;
            case uint ui: body.Write((long)ui); return;
            case ulong ul: body.Write((decimal)ul); return;
            case double d: body.Write(d); return;
            case float f: body.Write((double)f); return;
            case decimal m: body.Write(m); return;
            case Guid g: body.Write(g); return;
            case DateTime dt: body.Write(dt); return;
            case DateTimeOffset dto: body.Write(dto); return;
            case DateOnly date: body.Write(date); return;
            case TimeOnly time: body.Write(time); return;

            // A GraphQL enum travels as its name, and in variables that name is a JSON string.
            case Enum e: body.Write(e.ToString()); return;

            case IReadOnlyDictionary<string, object?> nested: WriteMap(body, nested); return;
        }

        if (value is IEnumerable items)
        {
            body.WriteRaw("["u8);

            int written = 0;

            foreach (object? item in items)
            {
                if (written++ > 0)
                    body.WriteRaw(","u8);

                WriteValue(body, item);
            }

            body.WriteRaw("]"u8);

            return;
        }

        throw new NotSupportedException(
            $"A variable of type '{value.GetType().Name}' cannot be written. Pass a scalar, an "
            + "enum, a dictionary, or a sequence of those — or build the request body yourself.");
    }

    extension(HttpResponseMessage response)
    {
        /// <summary>
        /// Reads the reply with a parser written for this query's own reply.
        /// </summary>
        /// <remarks>
        /// The preferred path for a compiled query, and the reason this package no longer owns
        /// reading: the parser is generated from the selection set, so the rows arrive in the shape
        /// the caller asked for, in one pass, with no contract resolved and nothing materialized on
        /// the way that is not the answer. What this adds is the part that is the transport's — the
        /// pooled buffer, and what a failed reply means.
        /// </remarks>
        /// <typeparam name="TResult">What the parser produces.</typeparam>
        /// <param name="parse">The generated parser.</param>
        /// <param name="cancellationToken">Cancels the read.</param>
        /// <exception cref="GraphQLHttpException">
        /// The reply carried an <c>errors</c> array, or carried no <c>data</c> at all.
        /// </exception>
        public async ValueTask<TResult> ReadGraphQLReplyAsync<TResult>(
                GraphQLReplyParser<TResult> parse,
                CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(response);
            ArgumentNullException.ThrowIfNull(parse);

            using var body = await ResponseBuffer
                    .ReadAsync(response.Content, cancellationToken)
                    .ConfigureAwait(false);

            var result = parse(body.Bytes.Span, out bool hasData, out bool hasErrors);

            // The errors themselves are not collected on the way past — a reply that carries them
            // is the failing one, and rescanning it costs nothing a successful read pays.
            Ensure(response, hasErrors, hasData, errors: null, body.Bytes);

            return result;
        }

        /// <summary>
        /// Reads the reply's <c>data</c> into <typeparamref name="TData"/>.
        /// </summary>
        /// <typeparam name="TData">
        /// The shape of the <c>data</c> field — not of the whole reply. A query for
        /// <c>person</c> deserializes into a type with a <c>Person</c> member, not into
        /// <c>Person</c> itself.
        /// </typeparam>
        /// <param name="cancellationToken">Cancels the read.</param>
        /// <returns>The <c>data</c> field, deserialized.</returns>
        /// <exception cref="GraphQLHttpException">
        /// The reply carried an <c>errors</c> array, or carried no <c>data</c> at all. The
        /// exception holds the parsed errors and this response, still undisposed. Catch its base
        /// <see cref="GraphQLException"/> to handle a failed query without naming the transport.
        /// </exception>
        /// <remarks>
        /// Errors are checked before the status code, because a GraphQL server routinely reports
        /// them with <c>200 OK</c> — and when it does, its own message is far more useful than
        /// the status.
        /// </remarks>
        public async ValueTask<TData> ReadGraphQLAsync<TData>(
                CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(response);

            // Buffered before it is parsed, rather than deserialized off the content stream.
            // System.Text.Json's asynchronous reader works over a chain of segments and cannot
            // see the whole document at once; handed one span it is substantially faster.
            using var body = await ResponseBuffer
                    .ReadAsync(response.Content, cancellationToken)
                    .ConfigureAwait(false);

            var reply = GraphQLResponseReader.Read<TData>(body.Bytes);

            if (reply.Errors is { Length: > 0 } errors)
                throw new GraphQLHttpException(errors, response);

            response.EnsureSuccessStatusCode();

            // An absent data, a null one, and a JsonElement that read as either are the same
            // answer: the server sent nothing to return. The element is named explicitly because
            // it is a value type, so its "nothing" is a ValueKind rather than a null.
            if (reply.Data is null or JsonElement { ValueKind: JsonValueKind.Undefined or JsonValueKind.Null })
                throw new GraphQLHttpException(errors: null, response);

            return reply.Data;
        }
    }

    /// <summary>
    /// Throws what a failed reply means, for the readers that share one idea of failure.
    /// </summary>
    /// <remarks>
    /// Errors first, before the status code: a GraphQL server answers a failed query with 200 and
    /// an <c>errors</c> array far more often than with a failing status, and the array says more
    /// than the status would. A reply with neither errors nor <c>data</c> is still a failure —
    /// there is nothing to return and no explanation of why.
    /// </remarks>
    private static void Ensure(
        HttpResponseMessage response,
        bool hasErrors,
        bool hasData,
        Primitives.GraphQLError[]? errors,
        ReadOnlyMemory<byte> body)
    {
        if (hasErrors)
            throw new GraphQLHttpException(errors ?? GraphQLResponseReader.ErrorsIn(body) ?? [], response);

        response.EnsureSuccessStatusCode();

        if (!hasData)
            throw new GraphQLHttpException(errors: null, response);
    }
}
