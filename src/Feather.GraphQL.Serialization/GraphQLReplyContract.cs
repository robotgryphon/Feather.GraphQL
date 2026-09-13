using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Feather.GraphQL.Metadata;
using Feather.GraphQL.Primitives;

namespace Feather.GraphQL.Serialization;

/// <summary>
/// Describes the reply envelope to the serializer without reflecting on it.
/// </summary>
/// <remarks>
/// <para>
/// The envelope is generic in the payload, and nothing declares a contract for a constructed
/// generic it has never heard of — which is why reading it used to fall back to reflection, and
/// why walking it by hand was tried instead. Walking it costs a second pass over the payload:
/// finding where <c>data</c> ends means tokenizing every row, and the serializer then tokenizes
/// them again. Measured at about 40% on a 25-row reply.
/// </para>
/// <para>
/// So the envelope is described rather than walked. Two properties, written out here the way a
/// source generator would write them, with <c>data</c> pointing at whatever contract the caller
/// declared for their own type. One pass, no reflection, and the caller still declares nothing
/// but the type they asked for.
/// </para>
/// <para>
/// Built per payload type through a generic static, so every instantiation an app uses is one the
/// compiler can see. That is the difference between this and a resolver keyed on
/// <see cref="Type"/>, which would need <c>MakeGenericType</c> and would be exactly what an AOT
/// build cannot do.
/// </para>
/// </remarks>
internal static class GraphQLReplyContract
{
    public static JsonTypeInfo<GraphQLReply<TData>> Create<TData>(JsonSerializerOptions options)
        => JsonMetadataServices.CreateObjectInfo<GraphQLReply<TData>>(
            options,
            new JsonObjectInfoValues<GraphQLReply<TData>>
            {
                ObjectCreator = static () => new GraphQLReply<TData>(),
                // The options are captured rather than taken from the parameter: that is the
                // context this was created from, and a contract built from options alone has
                // no context to be handed.
                PropertyMetadataInitializer = _ => Properties<TData>(options)
            });

    /// <summary>The envelope's two members, named as the specification names them.</summary>
    /// <remarks>
    /// The JSON names are given rather than derived, so the naming policy on whatever options
    /// this is built for cannot rename <c>data</c> into something a server never sends.
    /// </remarks>
    private static JsonPropertyInfo[] Properties<TData>(JsonSerializerOptions options)
    {
        var data = JsonMetadataServices.CreatePropertyInfo(
            options,
            new JsonPropertyInfoValues<TData?>
            {
                IsProperty = true,
                IsPublic = true,
                DeclaringType = typeof(GraphQLReply<TData>),
                PropertyTypeInfo = Payload<TData>(options),
                PropertyName = nameof(GraphQLReply<TData>.Data),
                JsonPropertyName = "data",
                Getter = static obj => ((GraphQLReply<TData>)obj).Data,
                Setter = static (obj, value) => ((GraphQLReply<TData>)obj).Data = value
            });

        var errors = JsonMetadataServices.CreatePropertyInfo(
            options,
            new JsonPropertyInfoValues<GraphQLError[]?>
            {
                IsProperty = true,
                IsPublic = true,
                DeclaringType = typeof(GraphQLReply<TData>),
                PropertyTypeInfo = (JsonTypeInfo<GraphQLError[]?>)options.GetTypeInfo(typeof(GraphQLError[])),
                PropertyName = nameof(GraphQLReply<TData>.Errors),
                JsonPropertyName = "errors",
                Getter = static obj => ((GraphQLReply<TData>)obj).Errors,
                Setter = static (obj, value) => ((GraphQLReply<TData>)obj).Errors = value
            });

        return [data, errors];
    }

    /// <summary>The caller's own contract for the type they asked for.</summary>
    /// <remarks>
    /// Resolved from the registry, which is where their <c>JsonSerializerContext</c> registered
    /// itself. A type no context covers fails here — plainly, and at the moment of reading —
    /// rather than quietly materializing through reflection.
    /// </remarks>
    private static JsonTypeInfo<TData?> Payload<TData>(JsonSerializerOptions options)
        => (JsonTypeInfo<TData?>)options.GetTypeInfo(typeof(TData));
}
