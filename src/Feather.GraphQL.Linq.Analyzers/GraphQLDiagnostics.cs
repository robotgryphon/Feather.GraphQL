using Microsoft.CodeAnalysis;

namespace Feather.GraphQL.Linq.Analyzers;

/// <summary>
/// The compile-time half of the rule table the translator enforces at runtime.
/// </summary>
/// <remarks>
/// Same IDs, same meanings. A rule that can be decided from the source is decided here, where
/// it costs a red squiggle rather than an exception on the first request.
/// </remarks>
internal static class GraphQLDiagnostics
{
    private const string CATEGORY = "Feather.GraphQL";

    /// <summary>A projection of a member that has no scalar fields to select automatically.</summary>
    public static readonly DiagnosticDescriptor NeedsProjection = new(
        "FGQL014",
        "This field has nothing to select automatically",
        "'{0}' cannot be selected on its own: '{1}' has no scalar fields, and its nested ones are "
        + "not selected by default. Say what to take from it with a nested Select, or name the "
        + "fields with Include() once it lands.",
        CATEGORY,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Naming an object member selects that member's own scalar fields. A type with "
        + "none of them has nothing to contribute, and a GraphQL selection set cannot be empty.");

    /// <summary>A chain that names no Where, Take or Select, and so asks for every record.</summary>
    /// <remarks>
    /// A warning rather than an error, and the distinction is the rule's whole content: such a
    /// query is valid, runs, and fills in the queried type's scalar fields — which is sometimes
    /// exactly what was wanted. It is reported because it is far more often a forgotten
    /// predicate, and because the cost of the mistake is paid by the server rather than here.
    /// </remarks>
    public static readonly DiagnosticDescriptor Unbounded = new(
        "FGQL012",
        "This query asks for every record",
        "'{0}' has no Where, Take or Select, so it requests every record the field returns, with "
        + "every scalar field of '{1}' filled in. Add one of the three if that is not what you "
        + "meant.",
        CATEGORY,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A query with no predicate, no page and no projection fetches the whole "
        + "collection. That is a legitimate thing to ask for and runs as written, but it is more "
        + "often a predicate that was left off.");
}
