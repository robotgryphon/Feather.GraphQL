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

        // A type whose model handed it to a converter is one value, whatever its properties say.
        if (Converter(underlying).Converter is not null)
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
    /// What a value of this type is called in the schema, or null when there is no name this can
    /// be sure of.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Needed only where a scalar has to be <em>declared</em> rather than merely written: a
    /// filter whose structure goes into the document names one variable per value, and a variable
    /// has to say what type it is. Nothing else in the library needs this, because a value
    /// written into a variables payload is coerced by the server against the input type it lands
    /// in and never has to name itself.
    /// </para>
    /// <para>
    /// The names are HotChocolate's defaults, which is the dialect the operations in
    /// <see cref="FilterSkeleton"/> are already hard-coded to. Deliberately partial: a type whose
    /// schema name is a judgement call — <c>Uri</c>, <c>TimeSpan</c>, <c>char</c>, the unsigned
    /// integers — answers null, and the caller keeps the form that needs no name. Guessing wrong
    /// here is not a compile error but a query the server rejects, so the list holds only the
    /// mappings that are not a guess.
    /// </para>
    /// </remarks>
    public static string? ScalarName(ITypeSymbol type)
        => UnwrapNullable(type).SpecialType switch
        {
            SpecialType.System_String => "String",
            SpecialType.System_Boolean => "Boolean",
            SpecialType.System_Byte => "Byte",
            SpecialType.System_Int16 => "Short",
            SpecialType.System_Int32 => "Int",
            SpecialType.System_Int64 => "Long",
            SpecialType.System_Single or SpecialType.System_Double => "Float",
            SpecialType.System_Decimal => "Decimal",
            SpecialType.System_DateTime => "DateTime",
            _ => UnwrapNullable(type).ToDisplayString(_metadataNames) switch
            {
                "System.Guid" => "UUID",
                "System.DateTimeOffset" => "DateTime",
                "System.DateOnly" => "Date",
                _ => null
            }
        };

    /// <summary>
    /// Whether a name can be written into a document unquoted.
    /// </summary>
    /// <remarks>
    /// A field name reaches a variables payload as a JSON string, where anything goes. Written
    /// into the document instead it is a GraphQL name, and the grammar is narrower than
    /// <c>[JsonPropertyName]</c> is: a caller that renames a member to something with a dash in
    /// it has to keep the form that quotes it.
    /// </remarks>
    public static bool IsGraphQLName(string name)
    {
        // The grammar is /[_A-Za-z][_0-9A-Za-z]*/ and nothing wider: char.IsLetter would accept
        // most of Unicode, which a server would not.
        if (name.Length == 0 || (!Letter(name[0]) && name[0] != '_'))
            return false;

        foreach (char c in name)
        {
            if (!Letter(c) && c != '_' && (c < '0' || c > '9'))
                return false;
        }

        return true;

        static bool Letter(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
    }

    /// <summary>
    /// A field that needs no selection set of its own: a scalar, or a list of them.
    /// </summary>
    public static bool IsLeaf(ITypeSymbol type)
    {
        var underlying = UnwrapNullable(type);

        // Asked before the collection is unwrapped: a converter on a type that happens to be
        // enumerable still takes that type whole, and its element is not what arrives.
        if (Converter(underlying).Converter is not null)
            return true;

        return IsScalar(ElementType(underlying) ?? underlying);
    }

    /// <summary>
    /// The same question about a field, which may carry a converter its type does not.
    /// </summary>
    /// <remarks>
    /// <c>[JsonConverter]</c> on the member says how that member is written and read whatever its
    /// type is, so it settles the member alone — the type goes on meaning what it means elsewhere.
    /// </remarks>
    public static bool IsLeaf(IPropertySymbol property)
        => Converter(property).Converter is not null || IsLeaf(property.Type);

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

    /// <summary>
    /// The converter a model declared for a member, on the member or on its type, and whether it
    /// is a factory that has to be asked for the real one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A converter is the model saying that this value is written and read whole. That settles
    /// two questions at once, and they have to be settled the same way: a generated reader reads
    /// the value through the converter rather than field by field, so the document has to ask for
    /// it as one field rather than descend into its properties. Asking for
    /// <c>money { amount }</c> and then reading <c>money</c> through a converter is the two halves
    /// disagreeing, and the server is the one that says so.
    /// </para>
    /// <para>
    /// Whether the JSON the converter writes is a string, a number or an object cannot be known
    /// from here — nothing in this compilation can be run, and a schema that would say is not
    /// something this library has. What can be known is that the model took the value out of the
    /// serializer's hands, and a type that is not shaped by its properties has no selection set
    /// this could write. So a converter means a leaf, and a model that wants the properties
    /// selected individually is one that does not declare a converter over them.
    /// </para>
    /// <para>
    /// Only a converter a reader could actually build counts, for the same reason: one without a
    /// public constructor taking nothing, or deriving from neither <c>JsonConverter&lt;T&gt;</c>
    /// nor <c>JsonConverterFactory</c>, is one <see cref="ResponseStructWriter"/> declines to read
    /// through — and a document that treated it as a leaf anyway would be disagreeing with the
    /// reader again, in the other direction. <c>System.Text.Json</c> refuses such an attribute at
    /// run time too.
    /// </para>
    /// <para>
    /// A converter registered globally instead — on the options rather than on the model — cannot
    /// be seen from here, and keeping those in step is the consumer's to do.
    /// </para>
    /// </remarks>
    public static (INamedTypeSymbol? Converter, bool Factory) Converter(IPropertySymbol property)
    {
        var own = Usable(Declared(property.GetAttributes()));

        return own.Converter is not null ? own : Converter(property.Type);
    }

    /// <summary>The converter a model declared on a type itself, if any.</summary>
    public static (INamedTypeSymbol? Converter, bool Factory) Converter(ITypeSymbol type)
        => Usable(Declared(UnwrapNullable(type).GetAttributes()));

    /// <summary>The type named by a <c>[JsonConverter]</c> among these attributes, if any.</summary>
    private static ITypeSymbol? Declared(
        System.Collections.Immutable.ImmutableArray<AttributeData> attributes)
    {
        foreach (var attribute in attributes)
        {
            if (attribute.AttributeClass?.ToDisplayString()
                    == "System.Text.Json.Serialization.JsonConverterAttribute"
                && attribute.ConstructorArguments.Length == 1
                && attribute.ConstructorArguments[0].Value is ITypeSymbol converter)
                return converter;
        }

        return null;
    }

    /// <summary>A converter as something that can be built and called, or nothing.</summary>
    private static (INamedTypeSymbol? Converter, bool Factory) Usable(ITypeSymbol? declared)
    {
        if (declared is not INamedTypeSymbol converter)
            return (null, false);

        // A converter has to be one this can build: a factory is asked for one, and anything
        // without a constructor taking nothing cannot be had at all.
        if (!converter.InstanceConstructors.Any(x => x.Parameters.Length == 0
            && x.DeclaredAccessibility == Accessibility.Public))
            return (null, false);

        for (var type = converter; type is not null; type = type.BaseType)
        {
            switch (type.ToDisplayString())
            {
                case "System.Text.Json.Serialization.JsonConverterFactory":
                    return (converter, true);

                case string name when name.StartsWith(
                    "System.Text.Json.Serialization.JsonConverter<", StringComparison.Ordinal):
                    return (converter, false);
            }
        }

        return (null, false);
    }
}
