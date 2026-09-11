using Feather.GraphQL.Linq.Rules;
using Microsoft.CodeAnalysis;

namespace Feather.GraphQL.Linq.Analyzers;

/// <summary>
/// The translator's notion of "scalar" and "field", read from symbols instead of from
/// <see cref="System.Type"/>.
/// </summary>
/// <remarks>
/// The <em>walk</em> is a second implementation — the compiler has no <see cref="System.Type"/>,
/// so unwrapping and enumerating have to be written against symbols. The <em>rules</em> are not:
/// this maps a symbol onto <c>TypeShape</c> and hands it to <c>GraphQLRules</c>, the same code the
/// translator calls. A mapping that drifts shows up as a wrong shape; a rule that drifted would
/// show up as a silently different selection set, which is why only one copy of it exists.
/// </remarks>
internal static class GraphQLTypeFacts
{
    /// <summary>Unwraps <see cref="System.Nullable{T}"/> and then a collection, if either applies.</summary>
    public static ITypeSymbol Unwrap(ITypeSymbol type)
        => ElementType(UnwrapNullable(type)) ?? UnwrapNullable(type);

    public static ITypeSymbol UnwrapNullable(ITypeSymbol type)
        => type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
            ? nullable.TypeArguments[0]
            : type;

    /// <summary>The element type of a collection, or null when the type is not one.</summary>
    public static ITypeSymbol? ElementType(ITypeSymbol type)
    {
        if (type.SpecialType == SpecialType.System_String)
            return null;

        if (type is IArrayTypeSymbol array)
            return array.ElementType;

        if (type is INamedTypeSymbol
            { OriginalDefinition.SpecialType: SpecialType.System_Collections_Generic_IEnumerable_T } self)
            return self.TypeArguments[0];

        foreach (var candidate in type.AllInterfaces)
        {
            if (candidate.OriginalDefinition.SpecialType
                == SpecialType.System_Collections_Generic_IEnumerable_T)
                return candidate.TypeArguments[0];
        }

        return null;
    }

    /// <summary>
    /// A value that needs no selection set, decided by the shared rule.
    /// </summary>
    /// <remarks>
    /// An unresolved type is treated as a scalar so that a query over code that does not compile
    /// yet produces the compiler's error and not a second, misleading one from this analyzer.
    /// </remarks>
    public static bool IsScalar(ITypeSymbol type)
    {
        var underlying = UnwrapNullable(type);

        if (underlying.TypeKind == TypeKind.Error)
            return true;

        return GraphQLRules.IsScalar(Shape(underlying));
    }

    /// <summary>
    /// Describes a symbol in the terms the shared rules are written in.
    /// </summary>
    private static TypeShape Shape(ITypeSymbol type)
        => new(
            type.ToDisplayString(_metadataNames),
            IsPrimitive(type),
            type.TypeKind == TypeKind.Enum,
            type.IsValueType,
            ElementType(type) is not null,
            type.ContainingAssembly?.Name is "System.Private.CoreLib" or "System.Runtime" or "mscorlib");

    /// <summary>
    /// What <c>Type.IsPrimitive</c> answers true for — the CLR's own short list, which is
    /// narrower than "built-in keyword type": it excludes <c>decimal</c> and <c>string</c>, both
    /// of which the shared rule names explicitly instead.
    /// </summary>
    /// <summary>
    /// Names types the way the runtime does — <c>System.String</c>, never the <c>string</c>
    /// keyword — because the shared rule compares against the names <c>Type.FullName</c> gives.
    /// </summary>
    private static readonly SymbolDisplayFormat _metadataNames = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.ExpandNullable);

    private static bool IsPrimitive(ITypeSymbol type)
        => type.SpecialType is SpecialType.System_Boolean
            or SpecialType.System_Byte or SpecialType.System_SByte
            or SpecialType.System_Int16 or SpecialType.System_UInt16
            or SpecialType.System_Int32 or SpecialType.System_UInt32
            or SpecialType.System_Int64 or SpecialType.System_UInt64
            or SpecialType.System_IntPtr or SpecialType.System_UIntPtr
            or SpecialType.System_Single or SpecialType.System_Double
            or SpecialType.System_Char;

    /// <summary>
    /// A field that needs no selection set of its own: a scalar, or a list of them.
    /// </summary>
    public static bool IsLeaf(ITypeSymbol type)
    {
        var underlying = UnwrapNullable(type);

        return IsScalar(ElementType(underlying) ?? underlying);
    }

    /// <summary>The properties the translator would map, in the order it maps them.</summary>
    public static IEnumerable<IPropertySymbol> Fields(ITypeSymbol type)
    {
        foreach (var property in Properties(type))
        {
            if (!IsIgnored(property))
                yield return property;
        }
    }

    /// <summary>True when <c>[JsonIgnore]</c> takes the property out of the schema.</summary>
    public static bool IsIgnored(IPropertySymbol property)
    {
        foreach (var attribute in property.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString()
                == "System.Text.Json.Serialization.JsonIgnoreAttribute")
                return true;
        }

        return false;
    }
    /// <summary>
    /// The properties the reflection fallback would map: public, instance, not indexers —
    /// <em>including inherited ones</em>, because <c>Type.GetProperties</c> returns those and a
    /// table that omitted them would disagree with the fallback it replaces.
    /// </summary>
    /// <remarks>
    /// Walks the chain itself rather than asking for it, since <c>GetMembers</c> returns only
    /// what a type declares. A derived member shadows a base one of the same name, which is the
    /// order reflection reports too.
    /// </remarks>
    public static List<IPropertySymbol> Properties(ITypeSymbol type)
    {
        var properties = new List<IPropertySymbol>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var current = type as INamedTypeSymbol; current is not null && current.SpecialType is SpecialType.None;
             current = current.BaseType)
        {
            foreach (var member in current.GetMembers())
            {
                if (member is IPropertySymbol
                    {
                        DeclaredAccessibility: Accessibility.Public,
                        IsStatic: false,
                        IsIndexer: false
                    } property
                    && seen.Add(property.Name))
                    properties.Add(property);
            }
        }

        return properties;
    }

    /// <summary>
    /// <c>[JsonPropertyName]</c>, then <c>[DataMember(Name)]</c>, then the member name
    /// camel-cased — the same rule, in symbol space, that the reflection fallback applies.
    /// </summary>
    /// <summary>
    /// Reads the attributes off the symbol and hands the rule the values it needs — the same rule
    /// the reflection fallback applies to the same two attributes.
    /// </summary>
    public static string FieldName(IPropertySymbol property)
    {
        string? json = null;
        string? member = null;

        foreach (var attribute in property.GetAttributes())
        {
            switch (attribute.AttributeClass?.ToDisplayString())
            {
                case "System.Text.Json.Serialization.JsonPropertyNameAttribute"
                    when attribute.ConstructorArguments.Length > 0:
                    json = attribute.ConstructorArguments[0].Value as string;
                    break;

                case "System.Runtime.Serialization.DataMemberAttribute":
                    foreach (var named in attribute.NamedArguments)
                    {
                        if (named.Key == "Name")
                            member = named.Value.Value as string;
                    }

                    break;
            }
        }

        return GraphQLRules.FieldName(property.Name, json, member);
    }

}

