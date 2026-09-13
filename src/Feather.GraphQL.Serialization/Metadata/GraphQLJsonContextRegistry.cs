using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using JetBrains.Annotations;

namespace Feather.GraphQL.Metadata;

/// <summary>
/// Where a source-generated <see cref="JsonSerializerContext"/> registers itself, so results can
/// be materialized without reflecting over the response type.
/// </summary>
/// <remarks>
/// <para>
/// A context cannot be emitted by this library's generator: source generators do not see one
/// another's output, so <c>System.Text.Json</c>'s generator would never process a context class
/// that another generator produced — it compiles to a partial class with unimplemented abstract
/// members. The context is therefore declared in your own source, where STJ's generator can see
/// it, and registered here. The declaring is one attribute list; everything after it is
/// automatic.
/// </para>
/// <para>
/// Registration is normally emitted for you: the analyzer package finds every
/// <see cref="JsonSerializerContext"/> in a compilation and registers it from a module
/// initializer.
/// </para>
/// <para>
/// It lives here, below both halves of the library, because reading a reply is not a LINQ
/// concern. The transport reads one whether a query was composed or written out by hand, and
/// both should read it through the same contracts — otherwise the hand-written half quietly
/// reflects while the composed half does not, which is a difference nobody asked for and
/// nothing in the source would explain.
/// </para>
/// </remarks>
[PublicAPI]
public static class GraphQLJsonContextRegistry
{
    private static readonly List<IJsonTypeInfoResolver> _resolvers = [];
    private static readonly List<JsonConverter> _converters = [];
    private static readonly Lock _gate = new();
    private static JsonSerializerOptions? _options;


    /// <summary>
    /// Adds a converter to the ones replies are read with.
    /// </summary>
    /// <remarks>
    /// How the LINQ half teaches this registry to read a reply whose root field is named at
    /// runtime. Kept as a registration rather than a reference so that the dependency points the
    /// way it already did: this assembly knows nothing about queries.
    /// </remarks>
    public static void Register(JsonConverter converter)
    {
        ArgumentNullException.ThrowIfNull(converter);

        lock (_gate)
        {
            _converters.Add(converter);
            _options = null;
        }
    }

    /// <summary>Adds a context's contracts to the ones results are materialized through.</summary>
    public static void Register(IJsonTypeInfoResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);

        lock (_gate)
        {
            _resolvers.Add(resolver);

            // Options are immutable once used, so a late registration gets a fresh set rather
            // than being silently ignored.
            Volatile.Write(ref _options, null);
        }
    }

    /// <summary>The options results are read with. Public so a test can ask where a contract came from.</summary>
    /// <remarks>
    /// Read without taking the lock once there is something to read. This sits on the per-query
    /// path, and a lock there would be paid by every request to guard a field that is written
    /// once at startup in every program that is not still registering contexts.
    /// </remarks>
    public static JsonSerializerOptions Options
    {
        get
        {
            var current = Volatile.Read(ref _options);

            if (current is not null)
                return current;

            lock (_gate)
            {
                current = _options ??= Build();
                Volatile.Write(ref _options, current);

                return current;
            }
        }
    }

    /// <summary>
    /// The contract for one type — generated when a registered context covers it, reflected when
    /// none does.
    /// </summary>
    public static JsonTypeInfo TypeInfo(Type type) => Options.GetTypeInfo(type);


    private static JsonSerializerOptions Build()
    {
        // Registered contexts and nothing else. A reflection resolver used to sit at the end of
        // this chain so that a type no context covered still materialized — which is exactly what
        // an AOT build cannot do, and it made every read through this registry unanalyzable. A
        // type nobody declared is now a clear failure at the point of reading rather than a
        // silent dependence on reflection that only fails once published.
        var chain = new List<IJsonTypeInfoResolver>(_resolvers);

        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            TypeInfoResolver = JsonTypeInfoResolver.Combine([.. chain]).WithAddedModifier(NothingIsRequired)
        };

        foreach (var converter in _converters)
            options.Converters.Add(converter);

        return options;
    }

    /// <summary>
    /// A projection asks for a subset of the type's fields, so the response legitimately omits
    /// the rest — including members marked <c>required</c>, which the serializer would otherwise
    /// refuse to leave unset. Requiredness is the server's contract to enforce, not the
    /// materializer's, and enforcing it here would make <c>Select(p =&gt; p.Name)</c> fail on any
    /// type with a required member.
    /// </summary>
    /// <remarks>
    /// Applied over the whole chain, so it holds for generated contracts as well as reflected
    /// ones — a modifier changes the contract model, not how it was built.
    /// </remarks>
    private static void NothingIsRequired(JsonTypeInfo info)
    {
        if (info.Kind is not JsonTypeInfoKind.Object)
            return;

        foreach (var property in info.Properties)
            property.IsRequired = false;
    }
}
