using System.Collections.Concurrent;
using System.Linq.Expressions;
using Feather.GraphQL.Linq.Execution;
using JetBrains.Annotations;

namespace Feather.GraphQL.Linq.Metadata;

/// <summary>
/// Where a generated shaper registers itself — the compiled stand-in for a <c>Select</c>.
/// </summary>
/// <remarks>
/// <para>
/// Applying a projection meant <c>LambdaExpression.Compile()</c>, which is runtime IL
/// generation: the one thing in the materialization path that NativeAOT cannot do at all, worse
/// than reflection rather than a milder form of it. A shaper is that same projection written
/// out as ordinary C# at build time.
/// </para>
/// <para>
/// A projection with no registered shaper is still compiled, so nothing has to be covered for
/// the library to work. Missing a shaper costs speed; that is the only thing it costs.
/// </para>
/// </remarks>
[PublicAPI]
public static class GraphQLProjectionRegistry
{
    private static readonly ConcurrentDictionary<string, Func<object?, object?>> _shapers =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Registers the compiled form of one projection.
    /// </summary>
    /// <param name="key">
    /// The projection's key, as the generator computed it. It has to match what
    /// <c>ProjectionKey</c> derives from the expression tree, or the shaper is simply never
    /// found — which is a slow query rather than a wrong one.
    /// </param>
    /// <param name="shaper">The projection, as a function of the materialized element.</param>
    public static void Register(string key, Func<object?, object?> shaper)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(shaper);

        _shapers[key] = shaper;
    }

    /// <summary>The shaper for a projection, or null when none was generated for it.</summary>
    internal static Func<object?, object?>? Find(LambdaExpression projection)
        => ProjectionKey.For(projection) is { } key && _shapers.TryGetValue(key, out var shaper)
            ? shaper
            : null;

    /// <summary>How many shapers are registered. For tests that assert coverage.</summary>
    public static int Count => _shapers.Count;
}
