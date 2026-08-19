namespace Feather.GraphQL.Linq;

/// <summary>
/// How the server wraps the result of a root field. Determines the selection-set wrapper
/// the translator emits, and which paging operators are legal.
/// </summary>
public enum PagingKind
{
    /// <summary>Un-paged: <c>people { ... }</c>. <c>Skip</c> and <c>Count</c> are unavailable.</summary>
    None = 0,

    /// <summary>HotChocolate <c>[UsePaging]</c>: <c>people { nodes { ... } }</c>. Cursor-based, so <c>Skip</c> is illegal.</summary>
    Cursor = 1,

    /// <summary>HotChocolate <c>[UseOffsetPaging]</c>: <c>people { items { ... } }</c>.</summary>
    Offset = 2
}
