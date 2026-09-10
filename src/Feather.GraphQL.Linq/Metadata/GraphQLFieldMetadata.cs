using System.Reflection;

namespace Feather.GraphQL.Linq.Metadata;

/// <summary>A single CLR member and the GraphQL field it maps to.</summary>
public sealed class GraphQLFieldMetadata(string clrName, string fieldName, Type clrType, bool isIgnored = false)
{
    /// <summary>The CLR property name.</summary>
    public string ClrName { get; } = clrName;

    /// <summary>
    /// The GraphQL field name, resolved from <c>[JsonPropertyName]</c>, then
    /// <c>[DataMember(Name)]</c>, then the member name camel-cased.
    /// </summary>
    public string FieldName { get; } = fieldName;

    /// <summary>The declared CLR type, with <see cref="Nullable{T}"/> left intact.</summary>
    public Type ClrType { get; } = clrType;

    /// <summary>True when the member carries <c>[JsonIgnore]</c> and is not a GraphQL field.</summary>
    public bool IsIgnored { get; } = isIgnored;

    public static GraphQLFieldMetadata FromProperty(PropertyInfo property, string fieldName, bool isIgnored)
        => new(property.Name, fieldName, property.PropertyType, isIgnored);
}
