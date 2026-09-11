using System.Diagnostics.CodeAnalysis;
using Feather.GraphQL.Linq.Filtering;
using JetBrains.Annotations;

namespace Feather.GraphQL.Linq.Query;

/// <summary>
/// What a query needs to know about the server that cannot be read off the POCO.
/// </summary>
/// <remarks>
/// <para>
/// The field a type is queried through, the names of its filter and sort inputs, how the server
/// pages it, and which dialect predicates lower to — none of these are properties of the CLR
/// type, they are properties of the schema it happens to model. Stating them at the call site
/// keeps the type a plain POCO that anything else can also use.
/// </para>
/// <para>
/// Mutable on purpose, so a configure delegate reads the way the options pattern does elsewhere.
/// </para>
/// </remarks>
[PublicAPI]
public class GraphQLQueryOptions
{
    /// <summary>
    /// The field on the schema's <c>Query</c> type. Required — a query with no root field has
    /// nothing to ask for, and that is <c>FGQL011</c>.
    /// </summary>
    public string? RootField { get; set; }

    /// <summary>
    /// The GraphQL type of the filter argument. Defaults to <c>{TypeName}FilterInput</c>, which
    /// is what HotChocolate generates.
    /// </summary>
    public string? FilterInput { get; set; }

    /// <summary>
    /// The GraphQL type of the sort argument's elements. Defaults to <c>{TypeName}SortInput</c>.
    /// </summary>
    public string? SortInput { get; set; }

    /// <summary>
    /// How the server wraps this field's result. Cannot be inferred from the CLR type and must
    /// match the server, since it changes both the emitted selection set and the path the
    /// materializer walks.
    /// </summary>
    public PagingKind Paging { get; set; } = PagingKind.None;

    /// <summary>
    /// The dialect predicates are lowered to. HotChocolate's when unset, and per query — two
    /// endpoints in one app may disagree about filter syntax.
    /// </summary>
    public IFilterTranslationProvider? FilterProvider { get; set; }

    /// <summary>Copies the settings this type declares onto <paramref name="target"/>.</summary>
    internal void CopyTo(GraphQLQueryOptions target)
    {
        target.RootField = RootField;
        target.FilterInput = FilterInput;
        target.SortInput = SortInput;
        target.Paging = Paging;
        target.FilterProvider = FilterProvider;
    }

    /// <summary>The filter input name, or the convention when none was given.</summary>
    internal string FilterInputOr([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.None)] Type elementType)
        => FilterInput is { Length: > 0 } name ? name : $"{elementType.Name}FilterInput";

    /// <summary>The sort input name, or the convention when none was given.</summary>
    internal string SortInputOr([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.None)] Type elementType)
        => SortInput is { Length: > 0 } name ? name : $"{elementType.Name}SortInput";
}
