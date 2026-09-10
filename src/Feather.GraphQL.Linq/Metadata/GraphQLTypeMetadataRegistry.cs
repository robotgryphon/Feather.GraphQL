using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Feather.GraphQL.Linq.Metadata;

/// <summary>
/// Where generated metadata registers itself, via a module initializer, at assembly load.
/// Types absent from the registry fall back to reflection.
/// </summary>
public static class GraphQLTypeMetadataRegistry
{
    private static readonly ConcurrentDictionary<Type, IGraphQLTypeMetadata> _metadata = new();

    public static void Register(IGraphQLTypeMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        _metadata[metadata.ClrType] = metadata;
    }

    public static bool TryGet(Type clrType, [NotNullWhen(true)] out IGraphQLTypeMetadata? metadata)
        => _metadata.TryGetValue(clrType, out metadata);

    /// <summary>Registers a reflection-built fallback so it is only built once per type.</summary>
    public static IGraphQLTypeMetadata GetOrAdd(Type clrType, Func<Type, IGraphQLTypeMetadata> factory)
        => _metadata.GetOrAdd(clrType, factory);
}
