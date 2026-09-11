using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Feather.GraphQL.Linq.Analyzers;

/// <summary>
/// Prints each recognised query's document at compile time and hands it to the chain through an
/// interceptor on its entry point.
/// </summary>
/// <remarks>
/// <para>
/// The document a chain sends depends only on the chain's <em>shape</em> — every value it binds
/// goes into the variables payload as <c>$v0</c>, <c>$v1</c> — so it can be printed before any
/// value exists. That is what makes this possible at all, and it is also its limit: the payload
/// still has to be built at runtime, because that is where the values are.
/// </para>
/// <para>
/// Interception happens at the entry point rather than at the terminal because the entry point
/// creates the provider, and a provider is scoped to exactly one chain. The generated call does
/// nothing but attach a string.
/// </para>
/// <para>
/// Requires the consuming project to allow this namespace as an interceptor namespace; the
/// package's build props does that. Without it the compiler reports the interceptor and nothing
/// is precompiled — which is a build error, not silent wrongness.
/// </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class QueryInterceptorGenerator : IIncrementalGenerator
{
    private const string Namespace = "Feather.GraphQL.Linq.Generated";

    /// <summary>One chain, reduced to the strings needed to emit it.</summary>
    private sealed record Interception(
        string Attribute,
        string TypeParameter,
        string Constraints,
        string Parameters,
        string Using,
        string Forward,
        string Document,
        StaticPlan? Plan);

    /// <summary>
    /// The rest of a plan, for a chain that binds nothing at runtime.
    /// </summary>
    /// <remarks>
    /// A document is only half of what executing a chain needs; the other half is the root field,
    /// how it pages and which terminal reduced it. Those are as knowable at compile time as the
    /// document is — but only when the chain binds no values, because a bound value lives in the
    /// expression tree and reading it is the whole reason the tree is walked at all.
    /// </remarks>
    private sealed record StaticPlan(
        string RootField,
        int Paging,
        int ResultOperator,
        int? Page,
        string? ProjectionKey,
        FilterSkeletonModel? Filter);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var interceptions = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is InvocationExpressionSyntax
                {
                    Expression: MemberAccessExpressionSyntax
                    {
                        Name: GenericNameSyntax { Identifier.ValueText: "CreateQueryable" or "For" }
                    }
                },
                static (context, token) => Describe(context, token))
            .Where(static x => x is not null)
            .Collect();

        context.RegisterSourceOutput(interceptions, static (context, found) => Emit(context, found!));
    }

    private static Interception? Describe(GeneratorSyntaxContext context, CancellationToken token)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;

        if (context.SemanticModel.GetSymbolInfo(invocation, token).Symbol is not IMethodSymbol method
            || !EntryPoints.IsEntryPoint(method))
            return null;

        var completions = QueryChainReader.Read(invocation, method, context.SemanticModel, token);
        if (completions is null)
            return null;

        // A chain used in several places shares one provider, so one document has to serve them
        // all. Where they disagree — a `First()` here and a `ToArray()` there — there is no such
        // document, and the runtime translates each use on its own as it always did.
        string? document = null;

        foreach (var facts in completions)
        {
            string? printed = QueryDocumentWriter.TryWrite(facts, context.SemanticModel, token);

            if (printed is null || (document is not null && printed != document))
                return null;

            document = printed;
        }

        if (document is null)
            return null;

        var location = context.SemanticModel.GetInterceptableLocation(invocation, token);
        if (location is null)
            return null;

        return Signature(method, location.GetInterceptsLocationAttributeSyntax(), document,
            Plan(completions, context.SemanticModel, token));
    }

    /// <summary>
    /// Builds the interceptor's signature from the entry point's own, which is the only way it
    /// will match: an interceptor has to look exactly like what it replaces.
    /// </summary>
    /// <summary>
    /// The whole plan for this chain, when every use of it can be described without running it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every use has to agree, for the reason the document does: one provider serves them all.
    /// A chain that binds anything — a predicate, an ordering, a page, or a terminal that asks
    /// the server for one row — is excluded, because the values it binds are only in the
    /// expression tree. So is a projection, whose lambda the materializer still needs.
    /// </para>
    /// <para>
    /// The enums are passed as their numbers. Both are mirrored between this assembly and the
    /// runtime's, and a test asserts they still line up — a reordering would otherwise turn every
    /// precompiled plan quietly wrong.
    /// </para>
    /// </remarks>
    private static StaticPlan? Plan(
        IReadOnlyList<ChainFacts> completions,
        SemanticModel model,
        CancellationToken token)
    {
        StaticPlan? agreed = null;

        foreach (var facts in completions)
        {
            // A predicate, an ordering, an offset or a page the chain sized itself all bind a
            // value that only exists once the expression tree is walked. A page a *terminal*
            // asked for does not: First means one, Single means two, and that is decided here.
            if (facts.HasOrdering || facts.HasSkip || facts.ExplicitTake)
                return null;

            string? projection = null;

            if (facts.Projection is not null)
            {
                // The key the shaper registered itself under. Without one the materializer would
                // need the lambda, which is the thing this exists to avoid walking for.
                if (ProjectionShaper.From(model, facts.Projection, token) is not { } shaper)
                    return null;

                projection = shaper.Key;
            }

            // A filter whose shape the compiler can print leaves only its values to be read at
            // runtime. One that it cannot is not a refusal — the chain keeps its runtime lowering.
            var filter = facts.HasFilter && !facts.HasOpaquePredicate
                ? FilterSkeleton.From(facts.Predicates, model, token)
                : null;

            if (facts.HasFilter && filter is null)
                return null;

            var plan = new StaticPlan(
                facts.RootField, (int)facts.Paging, (int)facts.Result, facts.ResultPage, projection, filter);

            if (agreed is not null && (agreed with { Filter = null }) != (plan with { Filter = null }))
                return null;

            if (agreed is { Filter: not null } && plan.Filter?.Body != agreed.Filter.Body)
                return null;

            agreed = plan;
        }

        return agreed;
    }

    private static Interception? Signature(
        IMethodSymbol method,
        string attribute,
        string document,
        StaticPlan? plan)
    {
        var definition = method.OriginalDefinition;

        if (definition.TypeParameters.Length != 1)
            return null;

        string typeParameter = definition.TypeParameters[0].Name;
        var parameters = new List<string>();
        var arguments = new List<string>();

        // A C# 14 extension member reports no ReducedFrom and is not an extension method; its
        // receiver lives on the extension container instead.
        var container = QueryChainReader.Outermost(method.ContainingType);
        var receiver = method.ContainingType is { IsExtension: true } extension
            ? extension.ExtensionParameter
            : null;

        string receiverName = receiver?.Name ?? "";

        if (receiver is not null)
            parameters.Add("this " + Display(receiver.Type) + " " + receiverName);

        foreach (var parameter in definition.Parameters)
        {
            if (parameter.RefKind != RefKind.None || parameter.IsParams)
                return null;

            string rendered = Display(parameter.Type) + " " + parameter.Name;

            // Only a null default is reproduced; anything else would have to be re-rendered as a
            // constant expression, and neither entry point has one.
            if (parameter.HasExplicitDefaultValue)
            {
                if (parameter.ExplicitDefaultValue is not null)
                    return null;

                rendered += " = null";
            }

            parameters.Add(rendered);
            arguments.Add(parameter.Name);
        }

        // An extension member is called through its receiver and a plain static through its
        // type. The static form of an extension member is not equivalent — the compiler lowers
        // it to a signature with its own constraints — so this calls each the way it was
        // written. Neither call site is itself intercepted, since interception is by location.
        string forward = receiver is not null
            ? receiverName + "." + method.Name + "<" + typeParameter + ">("
                + string.Join(", ", arguments) + ")"
            : Display(container!) + "." + method.Name + "<" + typeParameter + ">("
                + string.Join(", ", arguments) + ")";

        // Instance form needs the extension's namespace in scope; a qualified static call does not.
        string @using = receiver is not null
            ? container!.ContainingNamespace?.ToDisplayString() ?? ""
            : "";

        return new Interception(attribute, typeParameter, Constraints(definition.TypeParameters[0]),
            string.Join(", ", parameters), @using, forward, document, plan);
    }

    /// <summary>
    /// Reproduces a type parameter's constraints. An interceptor has to be substitutable for what
    /// it replaces, so a missing <c>notnull</c> is a compile error rather than a subtle one.
    /// </summary>
    private static string Constraints(ITypeParameterSymbol parameter)
    {
        var constraints = new List<string>();

        if (parameter.HasReferenceTypeConstraint)
            constraints.Add("class");

        if (parameter.HasUnmanagedTypeConstraint)
            constraints.Add("unmanaged");
        else if (parameter.HasValueTypeConstraint)
            constraints.Add("struct");

        if (parameter.HasNotNullConstraint)
            constraints.Add("notnull");

        foreach (var type in parameter.ConstraintTypes)
            constraints.Add(Display(type));

        // Implied by struct, and saying both is an error.
        if (parameter.HasConstructorConstraint && !parameter.HasValueTypeConstraint)
            constraints.Add("new()");

        return constraints.Count == 0
            ? ""
            : " where " + parameter.Name + " : " + string.Join(", ", constraints);
    }

    private static readonly SymbolDisplayFormat _fullyQualified = SymbolDisplayFormat.FullyQualifiedFormat
        .WithMiscellaneousOptions(
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
            | SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    private static string Display(ISymbol symbol) => symbol.ToDisplayString(_fullyQualified);

    private static void Emit(SourceProductionContext context, ImmutableArray<Interception> found)
    {
        if (found.IsDefaultOrEmpty)
            return;

        var builder = new StringBuilder("""
            // <auto-generated/>
            #nullable enable

            namespace System.Runtime.CompilerServices
            {
                /// <summary>Declared here because the framework does not ship it.</summary>
                [global::System.AttributeUsage(global::System.AttributeTargets.Method, AllowMultiple = true)]
                file sealed class InterceptsLocationAttribute : global::System.Attribute
                {
                    public InterceptsLocationAttribute(int version, string data)
                    {
                        _ = version;
                        _ = data;
                    }
                }
            }

            namespace 
            """);

        builder.Append(Namespace).Append("\n{\n");

        foreach (string @using in found.Select(x => x.Using).Where(x => x.Length > 0).Distinct().OrderBy(x => x))
            builder.Append("    using global::").Append(@using).Append(";\n");

        builder.Append("""
                /// <summary>
                /// One method per query whose document the compiler could print. Each replaces the
                /// call that starts its chain, and does nothing but hand the chain that document.
                /// </summary>
                file static class PrecompiledQueries
                {

            """);

        // A file-local type cannot be nested, so the filters are collected here and written as
        // siblings of the class whose queries use them.
        var filters = new StringBuilder();

        int index = 0;
        foreach (var interception in found.Distinct())
        {
            builder.Append("        ").Append(interception.Attribute).Append('\n')
                .Append("        public static global::System.Linq.IQueryable<")
                .Append(interception.TypeParameter).Append("> Query").Append(index)
                .Append('<').Append(interception.TypeParameter).Append(">(")
                .Append(interception.Parameters).Append(')').Append(interception.Constraints).Append('\n')
                .Append("            => global::Feather.GraphQL.Linq.Query.GraphQLPrecompiled.")
                .Append(interception.Plan is null ? "Attach(\n" : "AttachPlan(\n")
                .Append("                ").Append(interception.Forward).Append(",\n")
                .Append("                ").Append(Literal(interception.Document));

            if (interception.Plan is { } plan)
            {
                builder.Append(",\n                ").Append(Literal(plan.RootField))
                    .Append(", ").Append(plan.Paging)
                    .Append(", ").Append(plan.ResultOperator)
                    .Append(", ").Append(plan.Page is { } page ? page.ToString() : "null")
                    .Append(", ").Append(plan.ProjectionKey is { } key ? Literal(key) : "null")
                    .Append(", ").Append(plan.Filter?.Holes.Count ?? 0)
                    .Append(",\n                ")
                    .Append(plan.Filter is null ? "null" : "Filter" + index + ".Build");
            }

            builder.Append(");\n\n");

            if (interception.Plan?.Filter is { } skeleton)
                Filter(filters, index, skeleton);

            index++;
        }

        builder.Append("    }\n\n").Append(filters).Append("}\n");

        context.AddSource("GraphQLPrecompiledQueries.g.cs", SourceText.From(builder.ToString(), Encoding.UTF8));
    }

    /// <summary>
    /// Emits the filter one chain binds, as a payload that writes itself.
    /// </summary>
    /// <remarks>
    /// The shape is fixed here, in the writer calls; the values arrive as constructor arguments
    /// the runtime read out of the expression tree. Nothing about the filter is built at run time
    /// — no node tree, no dictionary, no intermediate of any kind between the predicate and the
    /// bytes on the wire.
    /// </remarks>
    private static void Filter(StringBuilder builder, int index, FilterSkeletonModel skeleton)
    {
        builder.Append("    /// <summary>The filter for Query").Append(index)
            .Append(", printed at build time with its values left open.</summary>\n")
            .Append("    file sealed class Filter").Append(index)
            .Append(" : global::Feather.GraphQL.IGraphQLVariables\n    {\n");

        for (int i = 0; i < skeleton.Holes.Count; i++)
        {
            builder.Append("        private readonly ").Append(skeleton.Holes[i].Type)
                .Append(" _").Append(i).Append(";\n");
        }

        builder.Append("\n        private Filter").Append(index).Append('(');

        for (int i = 0; i < skeleton.Holes.Count; i++)
        {
            if (i > 0)
                builder.Append(", ");

            builder.Append(skeleton.Holes[i].Type).Append(" v").Append(i);
        }

        builder.Append(")\n        {\n");

        for (int i = 0; i < skeleton.Holes.Count; i++)
            builder.Append("            _").Append(i).Append(" = v").Append(i).Append(";\n");

        builder.Append("        }\n\n")
            .Append("        public static global::Feather.GraphQL.IGraphQLVariables Build(")
            .Append("global::System.Collections.Generic.IReadOnlyList<object?> values)\n")
            .Append("            => new Filter").Append(index).Append('(');

        for (int i = 0; i < skeleton.Holes.Count; i++)
        {
            if (i > 0)
                builder.Append(", ");

            builder.Append('(').Append(skeleton.Holes[i].Type).Append(")values[").Append(i).Append("]!");
        }

        builder.Append(");\n\n")
            .Append("        public bool IsEmpty => false;\n\n")
            .Append("        public void WriteTo(global::System.Text.Json.Utf8JsonWriter writer)\n")
            .Append("        {\n")
            .Append("            writer.WriteStartObject();\n")
            .Append("            writer.WritePropertyName(\"v0\");\n")
            .Append(skeleton.Body)
            .Append("            writer.WriteEndObject();\n")
            .Append("        }\n    }\n\n");
    }

    private static string Literal(string value)
        => SyntaxFactory.Literal(value).ToFullString();
}
