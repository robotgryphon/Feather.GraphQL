using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using Feather.GraphQL.Linq.Metadata;
using Feather.GraphQL.Linq.Rules;

namespace Feather.GraphQL.Linq.Metadata;

/// <summary>
/// Field mapping built by reflecting over a type the source generator never saw — someone
/// else's entity, typically, reached through the <see cref="IQueryable{T}"/> extensions.
/// </summary>
/// <remarks>
/// This is the one place reflection survives in the translation path. Generated metadata takes
/// precedence when present. Instances are cached per type in
/// <see cref="GraphQLTypeMetadataRegistry"/>, so the reflection cost is paid once.
/// </remarks>
internal sealed class ReflectionTypeMetadata : IGraphQLTypeMetadata
{
    private readonly Dictionary<string, GraphQLFieldMetadata> _byClrName;

    public Type ClrType { get; }
    public IReadOnlyList<GraphQLFieldMetadata> Fields { get; }

    private ReflectionTypeMetadata(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type clrType)
    {
        ClrType = clrType;

        var fields = new List<GraphQLFieldMetadata>();
        foreach (var property in clrType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0)
                continue;

            bool ignored = property.GetCustomAttribute<JsonIgnoreAttribute>() is not null;
            fields.Add(GraphQLFieldMetadata.FromProperty(property, ResolveFieldName(property), ignored));
        }

        Fields = fields;
        _byClrName = fields.ToDictionary(f => f.ClrName, StringComparer.Ordinal);
    }

    /// <summary>
    /// Reads the attributes this side can see, and hands the rule the values it needs.
    /// </summary>
    private static string ResolveFieldName(PropertyInfo property)
        => GraphQLRules.FieldName(
            property.Name,
            property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name,
            property.GetCustomAttribute<DataMemberAttribute>()?.Name);

    internal static string CamelCase(string name) => GraphQLRules.CamelCase(name);

    public bool TryGetField(string clrName, [NotNullWhen(true)] out GraphQLFieldMetadata? field)
        => _byClrName.TryGetValue(clrName, out field);

    [UnconditionalSuppressMessage("Trimming", "IL2067",
        Justification = "Reflection fallback is only reached for types the generator never saw; "
                      + "attributed types resolve through generated metadata.")]
    public static IGraphQLTypeMetadata For(Type clrType)
        => GraphQLTypeMetadataRegistry.GetOrAdd(clrType, static t => new ReflectionTypeMetadata(t));
}
