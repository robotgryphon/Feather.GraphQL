using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Feather.GraphQL.Linq.Analyzers;

/// <summary>One projection, as a key and the C# that replaces compiling it.</summary>
internal sealed class ShaperModel(string key, string body) : IEquatable<ShaperModel>
{
    public string Key { get; } = key;

    /// <summary>The shaper's body — a lambda taking the materialized element.</summary>
    public string Body { get; } = body;

    public bool Equals(ShaperModel? other) => other is not null && Key == other.Key && Body == other.Body;
    public override bool Equals(object? obj) => Equals(obj as ShaperModel);
    public override int GetHashCode() => unchecked((Key.GetHashCode() * 397) ^ Body.GetHashCode());
}

/// <summary>
/// Renders a <c>Select</c> written at a call site into ordinary C#, and into the key the runtime
/// will look for.
/// </summary>
/// <remarks>
/// <para>
/// Only the shapes the translator already allows are rendered: an anonymous type built from
/// member paths, or a bare member path. Computation inside a projection is <c>FGQL013</c>, and
/// anything else — a constructor call, a nested LINQ chain — returns null and is left to be
/// compiled at runtime.
/// </para>
/// <para>
/// The rendered anonymous type is the <em>same</em> type as the one at the call site: C# unifies
/// anonymous types with matching property names, types and order within an assembly, which is
/// what lets generated code hand back something the caller can consume.
/// </para>
/// </remarks>
internal static class ProjectionShaper
{
    private static readonly SymbolDisplayFormat _qualified =
        SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions
            & ~SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    public static ShaperModel? From(
        SemanticModel model,
        InvocationExpressionSyntax invocation,
        CancellationToken token)
    {
        if (model.GetSymbolInfo(invocation, token).Symbol is not IMethodSymbol
            {
                Name: "Select",
                ContainingType.Name: "Queryable"
            })
            return null;

        if (!EntryPoints.StartsAtEntryPoint(model, invocation, token))
            return null;

        if (invocation.ArgumentList.Arguments.Count != 1
            || invocation.ArgumentList.Arguments[0].Expression is not LambdaExpressionSyntax lambda)
            return null;

        return From(model, lambda, token);
    }

    /// <summary>
    /// The same, from the projection's lambda alone.
    /// </summary>
    /// <remarks>
    /// The interceptor generator holds a chain's projection as a lambda rather than as the call
    /// it was written in, and needs the key to say which shaper a precompiled plan should use.
    /// Both go through the same renderer, so a key emitted in a plan is the key the shaper
    /// registered itself under.
    /// </remarks>
    public static ShaperModel? From(
        SemanticModel model,
        LambdaExpressionSyntax lambda,
        CancellationToken token)
    {
        if (lambda.Body is not ExpressionSyntax body)
            return null;

        if (model.GetSymbolInfo(lambda, token).Symbol is not IMethodSymbol { Parameters.Length: 1 } projection)
            return null;

        var parameter = projection.Parameters[0];

        // Type.FullName is what the runtime key is built from, so the compile-time half has to
        // spell the type the same way — namespace and containing types, not display syntax.
        if (MetadataName(parameter.Type) is not { } source)
            return null;

        var key = new StringBuilder(source).Append("=>");
        var rendered = new StringBuilder();

        if (!Render(model, body, parameter, key, rendered, token))
            return null;

        var shaper = new StringBuilder()
            .AppendLine("static source =>")
            .AppendLine("            {")
            .Append("                var src = (").Append(parameter.Type.ToDisplayString(_qualified))
            .AppendLine(")source!;")
            .Append("                return ").Append(rendered).AppendLine(";")
            .Append("            }");

        return new ShaperModel(key.ToString(), shaper.ToString());
    }

