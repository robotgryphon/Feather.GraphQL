using JetBrains.Annotations;

namespace Feather.GraphQL.Linq.Query;

/// <summary>
/// What the server calls the arguments a chain translates to.
/// </summary>
/// <remarks>
/// <para>
/// The defaults are HotChocolate's. They are not universal — the public countries API names its
/// filter <c>filter</c>, not <c>where</c> — and an argument name is a fact about one schema, so
/// it belongs next to the query rather than baked into the translator.
/// </para>
/// <para>
/// Only the names change. What goes <em>inside</em> the filter is a different question, answered
/// by <see cref="Filtering.IFilterTranslationProvider"/>.
/// </para>
/// </remarks>
[PublicAPI]
public sealed record GraphQLArgumentNames
{
    /// <summary>HotChocolate's names, used when a chain says nothing.</summary>
    public static GraphQLArgumentNames Default { get; } = new();

    /// <summary>The filter argument. <c>where</c> by default.</summary>
    public string Filter { get; init; } = "where";

    /// <summary>The sort argument. <c>order</c> by default.</summary>
    public string Order { get; init; } = "order";

    /// <summary>Offset paging's limit, and the un-paged one. <c>take</c> by default.</summary>
    public string Take { get; init; } = "take";

    /// <summary>Cursor paging's forward limit. <c>first</c> by default.</summary>
    public string First { get; init; } = "first";

    /// <summary>Offset paging's offset. <c>skip</c> by default.</summary>
    public string Skip { get; init; } = "skip";

    /// <summary>Cursor paging's backward limit, used by <c>Last()</c>. <c>last</c> by default.</summary>
    public string Last { get; init; } = "last";
}
