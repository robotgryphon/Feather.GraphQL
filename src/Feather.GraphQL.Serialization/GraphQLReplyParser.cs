using JetBrains.Annotations;

namespace Feather.GraphQL.Serialization;

/// <summary>
/// Reads a whole reply into whatever the query asked for.
/// </summary>
/// <remarks>
/// <para>
/// The one seam between a transport and a generated reader, and deliberately the only one. What a
/// compiled query reads with is code written for its own reply — structs mirroring the payload, or
/// the queried type built as it is read — and none of that is anything this library needs a type
/// for. What it does need is a way to hand those bytes over, which is this.
/// </para>
/// <para>
/// A delegate rather than an interface because <see cref="ReadOnlySpan{T}"/> cannot be a type
/// argument, and the bytes are a span: they live in a pooled buffer the transport owns and returns
/// the moment the read is done, so nothing may outlive the call.
/// </para>
/// <para>
/// The envelope's answers come back as out parameters rather than in a result type for the same
/// reason. Whether a reply carried <c>data</c>, and whether it carried <c>errors</c>, is what the
/// transport needs to decide whether to throw — and it is the transport that knows what throwing
/// means, since only it has the response to put in the exception.
/// </para>
/// </remarks>
/// <typeparam name="TResult">What reading the reply produces.</typeparam>
/// <param name="json">The reply's bytes, whole.</param>
/// <param name="hasData">Set to true when the reply carried a <c>data</c> object.</param>
/// <param name="hasErrors">Set to true when it carried a non-empty <c>errors</c> array.</param>
[PublicAPI]
public delegate TResult GraphQLReplyParser<out TResult>(
    ReadOnlySpan<byte> json,
    out bool hasData,
    out bool hasErrors);