    private static bool Render(
        SemanticModel model,
        ExpressionSyntax node,
        IParameterSymbol parameter,
        StringBuilder key,
        StringBuilder rendered,
        CancellationToken token)
    {
        switch (node)
        {
            case ParenthesizedExpressionSyntax parenthesized:
                return Render(model, parenthesized.Expression, parameter, key, rendered, token);

            // A named type built and then filled, which is what most projections into a type of
            // one's own look like. The type is named in the key because two types filled with the
            // same members are two shapes; an anonymous one needs no name, since matching members
            // make it the same type already.
            case ObjectCreationExpressionSyntax creation:
            {
                if (creation.ArgumentList is { Arguments.Count: > 0 }
                    || creation.Initializer is not { } initializer
                    || initializer.Expressions.Count == 0
                    || model.GetTypeInfo(creation, token).Type is not { } target
                    || MetadataName(target) is not { } name)
                    return false;

                key.Append("new").Append(name).Append('{');
                rendered.Append("new ").Append(target.ToDisplayString(_qualified)).Append(" { ");

                for (int i = 0; i < initializer.Expressions.Count; i++)
                {
                    if (initializer.Expressions[i] is not AssignmentExpressionSyntax assignment
                        || assignment.Left is not IdentifierNameSyntax member)
                        return false;

                    if (i > 0)
                    {
                        key.Append(',');
                        rendered.Append(", ");
                    }

                    key.Append(member.Identifier.ValueText).Append(':');
                    rendered.Append(member.Identifier.ValueText).Append(" = ");

                    // A member may be filled from another object built the same way, so this
                    // recurses where the anonymous case only ever takes a path.
                    if (!Render(model, assignment.Right, parameter, key, rendered, token))
                        return false;
                }

                key.Append('}');
                rendered.Append(" }");

                return true;
            }

            case AnonymousObjectCreationExpressionSyntax anonymous:
            {
                key.Append("new{");
                rendered.Append("new { ");

                for (int i = 0; i < anonymous.Initializers.Count; i++)
                {
                    var initializer = anonymous.Initializers[i];

                    if (Name(initializer) is not { } name)
                        return false;

                    if (i > 0)
                    {
                        key.Append(',');
                        rendered.Append(", ");
                    }

                    key.Append(name).Append(':');
                    rendered.Append(name).Append(" = ");

                    if (!RenderPath(initializer.Expression, parameter, key, rendered))
                        return false;
                }

                key.Append('}');
                rendered.Append(" }");
                return anonymous.Initializers.Count > 0;
            }

            default:
                return RenderPath(node, parameter, key, rendered);
        }
    }

    /// <summary>Writes a member chain both as a dotted key and as an access off <c>src</c>.</summary>
    private static bool RenderPath(
        ExpressionSyntax node,
        IParameterSymbol parameter,
        StringBuilder key,
        StringBuilder rendered)
    {
        var path = new List<string>();
        var current = node;

        while (true)
        {
            switch (current)
            {
                case ParenthesizedExpressionSyntax parenthesized:
                    current = parenthesized.Expression;
                    continue;

                case PostfixUnaryExpressionSyntax
                    { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression } suppress:
                    current = suppress.Operand;
                    continue;

                case MemberAccessExpressionSyntax member:
                    path.Insert(0, member.Name.Identifier.ValueText);
                    current = member.Expression;
                    continue;

                case IdentifierNameSyntax identifier when identifier.Identifier.ValueText == parameter.Name:
                {
                    if (path.Count == 0)
                        return false;

                    key.Append(string.Join(".", path));
                    rendered.Append("src");

                    foreach (string segment in path)
                        rendered.Append('.').Append(segment);

                    return true;
                }

                default:
                    return false;
            }
        }
    }

    /// <summary>The property name an initializer takes: written out, or inferred from the member.</summary>
    private static string? Name(AnonymousObjectMemberDeclaratorSyntax initializer)
        => initializer.NameEquals?.Name.Identifier.ValueText
            ?? (initializer.Expression as MemberAccessExpressionSyntax)?.Name.Identifier.ValueText;

    /// <summary>
    /// The type as <see cref="Type.FullName"/> spells it: dotted namespace, then containing
    /// types joined with <c>+</c>. Generic and nested-in-generic types are declined rather than
    /// guessed at.
    /// </summary>
    private static string? MetadataName(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol { IsGenericType: false } named)
            return null;

        var names = new List<string>();
        for (var current = named; current is not null; current = current.ContainingType)
            names.Insert(0, current.Name);

        string ns = named.ContainingNamespace is { IsGlobalNamespace: false } space
            ? space.ToDisplayString() + "."
            : string.Empty;

        return ns + string.Join("+", names);
    }
}
