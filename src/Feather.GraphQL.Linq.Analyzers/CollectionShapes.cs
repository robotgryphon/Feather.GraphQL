using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Feather.GraphQL.Linq.Analyzers;

/// <summary>
/// How many of something reaches the member that asked for it.
/// </summary>
/// <remarks>
/// <para>
/// A generated reader accumulates a list as it reads it, because that is the only shape that can
/// be filled without knowing the count in advance. What the member is declared as is the caller's
/// choice, and for a long time the only choice this accepted was <c>T[]</c> — every other way of
/// spelling "many of these" was a decline, which is a strange thing to be told about a model that
/// the serializer it replaced read without complaint.
/// </para>
/// <para>
/// So the accumulated list is converted, by a constructor or a collection expression, and the set
/// of conversions is deliberately the set that can be recognised with certainty: the interfaces a
/// list already satisfies, a constructor that takes one of them, and a collection expression for
/// the types C# can build one of. Anything else is refused and says so, because a conversion
/// guessed wrong here is a reply read into the wrong shape rather than a build that fails.
/// </para>
/// </remarks>
internal static class CollectionShapes
{
    /// <summary>
    /// Whether a generated row can hold many of something as a read-only collection.
    /// </summary>
    /// <remarks>
    /// A row is a payload, not a model: nothing may add to it, and a member typed as something
    /// that can be added to says otherwise. <c>IReadOnlyList</c> rather than
    /// <c>IReadOnlyCollection</c> because it costs nothing and keeps the indexer — it is an
    /// <c>IReadOnlyCollection</c> wherever one is wanted.
    /// </remarks>
    /// <remarks>
    /// Which is only where the member is declared as something a read-only list already is. A row
    /// is read by the projection the caller wrote, verbatim and against the type they declared —
    /// so a member they declared as a <c>List</c> and pass on whole has to still be one, and
    /// narrowing it to be tidy would be a compile error in code nobody wrote. Declaring
    /// <c>IReadOnlyCollection</c> is what buys the read-only row, and costs nothing else.
    /// </remarks>
    public static bool ReadOnlyHolds(ITypeSymbol declared)
        => GraphQLTypeFacts.UnwrapNullable(declared) is INamedTypeSymbol named
            && named.OriginalDefinition.SpecialType
                is SpecialType.System_Collections_Generic_IEnumerable_T
                or SpecialType.System_Collections_Generic_IReadOnlyCollection_T
                or SpecialType.System_Collections_Generic_IReadOnlyList_T;

    /// <summary>The read-only list of a row, spelled out.</summary>
    public static string ReadOnly(string element)
        => "global::System.Collections.Generic.IReadOnlyList<" + element + ">";

    /// <summary>
    /// The expression that turns the accumulated rows into what a member is declared as, or null
    /// when nothing this recognises does.
    /// </summary>
    /// <param name="declared">How the member is declared.</param>
    /// <param name="element">The C# spelling of one row.</param>
    /// <param name="items">The expression holding a <c>List</c> of them.</param>
    public static string? Convert(ITypeSymbol declared, string element, string items)
    {
        var target = GraphQLTypeFacts.UnwrapNullable(declared);

        // The array the reader used to be the only way to fill.
        if (target is IArrayTypeSymbol { Rank: 1 })
            return items + ".ToArray()";

        if (target is not INamedTypeSymbol named)
            return null;

        // A list is already all of these, so the accumulator is handed over as it stands — which
        // is also the whole reason to accumulate into one.
        if (Satisfied(named))
            return items;

        // `[CollectionBuilder]` is the type saying how one of it is built, which is exactly the
        // question, and a collection expression is how C# asks.
        if (Builds(named) || Adds(named))
            return "[.. " + items + "]";

        foreach (var constructor in named.InstanceConstructors)
        {
            if (constructor.DeclaredAccessibility != Accessibility.Public
                || constructor.Parameters.Length != 1)
                continue;

            var takes = constructor.Parameters[0].Type;

            if (takes is INamedTypeSymbol parameter && Satisfied(parameter))
                return "new " + Spelled(named, element) + "(" + items + ")";

            if (takes is IArrayTypeSymbol { Rank: 1 })
                return "new " + Spelled(named, element) + "(" + items + ".ToArray())";
        }

        return null;
    }

    /// <summary>
    /// What a member declared as this holds one of, spelled for a generated file.
    /// </summary>
    /// <remarks>
    /// The rows a generated reader produces may be a struct of its own rather than the type the
    /// member names — a mirror of the payload, which is what a projection runs over — so the
    /// collection is respelled around whatever the rows turned out to be rather than copied from
    /// the member.
    /// </remarks>
    public static string? Spelled(ITypeSymbol declared, string element)
    {
        var target = GraphQLTypeFacts.UnwrapNullable(declared);

        if (target is IArrayTypeSymbol { Rank: 1 })
            return element + "[]";

        if (target is not INamedTypeSymbol named)
            return null;

        if (named.TypeArguments.Length == 1)
        {
            string open = named.OriginalDefinition.ToDisplayString(_qualified);
            int at = open.IndexOf('<');

            return at < 0 ? null : open.Substring(0, at) + "<" + element + ">";
        }

        // Nothing to respell, so the member's own spelling is it — and only where the rows are
        // the type it holds, since a mirror could not be named by it.
        return named.TypeArguments.Length == 0 ? named.ToDisplayString(_qualified) : null;
    }

    /// <summary>Whether a <c>List</c> of rows is already one of these.</summary>
    private static bool Satisfied(INamedTypeSymbol type)
        => type.OriginalDefinition.SpecialType is SpecialType.System_Collections_Generic_IEnumerable_T
                or SpecialType.System_Collections_Generic_ICollection_T
                or SpecialType.System_Collections_Generic_IList_T
                or SpecialType.System_Collections_Generic_IReadOnlyCollection_T
                or SpecialType.System_Collections_Generic_IReadOnlyList_T
            || type.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.List<T>";

    /// <summary>Whether the type says how a collection expression builds one of it.</summary>
    private static bool Builds(INamedTypeSymbol type)
        => type.GetAttributes().Any(attribute => attribute.AttributeClass?.ToDisplayString()
            == "System.Runtime.CompilerServices.CollectionBuilderAttribute");

    /// <summary>
    /// Whether a collection expression can fill one the long way: make it, then add to it.
    /// </summary>
    /// <remarks>
    /// The rule C# applies, asked of the symbols: something enumerable, something to make it with,
    /// and an <c>Add</c> that takes one row. An <c>Add</c> of some other arity or some other type
    /// is not the one a collection expression would call.
    /// </remarks>
    private static bool Adds(INamedTypeSymbol type)
    {
        if (GraphQLTypeFacts.ElementType(type) is not { } element)
            return false;

        if (!type.InstanceConstructors.Any(x => x.Parameters.Length == 0
            && x.DeclaredAccessibility == Accessibility.Public))
            return false;

        foreach (var member in type.GetMembers("Add"))
        {
            if (member is IMethodSymbol { DeclaredAccessibility: Accessibility.Public, Parameters.Length: 1 } add
                && SymbolEqualityComparer.Default.Equals(add.Parameters[0].Type, element))
                return true;
        }

        return false;
    }

    private static readonly SymbolDisplayFormat _qualified =
        SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions
            & ~SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);
}
