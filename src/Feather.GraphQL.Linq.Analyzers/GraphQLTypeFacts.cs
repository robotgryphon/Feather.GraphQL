using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Feather.GraphQL.Linq.Analyzers;

/// <summary>
/// The translator's notion of "scalar" and "field", read from symbols instead of from
/// <see cref="System.Type"/>.
/// </summary>
/// <remarks>
/// A deliberate second implementation of one rule. The translator cannot run at compile time and
/// the compiler has no <see cref="System.Type"/>, so the choice is between duplicating the rule
/// and not having it early. Keeping the two in one file each, with the same names, is what keeps
/// them honest.
/// </remarks>
internal static class GraphQLTypeFacts
{
    private static readonly ImmutableHashSet<string> _scalarTypes = ImmutableHashSet.Create(
        "System.String", "System.Decimal", "System.Guid", "System.DateTime",
        "System.DateTimeOffset", "System.DateOnly", "System.TimeOnly", "System.TimeSpan",
        "System.Uri");

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
    /// A value that needs no selection set. Mirrors <c>SelectionSetBuilder.IsScalar</c>.
    /// </summary>
    public static bool IsScalar(ITypeSymbol type)
    {
        var underlying = UnwrapNullable(type);

        if (underlying.TypeKind == TypeKind.Enum || underlying.TypeKind == TypeKind.Error)
            return true;

        switch (underlying.SpecialType)
        {
            case SpecialType.System_Boolean:
            case SpecialType.System_Byte:
            case SpecialType.System_SByte:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
            case SpecialType.System_Single:
            case SpecialType.System_Double:
            case SpecialType.System_Char:
            case SpecialType.System_String:
            case SpecialType.System_Decimal:
                return true;
        }

        return _scalarTypes.Contains(underlying.ToDisplayString());
    }

    /// <summary>
    /// A field that needs no selection set of its own: a scalar, or a list of them.
    /// </summary>
    public static bool IsLeaf(ITypeSymbol type)
    {
        var underlying = UnwrapNullable(type);

        return IsScalar(ElementType(underlying) ?? underlying);
    }

    /// <summary>The properties the translator would map, in declaration order.</summary>
    public static IEnumerable<IPropertySymbol> Fields(ITypeSymbol type)
    {
        foreach (var member in type.GetMembers())
        {
            if (member is not IPropertySymbol
                {
                    DeclaredAccessibility: Accessibility.Public,
                    IsStatic: false,
                    IsIndexer: false
                } property)
                continue;

            if (IsIgnored(property))
                continue;

            yield return property;
        }
    }

    private static bool IsIgnored(IPropertySymbol property)
    {
        foreach (var attribute in property.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString()
                == "System.Text.Json.Serialization.JsonIgnoreAttribute")
                return true;
        }

        return false;
    }
}
