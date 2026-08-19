using System.Linq.Expressions;

namespace Feather.GraphQL.Linq;

/// <summary>
/// Thrown when a LINQ expression cannot be translated to GraphQL.
/// </summary>
/// <remarks>
/// Carries the same diagnostic ID the analyzer would have reported at compile time. The
/// analyzer is a pre-flight of these rules, not a separate rule set — when it can follow a
/// chain it fails the build, and when it cannot (FGQL006) this is the fallback.
/// </remarks>
public sealed class GraphQLTranslationException : NotSupportedException
{
    /// <summary>The diagnostic ID, e.g. <c>FGQL002</c>.</summary>
    public string DiagnosticId { get; }

    /// <summary>The expression that could not be translated, when one is available.</summary>
    public Expression? Expression { get; }

    public GraphQLTranslationException(string diagnosticId, string message, Expression? expression = null)
        : base($"{diagnosticId}: {message}")
    {
        DiagnosticId = diagnosticId;
        Expression = expression;
    }

    internal static GraphQLTranslationException UnsupportedOperator(string method, Expression? expression = null)
        => new("FGQL001", $"The LINQ operator '{method}' has no GraphQL translation.", expression);

    internal static GraphQLTranslationException UnsupportedPredicate(string detail, Expression? expression = null)
        => new("FGQL002", $"Unsupported expression in a predicate: {detail}.", expression);

    internal static GraphQLTranslationException NotLowerable(string detail, Expression? expression = null)
        => new("FGQL003", $"The Where clause cannot be lowered to a filter input: {detail}.", expression);

    internal static GraphQLTranslationException BadOrderingKey(string detail, Expression? expression = null)
        => new("FGQL004", $"Ordering key is not a member access: {detail}.", expression);

    internal static GraphQLTranslationException IgnoredMember(string clrName, Type declaringType)
        => new("FGQL005",
            $"'{declaringType.Name}.{clrName}' is marked [JsonIgnore] and is not a GraphQL field.");
}
