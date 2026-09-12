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

    /// <summary>A chain marked for compilation that the compiler could not compile.</summary>
    /// <remarks>
    /// Reported because the attribute would otherwise do nothing at all, silently: the method
    /// keeps its body and the body keeps working, so the only visible consequence of a declined
    /// chain is the saving that did not happen. The message names the reason, because the fix is
    /// almost always to change the chain rather than to remove the attribute.
    /// </remarks>
    public static readonly DiagnosticDescriptor NotCompiled = new(
        "FGQL015",
        "This query was not compiled",
        "'{0}' is marked [GraphQLQuery] but was left to the runtime: {1}. The method still "
        + "works as written; it just composes its chain on every call.",
        CATEGORY,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A compiled query is one whose document and payload the compiler could write "
        + "out in full. What it can write out is bounded, and a chain outside those bounds keeps "
        + "the runtime translation it would have had without the attribute.");

    /// <summary>A declared query whose reply the compiler could not model.</summary>
    /// <remarks>
    /// An error rather than a warning, and that is the difference between this surface and the
    /// compiled one. A chain that cannot be compiled still has a body to run; a declared method
    /// has nothing but what the compiler writes for it, so a reply that cannot be read leaves the
    /// method with no implementation at all. Saying why here is better than letting the compiler
    /// report the missing half.
    /// </remarks>
    public static readonly DiagnosticDescriptor NotModelled = new(
        "FGQL016",
        "This query's reply cannot be read",
        "'{0}' cannot be implemented: {1}. Change the document or the return type, or read the "
        + "reply yourself with SendGraphQLQueryAsync and ReadGraphQLAsync.",
        CATEGORY,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A declared query is implemented by a reader written from its document and "
        + "its return type. Where the two do not describe a reply that can be read, there is "
        + "nothing to write and the method would be left unimplemented.");
}