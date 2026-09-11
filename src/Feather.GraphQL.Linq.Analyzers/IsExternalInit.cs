namespace System.Runtime.CompilerServices;

/// <summary>
/// Polyfill: <c>init</c> accessors need this type, and netstandard2.0 — the target an analyzer
/// must be built for — does not ship it.
/// </summary>
internal static class IsExternalInit;
