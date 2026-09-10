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
}
