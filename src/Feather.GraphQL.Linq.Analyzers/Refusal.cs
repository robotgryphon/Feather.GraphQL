using Microsoft.CodeAnalysis;

namespace Feather.GraphQL.Linq.Analyzers;

/// <summary>
/// Why a chain could not be compiled, and which part of it is the reason.
/// </summary>
/// <remarks>
/// <para>
/// A decline used to be a sentence about a whole method: the chain was outside what the compiler
/// writes, and the author was left to work out which part of it. That is affordable while a
/// decline costs an optimisation and unaffordable now that it costs the query — so every walk
/// that can refuse says what it refused and where, and the diagnostic is reported against that
/// syntax rather than against the method's name.
/// </para>
/// <para>
/// <see cref="Where"/> is optional because not every refusal has syntax behind it. A declared
/// query's selection set comes from a document in an attribute, where there is nothing to point
/// at; the reason is still worth having, and the method's own name is where it lands.
/// </para>
/// </remarks>
/// <param name="Reason">
/// A fragment completing "'X' cannot be compiled: " — lower case, no trailing stop.
/// </param>
/// <param name="Where">The expression the reason is about, when one was written.</param>
internal sealed record Refusal(string Reason, Location? Where);

/// <summary>
/// Collects the refusal a walk stopped at, so the walk itself can go on answering false.
/// </summary>
/// <remarks>
/// The first one wins. A walk that refuses unwinds through the frames that called it, and each of
/// those may have a refusal of its own to record — the innermost is the one that names the thing
/// that is actually wrong, and the ones above it name the expression it was part of.
/// </remarks>
internal sealed class Refusals
{
    /// <summary>The refusal the walk stopped at, or null when nothing refused.</summary>
    public Refusal? First { get; private set; }

    /// <summary>Records a refusal and answers false, for the walks that answer a bool.</summary>
    public bool No(SyntaxNode where, string reason)
    {
        Note(where, reason);
        return false;
    }

    /// <summary>Records a refusal without answering anything.</summary>
    public void Note(SyntaxNode where, string reason)
        => First ??= new Refusal(reason, where.GetLocation());

    /// <summary>Carries a refusal raised somewhere that had no syntax to hand.</summary>
    public void Note(Refusal? refusal)
    {
        if (refusal is not null)
            First ??= refusal;
    }
}
