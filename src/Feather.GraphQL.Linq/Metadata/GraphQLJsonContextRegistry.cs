using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using JetBrains.Annotations;

namespace Feather.GraphQL.Linq.Metadata;

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
/// </remarks>
[PublicAPI]
public static class GraphQLJsonContextRegistry
{
    private static readonly List<IJsonTypeInfoResolver> _resolvers = [];
    private static readonly Lock _gate = new();
    private static JsonSerializerOptions? _options;

    /// <summary>Adds a context's contracts to the ones results are materialized through.</summary>
    public static void Register(IJsonTypeInfoResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);

        lock (_gate)
        {
            _resolvers.Add(resolver);

            // Options are immutable once used, so a late registration gets a fresh set rather
            // than being silently ignored.
            _options = null;
        }
    }

    /// <summary>The options results are read with. Public so a test can ask where a contract came from.</summary>
    public static JsonSerializerOptions Options
    {
        get
        {
            lock (_gate)
                return _options ??= Build();
        }
    }

    /// <summary>
    /// The contract for one type — generated when a registered context covers it, reflected when
    /// none does.
    /// </summary>
    internal static JsonTypeInfo TypeInfo(Type type) => Options.GetTypeInfo(type);

    private static JsonSerializerOptions Build()
    {
        // Registered contexts first, reflection last: a generated contract wins, and a type no
        // context covers still materializes. That is the same fallback the field tables keep.
        var chain = new List<IJsonTypeInfoResolver>(_resolvers.Count + 1);
        chain.AddRange(_resolvers);
        chain.Add(new DefaultJsonTypeInfoResolver());

        return new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            TypeInfoResolver = JsonTypeInfoResolver.Combine([.. chain]).WithAddedModifier(NothingIsRequired)
        };
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
