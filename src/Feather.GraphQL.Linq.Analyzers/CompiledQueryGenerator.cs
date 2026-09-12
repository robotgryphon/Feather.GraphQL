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
/// Compiles a <c>[GraphQLQuery]</c> method's chain into the request it stands for, and
/// replaces every call to the method with it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="QueryInterceptorGenerator"/> precompiles what a chain <em>says</em> — its document,
/// and as much of its plan as holds without values — and leaves the chain itself to be composed
/// and walked, because the values it binds are only in the expression tree. This removes that
/// last step, and the attribute is what makes it possible: a chain isolated to a method binds
/// that method's parameters and nothing else, so the values are known at the call, one level up
/// from where the tree would have been built.
/// </para>
/// <para>
/// So the interception is of the method rather than of anything inside it. Intercepting the
/// terminal would not do: its receiver is the rest of the chain, and a receiver is evaluated
/// before the call that would replace it — the trees would already exist. Replacing the call to
/// the method skips the body whole.
/// </para>
/// <para>
/// The body is still the specification, and still correct: a chain this declines to compile keeps
/// the runtime translation it has always had, and <c>FGQL015</c> says so rather than letting the
/// attribute quietly do nothing.
/// </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class CompiledQueryGenerator : IIncrementalGenerator
{
    private const string Attribute = "Feather.GraphQL.GraphQLQueryAttribute";
    private const string HttpClient = "global::System.Net.Http.HttpClient";
    private const string Token = "global::System.Threading.CancellationToken";

    /// <summary>One attributed method: either the request it compiles to, or why it does not.</summary>
    /// <remarks>
    /// <c>Read</c> is the reader the reply's shape calls for and <c>ResultType</c> what the rows
    /// are read as; <c>Result</c> turns what was read into what the method promised.
    /// <c>Payload</c> holds one part per variable the document declared, in its numbering, and
    /// <c>Holes</c> the values those parts wait for, in the order they are written.
    /// </remarks>
    private sealed record Compiled(
        string Key,
        string Name,
        Location? Where,
        string? Declined,
        string ReturnType,
        string ResultType,
        string Result,
        Reader Read,
        string Parameters,
        string Client,
        string? CancellationToken,
        string Document,
        ImmutableArray<PayloadPart> Payload,
        ImmutableArray<(string Type, string Binding)> Holes,
        string? Shaper,
        string ShaperParameter,
        string ShapedType,
        string ElementType,
        string Usings,
        ResponseStructWriter.Model? Reply,
        bool Generated,
        string? Receiver,
        Accessor? Reach);

    /// <summary>
    /// A field the replacement cannot name, and the accessor that reaches it anyway.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A client held in a private field — or captured from a primary constructor, which is a
    /// private field with a name no C# can write — is out of reach of an interceptor, because the
    /// interceptor is somewhere else and private means private to it too.
    /// </para>
    /// <para>
    /// <c>UnsafeAccessor</c> is how the runtime says that is allowed: the accessor is bound by the
    /// runtime against the field's metadata, with no reflection, nothing to trim away and nothing
    /// resolved at run time. The name comes from the symbol rather than from a guess at how the
    /// compiler spells a capture, so a compiler that spells it differently is still read right.
    /// </para>
    /// </remarks>
    /// <param name="Method">What the accessor is called.</param>
    /// <param name="Field">The field's name in metadata, which may not be a name at all.</param>
    /// <param name="Owner">The type that declares it.</param>
    /// <param name="Type">What the field holds.</param>
    private sealed record Accessor(string Method, string Field, string Owner, string Type);

    /// <summary>How a reply is read, which its root field's shape decides.</summary>
    /// <remarks>
    /// A plain field answers with the rows; a paged one wraps them in <c>items</c> or
    /// <c>nodes</c>; a count asks the wrapper for <c>totalCount</c> and gets no rows at all.
    /// </remarks>
    private enum Reader { Root, Page, Count }

    /// <summary>The code that writes one variable's value into the payload.</summary>
    private readonly record struct PayloadPart(string Name, string Body);

    /// <summary>One call to an attributed method, and the attribute that replaces it.</summary>
    private sealed record CallSite(string Key, string Attribute);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var compiled = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                Attribute,
                static (node, _) => node is MethodDeclarationSyntax,
                static (context, token) => Describe(context, token))
            .Where(static x => x is not null)
            .Collect();

        // Every call in the compilation has to be looked at, since which method is attributed is
        // not visible in syntax. What is visible is the shape a call to one can have: a compiled
        // method is not generic, and it takes at least the client to post through.
        var calls = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is InvocationExpressionSyntax invocation
                    && invocation.ArgumentList.Arguments.Count > 0
                    && invocation.Expression is IdentifierNameSyntax
                        or MemberAccessExpressionSyntax { Name: IdentifierNameSyntax },
                static (context, token) => Call(context, token))
            .Where(static x => x is not null)
            .Collect();

        context.RegisterSourceOutput(
            compiled.Combine(calls),
            static (context, found) => Emit(context, found.Left!, found.Right!));
    }

    /// <summary>Reads one attributed method, down to the strings its replacement is written from.</summary>
    private static Compiled? Describe(GeneratorAttributeSyntaxContext context, CancellationToken token)
    {
        if (context.TargetSymbol is not IMethodSymbol method
            || context.TargetNode is not MethodDeclarationSyntax declaration)
            return null;

        // A document was given, so the query is written as one and the other generator writes it.
        if (Document(context.Attributes[0]) is not null)
            return null;

        string key = method.OriginalDefinition.ToDisplayString(_key);
        var where = declaration.Identifier.GetLocation();

        Compiled Declined(string reason)
            => new(key, method.Name, where, reason, "", "", "", Reader.Root, "", "", null, "", [], [],
                null, "", "", "", "", null, false, null, null);

        if (method.IsGenericMethod || method.PartialImplementationPart is not null)
            return Declined("a compiled query is a plain method, with the chain as its body");

        if (Awaited(method.ReturnType) is not { } awaited)
            return Declined("its return type is not a Task or a ValueTask of something");

        var (returnType, returned) = awaited;

        // The body has to be the chain and nothing else. A method that also did something —
        // logged, counted, cached — would have that something silently skipped, since the call
        // this replaces is the call that would have run it.
        if (Chain(declaration) is null)
            return Declined("its body is more than the chain it stands for");

        // One entry point, so there is one chain and no question which one the method is about.
        var entries = declaration.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(call => context.SemanticModel.GetSymbolInfo(call, token).Symbol is IMethodSymbol origin
                && EntryPoints.IsEntryPoint(origin))
            .ToList();

        if (entries.Count != 1)
            return Declined("its body does not hold exactly one query");

        var entry = entries[0];

        if (context.SemanticModel.GetSymbolInfo(entry, token).Symbol is not IMethodSymbol entryPoint)
            return Declined("its body does not hold exactly one query");

        // The client the chain posts through has to be one the replacement can reach, and the
        // replacement runs at the call site rather than inside the method.
        if (entry.Expression is not MemberAccessExpressionSyntax { Expression: { } receiver }
            || !Is(context.SemanticModel.GetTypeInfo(receiver, token).Type, HttpClient))
            return Declined("its chain does not start from an HttpClient");

        var (client, reach) = Client(receiver, method, context.SemanticModel, token);

        if (client is null)
        {
            return Declined(
                "the HttpClient its chain posts through cannot be reached from the call — take it "
                + "as a parameter, or hold it on the declaring type");
        }

        var completions = QueryChainReader.Read(entry, entryPoint, context.SemanticModel, token);

        if (completions is not { Count: 1 })
            return Declined("its chain could not be read as one query ending in this method");

        var facts = completions[0];

        // This writes the post itself, so an endpoint it does not know about would send the
        // request somewhere other than where the chain meant it to go.
        if (facts.SetsEndpoint)
            return Declined("its options name an endpoint, and the compiled call posts to the client's own");

        // The operation names inside a filter are the dialect's, and the ones written here are
        // the default provider's.
        if (facts.SetsFilterProvider && facts.HasFilter)
            return Declined("its options name a filter dialect other than the one this writes");

        // Rows are always read as the queried type. A projection runs over them afterwards, as
        // the runtime runs it — never by reading the reply into the projected type, which would
        // make a renamed or nested member read as null instead of failing.
        var element = facts.ElementType;
        var shaped = facts.ElementType;
        Shaping? shaping = null;

        // A count answers from the connection and an existence check from the row count, so
        // neither has rows to shape.
        bool shapes = facts.Projection is not null && !facts.IsCount && facts.Result != ResultKind.Any;

        if (shapes)
        {
            if (Shaper(facts.Projection!, method, context.SemanticModel, token) is not { } written)
                return Declined(
                    "its Select reads something the replacement cannot — a local it captured, or a "
                    + "member private to the declaring type");

            shaping = written;
            shaped = written.Type;
        }

        if (facts.HasOpaqueOrdering)
            return Declined("its ordering is not a key selector written at the call");

        // `Take(n).First()` asks for a page twice, and which size wins is the runtime's rule to
        // make rather than this one's to guess.
        if (facts.ExplicitTake && facts.Result != ResultKind.Sequence)
            return Declined("a Take and a result operator both ask for a page");

        if (Reduction(facts, element, shaped, returned, out var resultType, out var reader) is not { } result)
            return Declined(
                "its terminal and its return type disagree — " + Expected(facts, shaped));

        string? filter = null;
        var holes = ImmutableArray<(string Type, string Binding)>.Empty;

        if (facts.HasFilter)
        {
            if (facts.HasOpaquePredicate)
                return Declined("its filter is not a predicate written at the call");

            var skeleton = FilterSkeleton.From(facts.Predicates, context.SemanticModel, token,
                value => Value(value, method, context.SemanticModel, token));

            if (skeleton is null)
                return Declined("its predicate is outside the shape the compiler prints");

            filter = skeleton.Body;
            holes = [.. skeleton.Holes.Select(hole => (hole.Type, hole.Binding!))];
        }

        // The reply's own shape, which is the selection set seen from the other side. A chain
        // that projects needs the payload mirrored, because the projection is written against
        // those fields; one that does not is asking for the queried type, so the rows are built
        // as they are read and there is no mirror to copy out of.
        ResponseStructWriter.Model? reply = null;
        bool generated = false;

        if (facts.IsCount)
        {
            // Nothing to read but the connection's own field, and the generated envelope already
            // reads it — so a count needs no row model and never falls back.
            generated = true;
        }
        else if (SelectionSetWriter.Build(facts.ElementType, facts.Projection, context.SemanticModel, token)
            is { } selection)
        {
            reply = ResponseStructWriter.Describe(
                method.Name, facts.ElementType, selection, direct: shaping is null);

            generated = reply is not null;
        }

        if (!generated)
        {
            return Declined(
                "its reply could not be modelled — a field the element does not have, a type with "
                + "no certain read, or a collection that is not an array");
        }

        var bindings = new List<DocumentBinding>();

        if (QueryDocumentWriter.TryWrite(facts, context.SemanticModel, token, bindings) is not { } document)
            return Declined("its document could not be printed at compile time");

        var payload = ImmutableArray.CreateBuilder<PayloadPart>();
        var bound = holes.ToBuilder();

        foreach (var binding in bindings)
        {
            switch (binding.Kind)
            {
                case BoundValue.Filter:
                    // Its holes were numbered first, which is also where the writer bound it.
                    payload.Add(new PayloadPart(binding.Name, filter!));
                    continue;

                case BoundValue.Order:
                    if (Sort(facts, context.SemanticModel, token) is not { } sort)
                        return Declined("its ordering keys are not members of the element");

                    payload.Add(new PayloadPart(binding.Name, sort));
                    continue;

                case BoundValue.Take when !facts.ExplicitTake:
                case BoundValue.Last:
                    // A page the terminal asked for: First means one, Single means two, and the
                    // compiler is the one that decided so.
                    payload.Add(new PayloadPart(binding.Name,
                        PageSize(facts.ResultPage?.ToString() ?? "1")));
                    continue;

                case BoundValue.Take:
                case BoundValue.Skip:
                {
                    var argument = binding.Kind == BoundValue.Take ? facts.TakeValue : facts.SkipValue;

                    if (argument is null
                        || Value(argument, method, context.SemanticModel, token) is not { } size)
                        return Declined("its page size is not a parameter or a constant");

                    payload.Add(new PayloadPart(binding.Name, PageSize("_" + bound.Count)));
                    bound.Add(("int", size));
                    continue;
                }

                default:
                    return Declined("its document binds something this cannot write");
            }
        }

        holes = bound.ToImmutable();

        var parameters = new List<string>();
        string? cancellation = null;

        foreach (var parameter in method.Parameters)
        {
            if (parameter.RefKind != RefKind.None || parameter.IsParams)
                return Declined("a ref, out or params parameter cannot be forwarded");

            if (Default(parameter) is not { } rendered)
                return Declined("a parameter's default could not be reproduced");

            parameters.Add(rendered);

            if (cancellation is null && Is(parameter.Type, Token))
                cancellation = parameter.Name;
        }

        return new Compiled(key, method.Name, where, null, returnType, resultType, result, reader,
            string.Join(", ", parameters), client, cancellation, document, payload.ToImmutable(), holes,
            shaping?.Body, shaping?.Parameter ?? "", shaped.ToDisplayString(_signature),
            element.ToDisplayString(_signature), Usings(declaration, method), reply, generated,
            method.IsStatic ? null : method.ContainingType.ToDisplayString(_signature), reach);
    }

    /// <summary>
    /// The single expression a method's body is, or null when the body is anything else.
    /// </summary>
    /// <remarks>
    /// Both spellings of the same method: an expression body, and a block whose one statement
    /// returns. Anything longer is a method that does something besides describe a query, and the
    /// something would be lost.
    /// </remarks>
    private static ExpressionSyntax? Chain(MethodDeclarationSyntax declaration)
    {
        if (declaration.ExpressionBody is { } arrow)
            return arrow.Expression;

        return declaration.Body?.Statements.Count == 1
            && declaration.Body.Statements[0] is ReturnStatementSyntax { Expression: { } returned }
            ? returned
            : null;
    }

    /// <summary>
    /// The document an attribute carried, when it carried one.
    /// </summary>
    /// <remarks>
    /// What tells the two kinds of query apart. A method with a document is implemented from it; a
    /// method without one is implemented from its own body, and it is that second kind this
    /// generator replaces the calls to.
    /// </remarks>
    private static string? Document(AttributeData attribute)
        => attribute.ConstructorArguments.Length == 1
            && attribute.ConstructorArguments[0].Value is string document
            && document.Length > 0
                ? document
                : null;

    /// <summary>What a call to an attributed method needs, or null when it is not one.</summary>
    private static CallSite? Call(GeneratorSyntaxContext context, CancellationToken token)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;

        if (context.SemanticModel.GetSymbolInfo(invocation, token).Symbol is not IMethodSymbol method)
            return null;

        // Only a query written as a chain is intercepted. One written as a document is a partial
        // method whose body the other generator writes, and replacing its calls would leave that
        // body unreachable rather than unnecessary.
        var attribute = method.GetAttributes().FirstOrDefault(
            x => x.AttributeClass?.ToDisplayString() == Attribute);

        if (attribute is null || Document(attribute) is not null)
            return null;

        var location = context.SemanticModel.GetInterceptableLocation(invocation, token);

        return location is null
            ? null
            : new CallSite(
                method.OriginalDefinition.ToDisplayString(_key),
                location.GetInterceptsLocationAttributeSyntax());
    }

    /// <summary>The type a <c>Task</c> or <c>ValueTask</c> carries, and how it is written.</summary>
    private static (string Return, ITypeSymbol Returned)? Awaited(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } task)
            return null;

        string definition = task.ConstructedFrom.ToDisplayString(
            SymbolDisplayFormat.FullyQualifiedFormat.WithGenericsOptions(SymbolDisplayGenericsOptions.None));

        return definition is "global::System.Threading.Tasks.Task"
            or "global::System.Threading.Tasks.ValueTask"
            ? (task.ToDisplayString(_signature), task.TypeArguments[0])
            : null;
    }

    /// <summary>
    /// Reconciles the chain's terminal with the method's return type, and says how to get from
    /// one to the other.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The terminal decides three things at once: what the server is asked for, what the reply
    /// looks like, and what the method is entitled to return. They have to agree, and this is
    /// where that is checked — a <c>FirstAsync</c> chain in a method returning <c>T[]</c> is a
    /// mistake to report rather than an invitation to be creative.
    /// </para>
    /// <para>
    /// The reductions mirror <c>ResultMaterializer.Reduce</c>, exception messages included, so a
    /// query means the same thing whether it was compiled or translated.
    /// </para>
    /// </remarks>
    private static string? Reduction(
        ChainFacts facts,
        ITypeSymbol element,
        ITypeSymbol shaped,
        ITypeSymbol returned,
        out string resultType,
        out Reader reader)
    {
        // A count asks the connection rather than the rows, so none are read.
        reader = facts.IsCount ? Reader.Count : facts.Paging == Paging.None ? Reader.Root : Reader.Page;

        // What the reply is read as is the queried type; what the method returns is what the
        // projection made of it, and the two are only the same when there was no projection.
        resultType = element.ToDisplayString(_signature) + "[]";

        string returnedType = returned.ToDisplayString(_qualified);

        if (facts.IsCount)
        {
            resultType = "";

            return facts.Result == ResultKind.Count
                ? returnedType == "int" ? "(int)total" : null
                : returnedType == "long" ? "total" : null;
        }

        if (facts.Result == ResultKind.Any)
            return returnedType == "bool" ? "rows.Length > 0" : null;

        if (facts.Result == ResultKind.Sequence)
        {
            if (returned is IArrayTypeSymbol { Rank: 1 } array)
            {
                return SymbolEqualityComparer.Default.Equals(array.ElementType, shaped)
                    ? "rows"
                    : null;
            }

            return returned is INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } list
                && list.ConstructedFrom.ToDisplayString() == "System.Collections.Generic.List<T>"
                && SymbolEqualityComparer.Default.Equals(list.TypeArguments[0], shaped)
                ? "new global::System.Collections.Generic.List<" + shaped.ToDisplayString(_signature) + ">(rows)"
                : null;
        }

        // Every other terminal returns one row, so the method has to be declared to return one.
        if (!SymbolEqualityComparer.Default.Equals(GraphQLTypeFacts.UnwrapNullable(returned), shaped))
            return null;

        const string none = "\"The query returned no elements.\"";
        const string several = "\"The query returned more than one element.\"";

        return facts.Result switch
        {
            ResultKind.First => "rows.Length > 0 ? rows[0] "
                + ": throw new global::System.InvalidOperationException(" + none + ")",
            ResultKind.FirstOrDefault => "rows.Length > 0 ? rows[0] : default!",
            ResultKind.Last => "rows.Length > 0 ? rows[rows.Length - 1] "
                + ": throw new global::System.InvalidOperationException(" + none + ")",
            ResultKind.LastOrDefault => "rows.Length > 0 ? rows[rows.Length - 1] : default!",
            ResultKind.Single => "rows.Length == 1 ? rows[0] "
                + ": throw new global::System.InvalidOperationException("
                + "rows.Length == 0 ? " + none + " : " + several + ")",
            ResultKind.SingleOrDefault => "rows.Length == 0 ? default! : rows.Length == 1 ? rows[0] "
                + ": throw new global::System.InvalidOperationException(" + several + ")",
            _ => null
        };
    }

    /// <summary>What the method would have to return for its terminal, said plainly.</summary>
    private static string Expected(ChainFacts facts, ITypeSymbol shaped)
    {
        string name = shaped.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

        return facts.Result switch
        {
            ResultKind.Count => "Count needs an int",
            ResultKind.LongCount => "LongCount needs a long",
            ResultKind.Any => "Any needs a bool",
            ResultKind.Sequence => "a sequence terminal needs " + name + "[] or List<" + name + ">",
            _ => facts.Result + " needs " + name
        };
    }

    /// <summary>
    /// The imports a copied projection needs to mean what it meant where it was written.
    /// </summary>
    /// <remarks>
    /// Its own file's usings, plus the namespace the method is declared in — which is in scope
    /// at the original site by being its own, and has to be asked for here. Types are written out
    /// in full regardless; this is for everything a using is still load-bearing for, extension
    /// methods above all.
    /// </remarks>
    private static string Usings(MethodDeclarationSyntax declaration, IMethodSymbol method)
    {
        var imports = new List<string>();

        if (declaration.SyntaxTree.GetRoot() is CompilationUnitSyntax unit)
        {
            foreach (var import in unit.Usings)
                imports.Add(import.ToString().Trim());

            // A file-scoped or block namespace can carry usings of its own.
            foreach (var declared in unit.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>())
            {
                foreach (var import in declared.Usings)
                    imports.Add(import.ToString().Trim());
            }
        }

        if (!method.ContainingNamespace.IsGlobalNamespace)
            imports.Add("using " + method.ContainingNamespace.ToDisplayString() + ";");

        return string.Join("\n", imports.Distinct().OrderBy(x => x, StringComparer.Ordinal));
    }

    /// <summary>What a projection produces, and the code that produces it.</summary>
    private readonly record struct Shaping(ITypeSymbol Type, string Parameter, string Body);

    /// <summary>
    /// Re-emits a projection so the compiled call can shape rows exactly as the chain would.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rows are read as the <em>queried</em> type and the projection is then run over them —
    /// the same order <c>ResultMaterializer</c> does it in, and for the same reason. Reading
    /// straight into the projected type would mean matching JSON field names to members, so
    /// <c>new Row { Title = c.Name }</c> would read as null and <c>c.Continent.Name</c> would
    /// read as nothing at all: a wrong answer rather than a missing one, arrived at silently.
    /// </para>
    /// <para>
    /// What makes this possible without an expression tree is that the projection is already C#.
    /// It is not interpreted or rebuilt — it is the author's own lambda body, copied into the
    /// generated file with its type names written out in full, and compiled by their compiler as
    /// part of their assembly. So a rename shapes, a nested path shapes, and an expression
    /// shapes, because none of those are cases here: there is one case, and it is the source.
    /// </para>
    /// <para>
    /// What is declined is what the copy would not survive: a captured local, or a member the
    /// generated file cannot reach. Both are read from the symbols the body references rather
    /// than guessed at from its shape.
    /// </para>
    /// </remarks>
    private static Shaping? Shaper(
        LambdaExpressionSyntax projection,
        IMethodSymbol method,
        SemanticModel model,
        CancellationToken token)
    {
        if (projection.Body is not ExpressionSyntax body
            || model.GetSymbolInfo(projection, token).Symbol is not IMethodSymbol { Parameters.Length: 1 } lambda
            || model.GetTypeInfo(body, token).Type is not { } produced
            || produced.TypeKind == TypeKind.Error
            || produced.IsAnonymousType)
            return null;

        // Everything the body may name: its own parameter, any a nested lambda introduces, and
        // the parameters of the method being compiled — which the replacement has in scope
        // because they are its own.
        var available = new HashSet<ISymbol>(SymbolEqualityComparer.Default) { lambda.Parameters[0] };

        foreach (var parameter in method.Parameters)
            available.Add(parameter);

        foreach (var node in body.DescendantNodes())
        {
            if (node is ParameterSyntax declared && model.GetDeclaredSymbol(declared, token) is { } nested)
                available.Add(nested);
        }

        foreach (var node in body.DescendantNodesAndSelf())
        {
            if (model.GetSymbolInfo(node, token).Symbol is not { } symbol)
                continue;

            switch (symbol)
            {
                // A value from the enclosing scope, which the call site does not have.
                case ILocalSymbol:
                case IRangeVariableSymbol:
                    return null;

                case IParameterSymbol parameter when !available.Contains(parameter):
                    return null;

                // A private helper is not reachable from the file the replacement lives in.
                case IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol
                    when !model.Compilation.IsSymbolAccessibleWithin(symbol, model.Compilation.Assembly):
                    return null;
            }
        }

        var qualifier = new Qualifier(model, token);
        var written = qualifier.Visit(body);

        return qualifier.Declined || written is null
            ? null
            : new Shaping(produced, lambda.Parameters[0].Name, written.ToFullString().Trim());
    }

    /// <summary>
    /// Rewrites a copied expression's type names into ones that mean the same thing anywhere.
    /// </summary>
    /// <remarks>
    /// The generated file is not the file the projection was written in. Its usings are carried
    /// over, but a type nested in the declaring class cannot be imported by any of them — so
    /// every name that binds to a type is written out in full, which needs no import to resolve
    /// and cannot be captured by one either.
    /// </remarks>
    private sealed class Qualifier(SemanticModel model, CancellationToken token) : CSharpSyntaxRewriter
    {
        /// <summary>Set when a name binds to a type that cannot be written out.</summary>
        public bool Declined { get; private set; }

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
            => Qualify(node) ?? base.VisitIdentifierName(node);

        public override SyntaxNode? VisitGenericName(GenericNameSyntax node)
            => Qualify(node) ?? base.VisitGenericName(node);

        private SyntaxNode? Qualify(SimpleNameSyntax node)
        {
            // The right-hand side of a member access is reached through its left, which is
            // qualified instead; a property named in an initializer is not a type at all.
            if (node.Parent is MemberAccessExpressionSyntax access && access.Name == node)
                return null;

            if (model.GetSymbolInfo(node, token).Symbol is not ITypeSymbol type)
                return null;

            if (type.TypeKind == TypeKind.Error || type.IsAnonymousType)
            {
                Declined = true;
                return node;
            }

            return SyntaxFactory.ParseTypeName(type.ToDisplayString(_signature)).WithTriviaFrom(node);
        }
    }

    /// <summary>
    /// The sort argument an ordering means, written out whole.
    /// </summary>
    /// <remarks>
    /// An ordering binds no value at all — which member and which direction are both in the
    /// syntax — so unlike a filter this leaves no holes. Mirrors
    /// <c>FilterTranslator.TranslateOrdering</c>: one object per key, nested along the member
    /// path, with the direction as its leaf.
    /// </remarks>
    private static string? Sort(ChainFacts facts, SemanticModel model, CancellationToken token)
    {
        var builder = new StringBuilder();
        string indent = new(' ', 16);

        builder.Append(indent).Append("writer.WriteStartArray();\n");

        foreach (var (key, descending) in facts.Ordering)
        {
            if (model.GetSymbolInfo(key, token).Symbol is not IMethodSymbol { Parameters.Length: 1 } lambda
                || key.Body is not ExpressionSyntax body
                || Path(body, lambda.Parameters[0], model, token) is not { } path)
                return null;

            foreach (string segment in path)
            {
                builder.Append(indent).Append("writer.WriteStartObject();\n")
                    .Append(indent).Append("writer.WritePropertyName(\"").Append(segment).Append("\");\n");
            }

            builder.Append(indent).Append("writer.WriteStringValue(\"")
                .Append(descending ? "DESC" : "ASC").Append("\");\n");

            for (int i = 0; i < path.Count; i++)
                builder.Append(indent).Append("writer.WriteEndObject();\n");
        }

        builder.Append(indent).Append("writer.WriteEndArray();\n");

        return builder.ToString();
    }

    /// <summary>The field path a member chain names, or null when it is not one.</summary>
    private static IReadOnlyList<string>? Path(
        ExpressionSyntax expression,
        IParameterSymbol parameter,
        SemanticModel model,
        CancellationToken token)
    {
        var path = new List<string>();
        var current = expression;

        while (true)
        {
            switch (current)
            {
                case ParenthesizedExpressionSyntax parenthesized:
                    current = parenthesized.Expression;
                    continue;

                case MemberAccessExpressionSyntax member:
                {
                    if (model.GetSymbolInfo(member, token).Symbol is not IPropertySymbol property
                        || GraphQLTypeFacts.IsIgnored(property))
                        return null;

                    path.Insert(0, GraphQLTypeFacts.FieldName(property));
                    current = member.Expression;
                    continue;
                }

                case IdentifierNameSyntax identifier:
                    return SymbolEqualityComparer.Default.Equals(
                            model.GetSymbolInfo(identifier, token).Symbol, parameter)
                        && path.Count > 0
                        ? path
                        : null;

                default:
                    return null;
            }
        }
    }

    /// <summary>A page size, written as the number it is.</summary>
    private static string PageSize(string value)
        => new string(' ', 16) + "writer.WriteNumberValue(" + value + ");\n";

    /// <summary>
    /// The code a comparison's value is read from at the call, or null when there is none.
    /// </summary>
    /// <remarks>
    /// Only two things qualify, and for the same reason: they mean the same where the call is as
    /// they did where the chain was written. A parameter is handed over by the caller, and a
    /// constant is itself — re-rendered as a literal rather than by its name, since the name it
    /// was written under may not be in scope in the generated file.
    /// </remarks>
    private static string? Value(
        ExpressionSyntax expression,
        IMethodSymbol method,
        SemanticModel model,
        CancellationToken token)
    {
        if (expression is IdentifierNameSyntax identifier
            && Bound(identifier, method, model, token) is { } parameter)
            return parameter;

        return model.GetConstantValue(expression, token) is { HasValue: true, Value: var value }
            ? Constant(value)
            : null;
    }

    /// <summary>
    /// How the replacement reaches the client the chain posts through.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A parameter is simply forwarded — the caller passes it, so the replacement has it. A member
    /// of the declaring type is read off the receiver, which an interceptor for an instance method
    /// is handed; but only when it is a member the generated file can see, because that file is
    /// somewhere else and a private field is private to everyone including it.
    /// </para>
    /// <para>
    /// This is the one place the two ways of declaring a query genuinely differ. A document is
    /// implemented <em>inside</em> the declaring type, so it can read anything the type declares;
    /// a chain is replaced at the call site, so it can read only what the call site could. A chain
    /// over a private field keeps its body and runs, which is the right answer rather than a
    /// worse one.
    /// </para>
    /// </remarks>
    private static (string? Expression, Accessor? Reach) Client(
        ExpressionSyntax receiver,
        IMethodSymbol method,
        SemanticModel model,
        CancellationToken token)
    {
        switch (receiver)
        {
            case IdentifierNameSyntax identifier:
            {
                if (Bound(identifier, method, model, token) is { } parameter)
                    return (parameter, null);

                // A member named without `this.`, or a primary constructor's parameter, which for
                // a method of the type is in scope the same way.
                return Held(model.GetSymbolInfo(identifier, token).Symbol, method, model);
            }

            // `this.Client`, said in full.
            case MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } access:
                return Held(model.GetSymbolInfo(access, token).Symbol, method, model);

            default:
                return (null, null);
        }
    }

    /// <summary>How something the declaring type holds is named from the replacement.</summary>
    /// <remarks>
    /// Three ways, in order of what costs least. A member the generated file can see is named
    /// outright. One it cannot is reached through an accessor the runtime binds. A parameter of
    /// the primary constructor is neither — it is a field the compiler made, under a name no C#
    /// can write — so it is reached the same way, by the name it has in metadata.
    /// </remarks>
    private static (string? Expression, Accessor? Reach) Held(
        ISymbol? symbol,
        IMethodSymbol method,
        SemanticModel model)
    {
        if (symbol is IParameterSymbol captured)
            return Captured(captured, method);

        if (symbol is not (IFieldSymbol or IPropertySymbol))
            return (null, null);

        // A static member is reached through its type; an instance one through the receiver the
        // interceptor is handed, which is the method's own `this`.
        if (symbol.IsStatic)
        {
            return model.Compilation.IsSymbolAccessibleWithin(symbol, model.Compilation.Assembly)
                ? (symbol.ContainingType.ToDisplayString(_signature) + "." + symbol.Name, null)
                : (null, null);
        }

        if (method.IsStatic)
            return (null, null);

        if (model.Compilation.IsSymbolAccessibleWithin(symbol, model.Compilation.Assembly))
            return ("receiver." + symbol.Name, null);

        // Out of reach by name, but not out of reach: a field has metadata an accessor can bind
        // to. A property does not, in the same way — its getter is a method — so it is declined
        // rather than guessed at.
        return symbol is IFieldSymbol field ? Accessed(field, field.Name, method) : (null, null);
    }

    /// <summary>
    /// The field standing behind a captured primary constructor parameter.
    /// </summary>
    /// <remarks>
    /// Found among the type's own members rather than spelled out from the parameter's name: how a
    /// compiler writes a capture is its business, and reading the name it actually used is the
    /// difference between working and working until it changes.
    /// </remarks>
    private static (string? Expression, Accessor? Reach) Captured(
        IParameterSymbol parameter,
        IMethodSymbol method)
    {
        if (method.IsStatic
            || parameter.ContainingSymbol is not IMethodSymbol { MethodKind: MethodKind.Constructor } constructor
            || !SymbolEqualityComparer.Default.Equals(constructor.ContainingType, method.ContainingType))
            return (null, null);

        foreach (var member in method.ContainingType.GetMembers())
        {
            if (member is IFieldSymbol field
                && !field.CanBeReferencedByName
                && SymbolEqualityComparer.Default.Equals(field.Type, parameter.Type)
                && field.Name.Contains(parameter.Name))
                return Accessed(field, field.Name, method);
        }

        return (null, null);
    }

    /// <summary>An accessor for one field, and the call that reads it.</summary>
    private static (string? Expression, Accessor? Reach) Accessed(
        IFieldSymbol field,
        string name,
        IMethodSymbol method)
    {
        // An accessor names its declaring type, and a generic one cannot be named once for all of
        // its instantiations.
        if (method.ContainingType.IsGenericType)
            return (null, null);

        var reach = new Accessor(
            "Client",
            name,
            method.ContainingType.ToDisplayString(_signature),
            field.Type.ToDisplayString(_signature));

        return ("Client(receiver)", reach);
    }

    /// <summary>The parameter an identifier names, or null when it names something else.</summary>
    private static string? Bound(
        IdentifierNameSyntax identifier,
        IMethodSymbol method,
        SemanticModel model,
        CancellationToken token)
        => model.GetSymbolInfo(identifier, token).Symbol is IParameterSymbol parameter
            && method.Parameters.Any(p => SymbolEqualityComparer.Default.Equals(p, parameter))
            ? parameter.Name
            : null;

    /// <summary>A constant written back out as a literal, or null when it is not one this writes.</summary>
    private static string? Constant(object? value)
        => value switch
        {
            null => "null",
            string text => SyntaxFactory.Literal(text).ToFullString(),
            bool flag => flag ? "true" : "false",
            int number => SyntaxFactory.Literal(number).ToFullString(),
            long number => SyntaxFactory.Literal(number).ToFullString(),
            double number => SyntaxFactory.Literal(number).ToFullString(),
            decimal number => SyntaxFactory.Literal(number).ToFullString(),
            _ => null
        };

    /// <summary>A parameter as the interceptor has to declare it, default included.</summary>
    /// <remarks>
    /// An interceptor stands in for what it replaces, so a default that is not reproduced is a
    /// compile error at every call that relied on it.
    /// </remarks>
    private static string? Default(IParameterSymbol parameter)
    {
        string rendered = parameter.Type.ToDisplayString(_signature) + " " + parameter.Name;

        if (!parameter.HasExplicitDefaultValue)
            return rendered;

        if (parameter.ExplicitDefaultValue is null)
            return rendered + (parameter.Type.IsReferenceType ? " = null" : " = default");

        return Constant(parameter.ExplicitDefaultValue) is { } constant
            ? rendered + " = " + constant
            : null;
    }

    private static bool Is(ITypeSymbol? type, string qualified)
        => type?.ToDisplayString(_qualified) == qualified;

    /// <summary>
    /// How a method is named when the name has to tell it from every other method.
    /// </summary>
    /// <remarks>
    /// The containing type and the parameters, because neither is optional for this job: a
    /// fully-qualified display of a method leaves both out by default, so two methods called
    /// <c>ByCodeAsync</c> in two services would be one key — and every call to either would be
    /// intercepted by both, which the compiler reports as a call intercepted twice.
    /// </remarks>
    private static readonly SymbolDisplayFormat _key = SymbolDisplayFormat.FullyQualifiedFormat
        .WithMemberOptions(SymbolDisplayMemberOptions.IncludeContainingType
            | SymbolDisplayMemberOptions.IncludeParameters)
        .WithParameterOptions(SymbolDisplayParameterOptions.IncludeType);

    private static readonly SymbolDisplayFormat _signature =
        SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions
            | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
            | SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    private static readonly SymbolDisplayFormat _qualified =
        SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions
            & ~SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    private static void Emit(
        SourceProductionContext context,
        ImmutableArray<Compiled> compiled,
        ImmutableArray<CallSite> calls)
    {
        if (compiled.IsDefaultOrEmpty)
            return;

        foreach (var query in compiled.Distinct().Where(x => x.Declined is not null))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                GraphQLDiagnostics.NotCompiled, query.Where, query.Name, query.Declined));
        }

        var sites = calls.Distinct()
            .GroupBy(call => call.Key)
            .ToDictionary(group => group.Key, group => group.Select(call => call.Attribute).ToList());

        // A method nobody calls has nothing to intercept, and an interceptor with no location
        // does not compile.
        var queries = compiled.Distinct()
            .Where(query => query.Declined is null && sites.ContainsKey(query.Key))
            .ToList();

        if (queries.Count == 0)
            return;

        // A file each, rather than one file for all of them. A compiled projection is the
        // author's own source, so the file it lands in has to carry the imports its own file had
        // — and two queries from two files may import names that collide.
        int index = 0;
        foreach (var query in queries.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            context.AddSource(
                "GraphQLCompiledQuery" + index + ".g.cs",
                SourceText.From(File(query, sites[query.Key], index), Encoding.UTF8));

            index++;
        }
    }

    /// <summary>One compiled query's file: the replacement, and the payload it posts.</summary>
    private static string File(Compiled query, List<string> sites, int index)
    {
        var builder = new StringBuilder("// <auto-generated/>\n#nullable enable\n\n");

        if (query.Usings.Length > 0)
            builder.Append(query.Usings).Append("\n\n");

        builder.Append("""
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

            namespace Feather.GraphQL.Linq.Generated
            {
                /// <summary>
                /// One compiled query, replacing every call to the method whose chain it was
                /// written from. The chain itself never runs.
                /// </summary>
                file static class CompiledQueries
                {

            """);

        Method(builder, query, sites, index);
        Reach(builder, query);

        builder.Append("    }\n\n");

        ResponseStructWriter.Write(builder, query.Reply, query.Name);
        Parser(builder, query);

        if (!query.Payload.IsEmpty)
            Payload(builder, query, index);

        return builder.Append("}\n").ToString();
    }

    /// <summary>The replacement for one method: post the document, read the reply, return it.</summary>
    private static void Method(StringBuilder builder, Compiled query, List<string> sites, int index)
    {
        string token = query.CancellationToken ?? "default";

        foreach (string site in sites)
            builder.Append("        ").Append(site).Append('\n');

        builder.Append("        public static async ").Append(query.ReturnType)
            .Append(" Compiled").Append(index).Append('(');

        if (query.Receiver is { } receiver)
        {
            builder.Append("this ").Append(receiver).Append(" receiver")
                .Append(query.Parameters.Length > 0 ? ", " : "");
        }

        builder.Append(query.Parameters).Append(")\n")
            .Append("        {\n")
            .Append("            using var response = await global::Feather.GraphQL.Http.HttpExtensions")
            .Append(".SendGraphQLQueryAsync(").Append(query.Client).Append(",\n")
            .Append("                ").Append(Literal(query.Document)).Append(",\n")
            .Append("                ");

        if (query.Payload.IsEmpty)
            builder.Append("global::Feather.GraphQL.GraphQLNoVariables.Instance");
        else
        {
            builder.Append("new Variables").Append(index).Append('(')
                .Append(string.Join(", ", query.Holes.Select(hole => hole.Binding))).Append(')');
        }

        builder.Append(",\n                ").Append(token).Append(").ConfigureAwait(false);\n\n")
            .Append("            ");

        // A count reads the connection's own field and never sees a row; everything else reads
        // rows, from inside the paging wrapper when there is one.
        if (query.Read == Reader.Count)
        {
            // A reply with no count is a server that does not expose one, which is a different
            // failure from a collection with nothing in it and is reported rather than returned.
            builder.Append("long total = await global::Feather.GraphQL.Http.HttpExtensions")
                .Append(".ReadGraphQLReplyAsync(response, ").Append(query.Name).Append("_Parser.ParseCount, ")
                .Append(token).Append(").ConfigureAwait(false)\n")
                .Append("                ?? throw new global::Feather.GraphQL.Http.GraphQLHttpException(")
                .Append("null, response);\n\n");
        }
        else
        {
            // The generated parser reads this query's own reply: no contract to resolve, and
            // nothing materialized that is not the answer. A projection still shapes afterwards,
            // over rows that mirror the payload rather than over the queried type.
            builder.Append("var ").Append(query.Shaper is null ? "rows" : "source")
                .Append(" = await global::Feather.GraphQL.Http.HttpExtensions")
                .Append(".ReadGraphQLReplyAsync(response, ").Append(query.Name).Append("_Parser.Parse, ")
                .Append(token).Append(").ConfigureAwait(false);\n\n");

            Shape(builder, query);
        }

        builder.Append("            return ").Append(query.Result).Append(";\n")
            .Append("        }\n\n");
    }

    /// <summary>
    /// Writes the accessor that reaches a client the replacement cannot name.
    /// </summary>
    /// <remarks>
    /// Bound by the runtime against the field's metadata: no reflection, nothing to trim away, and
    /// nothing decided at run time — which is what lets a query over a private field, or over a
    /// captured primary constructor parameter, compile like any other.
    /// </remarks>
    private static void Reach(StringBuilder builder, Compiled query)
    {
        if (query.Reach is not { } reach)
            return;

        builder.Append("        /// <summary>Reads the client <c>").Append(reach.Owner)
            .Append("</c> holds, which is private to it.</summary>\n")
            .Append("        [global::System.Runtime.CompilerServices.UnsafeAccessor(\n")
            .Append("            global::System.Runtime.CompilerServices.UnsafeAccessorKind.Field,\n")
            .Append("            Name = ").Append(Literal(reach.Field)).Append(")]\n")
            .Append("        private static extern ref ").Append(reach.Type).Append(' ')
            .Append(reach.Method).Append("(").Append(reach.Owner).Append(" receiver);\n\n");
    }

    /// <summary>
    /// Writes the parser the transport hands this query's bytes to.
    /// </summary>
    /// <remarks>
    /// The whole of what the transport knows about a compiled read: a method taking the reply's
    /// bytes and giving back the rows, with the envelope's two answers as out parameters. What it
    /// does inside is this query's own generated reader, which nothing outside this file names.
    /// </remarks>
    private static void Parser(StringBuilder builder, Compiled query)
    {
        bool count = query.Reply is null;

        builder.Append("    /// <summary>Reads one reply to <c>").Append(query.Name)
            .Append("</c>.</summary>\n")
            .Append("    file static class ").Append(query.Name).Append("_Parser\n")
            .Append("    {\n")
            .Append("        public static ").Append(count ? "long?" : query.Reply!.Name + "[]")
            .Append(count ? " ParseCount(\n" : " Parse(\n")
            .Append("            global::System.ReadOnlySpan<byte> json,\n")
            .Append("            out bool hasData,\n")
            .Append("            out bool hasErrors)\n")
            .Append("        {\n")
            .Append("            var reply = ").Append(query.Name).Append("_Reply.Read(json);\n\n")
            .Append("            hasData = reply.HasData;\n")
            .Append("            hasErrors = reply.HasErrors;\n\n")
            .Append("            return reply.").Append(count ? "TotalCount" : "Rows").Append(";\n")
            .Append("        }\n")
            .Append("    }\n\n");
    }

    /// <summary>
    /// Runs the projection over the rows, once each, in place of the sequence that was read.
    /// </summary>
    /// <remarks>
    /// A loop rather than <c>Select</c>: the length is known, so the result is allocated once and
    /// filled, with no enumerator and no intermediate. The body is the author's own — the row is
    /// bound to a local under the name their lambda gave it, and what follows is what they wrote.
    /// </remarks>
    private static void Shape(StringBuilder builder, Compiled query)
    {
        if (query.Shaper is not { } projection)
            return;

        builder.Append("            var rows = new ").Append(query.ShapedType).Append("[source.Length];\n")
            .Append("\n")
            .Append("            for (int i = 0; i < source.Length; i++)\n")
            .Append("            {\n")
            .Append("                var ").Append(query.ShaperParameter).Append(" = source[i];\n")
            .Append("\n")
            .Append("                rows[i] = ").Append(projection).Append(";\n")
            .Append("            }\n\n");
    }

    /// <summary>
    /// The variables one compiled query binds, written from the values the caller passed.
    /// </summary>
    /// <remarks>
    /// One property per variable the document declared, in the document's own numbering — the
    /// filter, the ordering, the page. The same payload <see cref="QueryInterceptorGenerator"/>
    /// emits, minus the part that reads values out of the expression tree: here they arrive as
    /// arguments, because the call the tree would have described is the call this replaces.
    /// </remarks>
    private static void Payload(StringBuilder builder, Compiled query, int index)
    {
        builder.Append("    /// <summary>The variables of Compiled").Append(index)
            .Append(", written from its own arguments.</summary>\n")
            .Append("    file sealed class Variables").Append(index)
            .Append(" : global::Feather.GraphQL.IGraphQLVariables\n    {\n");

        for (int i = 0; i < query.Holes.Length; i++)
            builder.Append("        private readonly ").Append(query.Holes[i].Type).Append(" _").Append(i).Append(";\n");

        builder.Append("\n        public Variables").Append(index).Append('(')
            .Append(string.Join(", ",
                query.Holes.Select((hole, i) => hole.Type + " v" + i)))
            .Append(")\n        {\n");

        for (int i = 0; i < query.Holes.Length; i++)
            builder.Append("            _").Append(i).Append(" = v").Append(i).Append(";\n");

        builder.Append("        }\n\n")
            .Append("        public bool IsEmpty => false;\n\n")
            .Append("        public void WriteTo(global::System.Text.Json.Utf8JsonWriter writer)\n")
            .Append("        {\n")
            .Append("            writer.WriteStartObject();\n");

        foreach (var part in query.Payload)
        {
            builder.Append("            writer.WritePropertyName(\"").Append(part.Name).Append("\");\n")
                .Append(part.Body);
        }

        builder.Append("            writer.WriteEndObject();\n")
            .Append("        }\n    }\n\n");
    }

    private static string Literal(string value)
        => SyntaxFactory.Literal(value).ToFullString();
}
