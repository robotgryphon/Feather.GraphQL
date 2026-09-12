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
/// Writes the body of every <c>[GraphQLQuery]</c> method.
/// </summary>
/// <remarks>
/// <para>
/// The shape <c>[GeneratedRegex]</c> and <c>[LoggerMessage]</c> use, and for the same reason: a
/// pattern that is constant at build time should be compiled at build time, not interpreted on
/// every call. Here the pattern is the document, and what it compiles to is the code that writes
/// the request.
/// </para>
/// <para>
/// The values are the method's parameters, which is what makes this the fastest surface the
/// library has. Composing the same query with LINQ allocates an expression tree at the call site
/// before any of this library runs — about a microsecond and three kilobytes — and then walks it
/// to recover the values the caller already had. A declared query hands them straight over.
/// </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class GraphQLQueryMethodGenerator : IIncrementalGenerator
{
    private const string Attribute = "Feather.GraphQL.GraphQLQueryAttribute";

    /// <summary>One method, reduced to what it takes to write its body.</summary>
    private sealed record Query(
        string Namespace,
        ImmutableArray<string> Containers,
        string Method,
        string Modifiers,
        string ReturnType,
        string ResultType,
        string Parameters,
        string Client,
        string? CancellationToken,
        string Document,
        ImmutableArray<(string Name, string Type, bool Boxed)> Variables,
        ResponseStructWriter.Model? Reply,
        bool Single,
        Location? Where,
        string? Declined);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var queries = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                Attribute,
                static (node, _) => node is MethodDeclarationSyntax,
                static (context, token) => Describe(context, token))
            .Where(static query => query is not null)
            .Collect();

        context.RegisterSourceOutput(queries, static (context, found) => Emit(context, found!));
    }

    private static Query? Describe(GeneratorAttributeSyntaxContext context, CancellationToken token)
    {
        if (context.TargetSymbol is not IMethodSymbol method
            || context.TargetNode is not MethodDeclarationSyntax declaration)
            return null;

        var arguments = context.Attributes[0].ConstructorArguments;

        // No document means the method's body is the query, which the other generator compiles.
        if (arguments.Length != 1 || arguments[0].Value is not string document || document.Length == 0)
            return null;

        // A body would collide with the one being written here, and a non-partial method has
        // nothing to implement.
        if (!method.IsPartialDefinition || declaration.Body is not null)
            return null;

        if (Result(method.ReturnType) is not { } result)
            return null;

        // The document says which fields the reply carries and the return type says what they
        // are, which between them is everything a reader needs. A document outside what can be
        // read with certainty — an alias, a fragment, more than one root field — leaves the
        // method reading through a contract, as it did before.
        var shape = Rows(method.ReturnType);
        ResponseStructWriter.Model? reply = null;
        string? declined = null;

        if (shape.Element is null)
        {
            declined = "its return type is not one row or an array of them";
        }
        else if (DocumentSelectionReader.Read(document) is not { } selection)
        {
            declined = "its document is outside what can be read with certainty — an alias, a "
                + "fragment, a directive, or more than one root field";
        }
        else if (ResponseStructWriter.Describe(method.Name, shape.Element, selection, direct: true)
            is not { } described)
        {
            declined = "its reply holds something with no certain read — a field the returned type "
                + "does not have, a type with no converter of its own, or a collection that is not "
                + "an array";
        }
        else
        {
            reply = described;
        }

        string? client = null;
        string? cancellation = null;
        var variables = ImmutableArray.CreateBuilder<(string, string, bool)>();
        var declared = Declared(document);

        foreach (var parameter in method.Parameters)
        {
            string type = parameter.Type.ToDisplayString(_qualified);

            if (client is null && type == "global::System.Net.Http.HttpClient")
            {
                client = parameter.Name;
                continue;
            }

            if (cancellation is null && type == "global::System.Threading.CancellationToken")
            {
                cancellation = parameter.Name;
                continue;
            }

            // Anything else is a variable, and the document has to have asked for it.
            if (!declared.Contains(parameter.Name))
                return null;

            variables.Add((parameter.Name, parameter.Type.ToDisplayString(_signature), Boxed(parameter.Type)));
        }

        // Every variable the document declares needs a value, or the server would reject it.
        // A client the method was handed, or the one its type holds — which is where a service
        // with an injected client keeps it, and asking for it again on every method would be
        // asking the caller to repeat what the type already knows.
        client ??= Injected(method);

        if (client is null)
        {
            declined = "it has no HttpClient to post through — take one as a parameter, or hold "
                + "one on the declaring type";
        }
        else if (declared.Count != variables.Count)
        {
            return null;
        }

        var containers = ImmutableArray.CreateBuilder<string>();
        for (var container = method.ContainingType; container is not null; container = container.ContainingType)
            containers.Insert(0, Container(container));

        return new Query(
            method.ContainingNamespace.IsGlobalNamespace
                ? ""
                : method.ContainingNamespace.ToDisplayString(),
            containers.ToImmutable(),
            method.Name,
            Modifiers(method) + (method.IsStatic ? " static" : ""),
            method.ReturnType.ToDisplayString(_signature),
            result,
            string.Join(", ", method.Parameters.Select(Parameter)),
            client ?? "",
            cancellation,
            document,
            variables.ToImmutable(),
            reply,
            shape.Single,
            declaration.Identifier.GetLocation(),
            declined);
    }

    /// <summary>The type a <c>Task&lt;T&gt;</c> or <c>ValueTask&lt;T&gt;</c> carries.</summary>
    private static string? Result(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } task)
            return null;

        // Named without its type parameter, whose name belongs to the framework rather than to
        // this comparison — `Task<TResult>` is not `Task<T>`.
        string definition = task.ConstructedFrom.ToDisplayString(_unqualifiedGenerics);

        return definition is "global::System.Threading.Tasks.Task"
            or "global::System.Threading.Tasks.ValueTask"
            ? task.TypeArguments[0].ToDisplayString(_qualified)
            : null;
    }

    /// <summary>
    /// What a declared query answers with: an array of rows, or one row on its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both are ordinary GraphQL — <c>{ people { … } }</c> answers with a list and
    /// <c>{ person { … } }</c> with an object — and the two are told apart by what the method
    /// returns rather than by what arrives, because a connection is an object too and guessing
    /// from the reply would sometimes guess wrong.
    /// </para>
    /// <para>
    /// A collection that is not an array is declined: a reader builds the rows into one and
    /// handing back anything else would need a conversion this does not write.
    /// </para>
    /// </remarks>
    private static (ITypeSymbol? Element, bool Single) Rows(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } task)
            return (null, false);

        var returned = task.TypeArguments[0];

        if (returned is IArrayTypeSymbol { Rank: 1 } array)
            return (array.ElementType, false);

        // One object of a type this can build, which is what a singular root field answers with.
        return returned is INamedTypeSymbol { TypeKind: TypeKind.Class or TypeKind.Struct } single
            && GraphQLTypeFacts.ElementType(returned) is null
                ? (single, true)
                : (null, false);
    }

    private static readonly SymbolDisplayFormat _unqualifiedGenerics =
        SymbolDisplayFormat.FullyQualifiedFormat.WithGenericsOptions(SymbolDisplayGenericsOptions.None);

    /// <summary>The variable names a document declares, in the order it declares them.</summary>
    /// <remarks>
    /// Read from the operation's variable list rather than by scanning for <c>$</c>, so a
    /// <c>$name</c> used inside the selection is not mistaken for one being declared.
    /// </remarks>
    private static HashSet<string> Declared(string document)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        int open = document.IndexOf('(');
        int body = document.IndexOf('{');

        if (open < 0 || (body >= 0 && body < open))
            return names;

        int close = document.IndexOf(')', open);
        if (close < 0)
            return names;

        string declarations = document.Substring(open + 1, close - open - 1);

        for (int i = 0; i < declarations.Length; i++)
        {
            if (declarations[i] != '$')
                continue;

            int start = ++i;
            while (i < declarations.Length && (char.IsLetterOrDigit(declarations[i]) || declarations[i] == '_'))
                i++;

            if (i > start)
                names.Add(declarations.Substring(start, i - start));
        }

        return names;
    }

    /// <summary>True when writing this type has to go through the boxing overload.</summary>
    private static bool Boxed(ITypeSymbol type)
        => GraphQLTypeFacts.UnwrapNullable(type).ToDisplayString(_qualified) is not
            ("string" or "int" or "long" or "bool" or "double" or "decimal");

    /// <summary>
    /// The client the declaring type holds, when the method was not handed one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A field or a property, its own or one it inherits — or a parameter of its primary
    /// constructor, which is what a service class most often looks like. Accessibility is not
    /// consulted because it does not have to be: the implementation is written into the same type,
    /// as another part of it, so a private field is as reachable from there as it is from anything
    /// else the type declares.
    /// </para>
    /// <para>
    /// A static method can only reach a static one. The first match wins, and a type holding two
    /// clients is a type this cannot choose between — so it takes the first and the author can
    /// name the one they meant with a parameter.
    /// </para>
    /// </remarks>
    private static string? Injected(IMethodSymbol method)
    {
        for (var type = method.ContainingType; type is not null; type = type.BaseType)
        {
            foreach (var member in type.GetMembers())
            {
                // A captured primary constructor parameter has a field behind it whose name is not
                // a name at all — <client>P — and writing that out is writing something that will
                // not compile. The parameter it stands for is named below, which is what a method
                // of this type would have written.
                if (!member.CanBeReferencedByName || (method.IsStatic && !member.IsStatic))
                    continue;

                var held = member switch
                {
                    IFieldSymbol field => field.Type,
                    IPropertySymbol { GetMethod: not null } property => property.Type,
                    _ => null
                };

                if (held?.ToDisplayString(_qualified) == "global::System.Net.Http.HttpClient")
                    return member.Name;
            }
        }

        // A primary constructor's parameter is in scope throughout the type, and naming it from a
        // method captures it — which is what any other method of the type does with it, and what
        // the generated part is doing too.
        if (method.IsStatic || Primary(method.ContainingType) is not { } primary)
            return null;

        foreach (var parameter in primary.Parameters)
        {
            if (parameter.Type.ToDisplayString(_qualified) == "global::System.Net.Http.HttpClient")
                return parameter.Name;
        }

        return null;
    }

    /// <summary>
    /// The type's primary constructor, when it has one.
    /// </summary>
    /// <remarks>
    /// Told by where it was written rather than by a flag: a primary constructor's declaration is
    /// the type's own, while every other constructor has a declaration of its own.
    /// </remarks>
    private static IMethodSymbol? Primary(INamedTypeSymbol type)
        => type.InstanceConstructors.FirstOrDefault(
            constructor => constructor.DeclaringSyntaxReferences.Any(
                reference => reference.GetSyntax() is TypeDeclarationSyntax));

    private static string Modifiers(IMethodSymbol method)
        => method.DeclaredAccessibility switch
        {
            Accessibility.Public => "public",
            Accessibility.Internal => "internal",
            Accessibility.Protected => "protected",
            Accessibility.ProtectedOrInternal => "protected internal",
            Accessibility.ProtectedAndInternal => "private protected",
            _ => "private"
        };

    private static string Parameter(IParameterSymbol parameter)
        => parameter.Type.ToDisplayString(_signature) + " " + parameter.Name;

    /// <summary>A containing type's declaration, so the generated part reopens the same type.</summary>
    private static string Container(INamedTypeSymbol type)
    {
        string keyword = type.TypeKind switch
        {
            TypeKind.Struct => type.IsRecord ? "record struct" : "struct",
            TypeKind.Interface => "interface",
            _ => type.IsRecord ? "record" : "class"
        };

        string name = type.Name;

        if (type.TypeParameters.Length > 0)
            name += "<" + string.Join(", ", type.TypeParameters.Select(p => p.Name)) + ">";

        // Declared exactly as the author declared it, accessibility included. Leaving it off is
        // legal — a part without one takes the accessibility of the parts that have one — but it
        // makes the generated part read as a different declaration than the one it completes, and
        // anything comparing the two has to know the rule to agree they match.
        string accessibility = type.DeclaredAccessibility switch
        {
            Accessibility.Public => "public ",
            Accessibility.Internal => "internal ",
            Accessibility.Protected => "protected ",
            Accessibility.ProtectedOrInternal => "protected internal ",
            Accessibility.ProtectedAndInternal => "private protected ",
            Accessibility.Private => "private ",
            _ => ""
        };

        return accessibility + (type.IsStatic ? "static partial " : "partial ") + keyword + " " + name;
    }

    /// <summary>
    /// How a type is written into the generated signature.
    /// </summary>
    /// <remarks>
    /// Nullable annotations included, and that is the point: an implementing part whose parameter
    /// says <c>string</c> where the declaration said <c>string?</c> does not match it, and the
    /// consumer's own build is where that fails.
    /// </remarks>
    private static readonly SymbolDisplayFormat _signature =
        SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions
            | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
            | SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    /// <summary>
    /// How a type is named when this only needs to know which type it is.
    /// </summary>
    /// <remarks>
    /// Annotations stripped, so that <c>string?</c> and <c>string</c> answer the same question
    /// about which overload writes them.
    /// </remarks>
    private static readonly SymbolDisplayFormat _qualified =
        SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions
            & ~SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
            | SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    private static void Emit(SourceProductionContext context, ImmutableArray<Query> found)
    {
        if (found.IsDefaultOrEmpty)
            return;

        foreach (var query in found.Distinct().Where(x => x.Declined is not null))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                GraphQLDiagnostics.NotModelled, query.Where, query.Method, query.Declined));
        }

        // Numbered once, across every query in the compilation. The methods are written grouped by
        // the type that declared them and the structs they name are numbered from the same walk,
        // so a method naming the wrong struct cannot happen by the two disagreeing.
        //
        // A method whose reply could not be modelled has no implementation to write, and the
        // diagnostic above says why rather than leaving the compiler to report the absence alone.
        var numbered = found.Distinct().Where(x => x.Declined is null)
            .Select((query, index) => (Query: query, Index: index)).ToList();

        var prefixes = Prefixes(numbered);

        // A file per declaring type, named for it. Generated code then sits beside the code that
        // caused it — and the readers, which are file-local, land in the same file as the methods
        // that call them rather than relying on everything sharing one.
        foreach (var group in numbered.GroupBy(
            x => (x.Query.Namespace, Containers: string.Join("+", x.Query.Containers))))
        {
            var first = group.First().Query;
            var builder = new StringBuilder("// <auto-generated/>\n#nullable enable\n\n");
            string indent = "";

            if (first.Namespace.Length > 0)
            {
                builder.Append("namespace ").Append(first.Namespace).Append("\n{\n");
                indent = "    ";
            }

            foreach (string container in first.Containers)
            {
                builder.Append(indent).Append(container).Append("\n").Append(indent).Append("{\n");
                indent += "    ";
            }

            foreach (var (query, index) in group)
                Method(builder, indent, query, index, prefixes[index]);

            foreach (var _ in first.Containers)
            {
                indent = indent.Substring(4);
                builder.Append(indent).Append("}\n");
            }

            if (first.Namespace.Length > 0)
                builder.Append("}\n");

            builder.Append('\n');

            foreach (var (query, index) in group)
                Variables(builder, query, index);

            foreach (var (query, index) in group)
            {
                string prefix = prefixes[index];

                // Every query left here has one: the ones that did not were reported and dropped.
                var described = query.Reply!;

                // Described under the method's own name, which another method elsewhere may share.
                var reply = prefix == query.Method
                    ? described
                    : ResponseStructWriter.Rename(described, query.Method, prefix);

                builder.Append("namespace Feather.GraphQL.Generated\n{\n");
                ResponseStructWriter.Write(builder, reply, prefix, query.Single);
                Parser(builder, query, prefix, reply);
                builder.Append("}\n\n");
            }

            context.AddSource(HintName(first), SourceText.From(builder.ToString(), Encoding.UTF8));
        }
    }

    /// <summary>
    /// What one type's generated file is called: its namespace and its own name.
    /// </summary>
    /// <remarks>
    /// The name a person would look for. Punctuation a file name cannot carry — the angle brackets
    /// of a generic type, the space in its argument list — is folded away, since the name only has
    /// to be unique and recognisable rather than round-trippable.
    /// </remarks>
    private static string HintName(Query query)
    {
        var builder = new StringBuilder();

        if (query.Namespace.Length > 0)
            builder.Append(query.Namespace).Append('.');

        foreach (string container in query.Containers)
        {
            // The container is a declaration — "public partial class Service" — and the name is
            // its last word, minus whatever a type argument list added.
            string name = container.Split(' ').Last();
            int generic = name.IndexOf('<');

            builder.Append(generic < 0 ? name : name.Substring(0, generic)).Append('.');
        }

        return builder.Append("g.cs").ToString();
    }

    /// <summary>
    /// What each query's generated types are called: its method's name, and its number when two
    /// methods share one.
    /// </summary>
    /// <remarks>
    /// Two types in an assembly may each declare a <c>ByCodeAsync</c>, and their readers land in
    /// the same file. The name is the method's because that is what makes generated code findable
    /// from the code that caused it; the number is there only when it has to be.
    /// </remarks>
    private static Dictionary<int, string> Prefixes(List<(Query Query, int Index)> numbered)
    {
        var taken = new HashSet<string>(StringComparer.Ordinal);
        var prefixes = new Dictionary<int, string>();

        foreach (var (query, index) in numbered)
        {
            prefixes[index] = taken.Add(query.Method) ? query.Method : query.Method + index;
        }

        return prefixes;
    }

    private static void Method(StringBuilder builder, string indent, Query query, int index, string prefix)
    {
        string token = query.CancellationToken ?? "default";

        builder.Append(indent).Append("/// <summary>Written by the compiler from this method's document.</summary>\n")
            .Append(indent).Append(query.Modifiers).Append(" async partial ")
            .Append(query.ReturnType).Append(' ')
            .Append(query.Method).Append('(').Append(query.Parameters).Append(")\n")
            .Append(indent).Append("{\n")
            .Append(indent).Append("    using var response = await global::Feather.GraphQL.Http.HttpExtensions")
            .Append(".SendGraphQLQueryAsync(").Append(query.Client).Append(",\n")
            .Append(indent).Append("        ").Append(Literal(query.Document)).Append(",\n")
            .Append(indent).Append("        ");

        if (query.Variables.Length == 0)
            builder.Append("global::Feather.GraphQL.GraphQLNoVariables.Instance");
        else
        {
            builder.Append("new global::Feather.GraphQL.Generated.Variables").Append(index).Append('(');

            for (int i = 0; i < query.Variables.Length; i++)
            {
                if (i > 0)
                    builder.Append(", ");

                builder.Append(query.Variables[i].Name);
            }

            builder.Append(')');
        }

        builder.Append(",\n")
            .Append(indent).Append("        ").Append(token).Append(").ConfigureAwait(false);\n\n")
            .Append(indent).Append("    return ");

        // This document's own reader: the fields it names, read into the type it returns, with
        // no contract resolved on the way. There is no other way to read one — a document this
        // cannot model is reported rather than read more slowly.
        builder.Append("await global::Feather.GraphQL.Http.HttpExtensions")
            .Append(".ReadGraphQLReplyAsync(response,\n")
            .Append(indent).Append("        global::Feather.GraphQL.Generated.").Append(prefix)
            .Append("_Parser.Parse,\n")
            .Append(indent).Append("        ").Append(token).Append(").ConfigureAwait(false);\n")
            .Append(indent).Append("}\n\n");
    }

    /// <summary>Reads one reply to a declared query.</summary>
    private static void Parser(StringBuilder builder, Query query, string prefix, ResponseStructWriter.Model reply)
    {
        builder.Append("    /// <summary>Reads one reply to <c>").Append(query.Method)
            .Append("</c>.</summary>\n")
            .Append("    file static class ").Append(prefix).Append("_Parser\n")
            .Append("    {\n")
            .Append("        public static ").Append(reply.Name).Append(query.Single ? " Parse(\n" : "[] Parse(\n")
            .Append("            global::System.ReadOnlySpan<byte> json,\n")
            .Append("            out bool hasData,\n")
            .Append("            out bool hasErrors)\n")
            .Append("        {\n")
            .Append("            var reply = ").Append(prefix).Append("_Reply.Read(json);\n\n")
            .Append("            hasData = reply.HasData;\n")
            .Append("            hasErrors = reply.HasErrors;\n\n")
            .Append("            return reply.").Append(query.Single ? "Row" : "Rows").Append(";\n")
            .Append("        }\n")
            .Append("    }\n\n");
    }

    /// <summary>
    /// The payload one declared query binds, written from the method's own parameters.
    /// </summary>
    /// <remarks>
    /// A readonly struct, and the writes are typed: there is no dictionary, no node, and nothing
    /// boxed between the caller's arguments and the bytes on the wire.
    /// </remarks>
    private static void Variables(StringBuilder builder, Query query, int index)
    {
        if (query.Variables.Length == 0)
            return;

        builder.Append("namespace Feather.GraphQL.Generated\n{\n")
            .Append("    /// <summary>The variables of one <c>[GraphQLQuery]</c> method.</summary>\n")
            .Append("    internal readonly struct Variables").Append(index)
            .Append(" : global::Feather.GraphQL.IGraphQLVariables\n    {\n");

        foreach (var (name, type, _) in query.Variables)
            builder.Append("        private readonly ").Append(type).Append(" _").Append(name).Append(";\n");

        builder.Append("\n        public Variables").Append(index).Append('(');

        for (int i = 0; i < query.Variables.Length; i++)
        {
            if (i > 0)
                builder.Append(", ");

            builder.Append(query.Variables[i].Type).Append(' ').Append(query.Variables[i].Name);
        }

        builder.Append(")\n        {\n");

        foreach (var (name, _, _) in query.Variables)
            builder.Append("            _").Append(name).Append(" = ").Append(name).Append(";\n");

        builder.Append("        }\n\n")
            .Append("        public bool IsEmpty => false;\n\n")
            .Append("        public void WriteTo(global::System.Text.Json.Utf8JsonWriter writer)\n        {\n")
            .Append("            writer.WriteStartObject();\n");

        foreach (var (name, _, boxed) in query.Variables)
        {
            builder.Append("            writer.WritePropertyName(\"").Append(name).Append("\");\n")
                .Append("            global::Feather.GraphQL.GraphQLVariableWriter.Write(writer, ");

            if (boxed)
                builder.Append("(object?)");

            builder.Append('_').Append(name).Append(");\n");
        }

        builder.Append("            writer.WriteEndObject();\n        }\n    }\n}\n\n");
    }

    private static string Literal(string value)
        => SyntaxFactory.Literal(value).ToFullString();
}
