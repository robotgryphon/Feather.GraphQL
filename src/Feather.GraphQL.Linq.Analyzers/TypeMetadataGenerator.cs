using System.Collections.Immutable;
using System.Text;
using Feather.GraphQL.Linq.Rules;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Feather.GraphQL.Linq.Analyzers;

/// <summary>
/// Emits the CLR-member → GraphQL-field table for every type this compilation queries, so the
/// translator does not have to reflect for one.
/// </summary>
/// <remarks>
/// <para>
/// The tables register themselves through a module initializer, which runs before any query can.
/// <c>ReflectionTypeMetadata.For</c> goes through <c>GraphQLTypeMetadataRegistry.GetOrAdd</c>, so
/// a generated table is simply found and the reflection path is never entered — no runtime change
/// was needed to prefer one.
/// </para>
/// <para>
/// Reflection remains the fallback for what this cannot see: a queryable whose element type is
/// only known at runtime. That is the same boundary the analyzer draws, and the same one
/// `FGQL006` exists to report.
/// </para>
/// </remarks>
[Generator]
public sealed class TypeMetadataGenerator : IIncrementalGenerator
{
    /// <summary>Fully qualified, and without the <c>?</c> that would make `typeof` invalid.</summary>
    private static readonly SymbolDisplayFormat _typeFormat =
        SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions
            & ~SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var models = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is InvocationExpressionSyntax,
                static (ctx, token) => Collect(ctx, token))
            .Where(static models => !models.IsDefaultOrEmpty)
            .Collect();

        // Contexts are declared in the consumer's own source — they have to be, since STJ's
        // generator cannot see what another generator emits — so finding them is all this does.
        var contexts = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax { BaseList: not null },
                static (ctx, token) => JsonContext(ctx, token))
            .Where(static name => name is not null)
            .Collect();

        // Projections written at a call site, rendered as the C# that replaces compiling them.
        var shapers = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is InvocationExpressionSyntax
                {
                    Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Select" }
                },
                static (ctx, token) => ProjectionShaper.From(
                    ctx.SemanticModel, (InvocationExpressionSyntax)ctx.Node, token))
            .Where(static shaper => shaper is not null)
            .Collect();

        context.RegisterSourceOutput(
            models.Combine(contexts).Combine(shapers),
            static (source, pair) => Emit(source, pair.Left.Left, pair.Left.Right!, pair.Right!));
    }

    /// <summary>
    /// The fully qualified name of a <c>JsonSerializerContext</c> declared here, if this is one.
    /// </summary>
    private static string? JsonContext(GeneratorSyntaxContext context, CancellationToken token)
    {
        if (context.SemanticModel.GetDeclaredSymbol((ClassDeclarationSyntax)context.Node, token)
            is not INamedTypeSymbol { IsAbstract: false } type)
            return null;

        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString() == "System.Text.Json.Serialization.JsonSerializerContext")
                return type.ToDisplayString(_typeFormat);
        }

        return null;
    }

    /// <summary>
    /// Finds the types one call site queries, and everything reachable from them.
    /// </summary>
    private static ImmutableArray<TypeModel> Collect(GeneratorSyntaxContext context, CancellationToken token)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;

        if (context.SemanticModel.GetSymbolInfo(invocation, token).Symbol is not IMethodSymbol method)
            return ImmutableArray<TypeModel>.Empty;

        var roots = Roots(method);
        if (roots.Count == 0)
            return ImmutableArray<TypeModel>.Empty;

        var found = new Dictionary<string, TypeModel>(StringComparer.Ordinal);
        foreach (var root in roots)
            Walk(root, found, token);

        return [.. found.Values];
    }

    /// <summary>
    /// The types a single call names. An entry point names its element type; a filter-shape
    /// <c>Where</c> names the input it is written against; the §4 extensions name the element of
    /// whatever they were called on.
    /// </summary>
    private static List<ITypeSymbol> Roots(IMethodSymbol method)
    {
        var roots = new List<ITypeSymbol>();

        string? container = Outermost(method.ContainingType)?.ToDisplayString();

        bool isEntryPoint = EntryPoints.IsEntryPoint(method);

        bool isFilterSurface = method.Name is "Where"
                && container is "Feather.GraphQL.Linq.Filtering.GraphQLWhereExtensions"
            || method.Name is "ToGraphQLFilter" or "ToGraphQLSort" or "ToGraphQLArguments"
                && container is "Feather.GraphQL.Linq.Filtering.GraphQLFilterExtensions";

        if (!isEntryPoint && !isFilterSurface)
            return roots;

        // Every type argument the call carries: the element type, and a filter shape when the
        // predicate was written against one.
        foreach (var argument in method.TypeArguments)
            roots.Add(argument);

        // The receiver's element type, for an extension called on IQueryable<T>.
        if (GraphQLTypeFacts.ElementType(method.ReceiverType!) is { } element)
            roots.Add(element);

        return roots;
    }

    /// <summary>
    /// Collects a type's field table, then the tables of everything it nests into.
    /// </summary>
    /// <remarks>
    /// Base types come along because a member's table is looked up by its <em>declaring</em>
    /// type, which for an inherited property is not the type that was queried.
    /// </remarks>
    private static void Walk(ITypeSymbol? type, Dictionary<string, TypeModel> found, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        if (type is null)
            return;

        var target = GraphQLTypeFacts.Unwrap(type);

        if (target is not INamedTypeSymbol named
            || target.TypeKind is TypeKind.Interface or TypeKind.Error or TypeKind.TypeParameter
            || GraphQLTypeFacts.IsScalar(target)
            || named.IsUnboundGenericType
            || named.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal)
            // object, ValueType, Enum and friends: a struct's base chain ends in them, and a
            // field table for them would be empty and meaningless.
            || named.SpecialType is not SpecialType.None)
            return;

        string qualified = named.ToDisplayString(_typeFormat);
        if (found.ContainsKey(qualified))
            return;

        var fields = ImmutableArray.CreateBuilder<FieldModel>();
        foreach (var property in GraphQLTypeFacts.Properties(named))
        {
            fields.Add(new FieldModel(
                property.Name,
                GraphQLTypeFacts.FieldName(property),
                property.Type.ToDisplayString(_typeFormat),
                GraphQLTypeFacts.IsIgnored(property)));
        }

        // Recorded before recursing, so a type that points back at itself terminates.
        found[qualified] = new TypeModel(qualified, Identifier(qualified), fields.ToImmutable());

        Walk(named.BaseType, found, token);

        foreach (var property in GraphQLTypeFacts.Properties(named))
            Walk(property.Type, found, token);
    }

    private static INamedTypeSymbol? Outermost(INamedTypeSymbol? type)
    {
        while (type?.ContainingType is not null)
            type = type.ContainingType;

        return type;
    }

    /// <summary>A C# identifier from a fully qualified name, unique to it.</summary>
    private static string Identifier(string qualified)
    {
        var builder = new StringBuilder(qualified.Length);
        foreach (char c in qualified)
            builder.Append(char.IsLetterOrDigit(c) ? c : '_');

        return builder.ToString().TrimStart('_');
    }

    private static void Emit(
        SourceProductionContext context,
        ImmutableArray<ImmutableArray<TypeModel>> collected,
        ImmutableArray<string?> contexts,
        ImmutableArray<ShaperModel?> shapers)
    {
        var unique = new SortedDictionary<string, TypeModel>(StringComparer.Ordinal);
        foreach (var batch in collected)
        {
            foreach (var model in batch)
                unique[model.QualifiedName] = model;
        }

        var jsonContexts = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string? name in contexts)
        {
            if (name is not null)
                jsonContexts.Add(name);
        }

        var projections = new SortedDictionary<string, ShaperModel>(StringComparer.Ordinal);
        foreach (var shaper in shapers)
        {
            if (shaper is not null)
                projections[shaper.Key] = shaper;
        }

        if (unique.Count == 0 && jsonContexts.Count == 0 && projections.Count == 0)
            return;

        var source = new StringBuilder();
        source.AppendLine("// <auto-generated/>");
        source.AppendLine("#nullable enable");
        source.AppendLine();
        source.AppendLine("namespace Feather.GraphQL.Linq.Generated");
        source.AppendLine("{");

        foreach (var model in unique.Values)
            AppendMetadata(source, model);

        source.AppendLine("    /// <summary>Registers the generated tables, and any JSON context, before a query runs.</summary>");
        source.AppendLine("    internal static class GraphQLGeneratedMetadata");
        source.AppendLine("    {");
        source.AppendLine("        [global::System.Runtime.CompilerServices.ModuleInitializer]");
        source.AppendLine("        internal static void Register()");
        source.AppendLine("        {");

        foreach (var model in unique.Values)
        {
            source.Append("            global::Feather.GraphQL.Linq.Metadata.GraphQLTypeMetadataRegistry.Register(")
                .Append(model.HintName).AppendLine("Metadata.Instance);");
        }

        foreach (string name in jsonContexts)
        {
            source.Append("            global::Feather.GraphQL.Linq.Metadata.GraphQLJsonContextRegistry.Register(")
                .Append(name).AppendLine(".Default);");
        }

        foreach (var shaper in projections.Values)
        {
            source.AppendLine();
            source.AppendLine("            global::Feather.GraphQL.Linq.Metadata.GraphQLProjectionRegistry.Register(");
            source.Append("            \"").Append(Escape(shaper.Key)).AppendLine("\",");
            source.Append("            ").Append(shaper.Body).AppendLine(");");
        }

        source.AppendLine("        }");
        source.AppendLine("    }");
        source.AppendLine("}");

        context.AddSource("GraphQLGeneratedMetadata.g.cs", source.ToString());

    }

    private static void AppendMetadata(StringBuilder source, TypeModel model)
    {
        const string FIELD = "global::Feather.GraphQL.Linq.Metadata.GraphQLFieldMetadata";

        source.Append("    /// <summary>Field table for <c>").Append(Escape(model.QualifiedName))
            .AppendLine("</c>.</summary>");
        source.Append("    internal sealed class ").Append(model.HintName)
            .AppendLine("Metadata : global::Feather.GraphQL.Linq.Metadata.IGraphQLTypeMetadata");
        source.AppendLine("    {");
        source.Append("        internal static readonly ").Append(model.HintName).Append("Metadata Instance = new ")
            .Append(model.HintName).AppendLine("Metadata();");
        source.AppendLine();
        source.Append("        private static readonly ").Append(FIELD).AppendLine("[] _fields =");
        source.AppendLine("        {");

        foreach (var field in model.Fields)
        {
            source.Append("            new ").Append(FIELD).Append("(\"").Append(field.ClrName)
                .Append("\", \"").Append(Escape(field.FieldName)).Append("\", typeof(").Append(field.ClrType)
                .Append("), ").Append(field.IsIgnored ? "true" : "false").AppendLine("),");
        }

        source.AppendLine("        };");
        source.AppendLine();
        source.Append("        public global::System.Type ClrType => typeof(").Append(model.QualifiedName)
            .AppendLine(");");
        source.AppendLine();
        source.Append("        public global::System.Collections.Generic.IReadOnlyList<").Append(FIELD)
            .AppendLine("> Fields => _fields;");
        source.AppendLine();
        // The attribute is part of the signature the interface declares; without it the
        // implementation does not match and the consumer's compile fails.
        source.Append("        public bool TryGetField(string clrName, ")
            .Append("[global::System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ")
            .Append(FIELD).AppendLine("? field)");
        source.AppendLine("        {");
        source.AppendLine("            switch (clrName)");
        source.AppendLine("            {");

        for (int i = 0; i < model.Fields.Length; i++)
        {
            source.Append("                case \"").Append(model.Fields[i].ClrName).AppendLine("\":");
            source.Append("                    field = _fields[").Append(i).AppendLine("];");
            source.AppendLine("                    return true;");
        }

        source.AppendLine("                default:");
        source.AppendLine("                    field = null;");
        source.AppendLine("                    return false;");
        source.AppendLine("            }");
        source.AppendLine("        }");
        source.AppendLine("    }");
        source.AppendLine();
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
