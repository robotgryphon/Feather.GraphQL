using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using Feather.GraphQL.Linq.Metadata;

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
    /// <c>[JsonPropertyName]</c>, then <c>[DataMember(Name)]</c>, then the member name
    /// camel-cased — the §3 rule, shared with the generator.
    /// </summary>
    private static string ResolveFieldName(PropertyInfo property)
    {
        if (property.GetCustomAttribute<JsonPropertyNameAttribute>() is { Name.Length: > 0 } json)
            return json.Name;

        if (property.GetCustomAttribute<DataMemberAttribute>() is { Name.Length: > 0 } member)
            return member.Name!;

        return CamelCase(property.Name);
    }

    internal static string CamelCase(string name)
    {
        if (name.Length == 0 || !char.IsUpper(name[0]))
            return name;

        return string.Create(name.Length, name, static (span, source) =>
        {
            source.AsSpan().CopyTo(span);
            span[0] = char.ToLowerInvariant(span[0]);
        });
    }

    public bool TryGetField(string clrName, [NotNullWhen(true)] out GraphQLFieldMetadata? field)
        => _byClrName.TryGetValue(clrName, out field);

    [UnconditionalSuppressMessage("Trimming", "IL2067",
        Justification = "Reflection fallback is only reached for types the generator never saw; "
                      + "attributed types resolve through generated metadata.")]
    public static IGraphQLTypeMetadata For(Type clrType)
        => GraphQLTypeMetadataRegistry.GetOrAdd(clrType, static t => new ReflectionTypeMetadata(t));
}
