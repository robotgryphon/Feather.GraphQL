using Microsoft.CodeAnalysis;

namespace Feather.GraphQL.Linq.Analyzers;

/// <summary>
/// The translator's notion of "scalar" and "field", read from symbols instead of from
/// <see cref="System.Type"/>.
/// </summary>
/// <remarks>
/// The rules used to live behind an abstraction — a <c>TypeShape</c> struct both this and the
/// runtime translator mapped onto, so that neither could drift from the other about what counts
/// as a scalar. There is no runtime translator now, and nothing to drift from, so the rules are
/// applied to symbols where they are read. Whatever this file says is what a selection set
/// contains.
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

        if (IsPrimitive(underlying) || underlying.TypeKind == TypeKind.Enum)
            return true;

        string name = underlying.ToDisplayString(_metadataNames);

        foreach (string scalar in _scalarTypes)
        {
            if (scalar == name)
                return true;
        }

        // A framework value type nobody listed — a tuple, say — is still a leaf as far as a
        // selection set is concerned. A user's struct is not.
        return ElementType(underlying) is null
            && underlying.IsValueType
            && underlying.ContainingAssembly?.Name
                is "System.Private.CoreLib" or "System.Runtime" or "mscorlib";
    }

    /// <summary>
    /// Framework types that are GraphQL scalars despite not being primitives.
    /// </summary>
    /// <remarks>
    /// Named the way the runtime names them — <c>System.String</c>, never the <c>string</c>
    /// keyword — because that is the shape <see cref="_metadataNames"/> produces.
    /// </remarks>
    private static readonly string[] _scalarTypes =
    [
        "System.String",
        "System.Decimal",
        "System.Guid",
        "System.DateTime",
        "System.DateTimeOffset",
        "System.DateOnly",
        "System.TimeOnly",
        "System.TimeSpan",
        "System.Uri"
    ];

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

        // The attribute that names it, then the other one, then the member camel-cased.
        if (!string.IsNullOrEmpty(json))
            return json!;

        if (!string.IsNullOrEmpty(member))
            return member!;

        return CamelCase(property.Name);
    }

    /// <summary>Lower-cases the first character, and only that.</summary>
    public static string CamelCase(string name)
        => name.Length == 0 || !char.IsUpper(name[0])
            ? name
            : char.ToLowerInvariant(name[0]) + name.Substring(1);

}

