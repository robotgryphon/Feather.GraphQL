using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Feather.GraphQL.Linq.Analyzers;

/// <summary>How a chain's terminal reduces the sequence, mirroring the runtime enum.</summary>
internal enum ResultKind
{
    Sequence, First, FirstOrDefault, Single, SingleOrDefault, Last, LastOrDefault, Any, Count, LongCount
}

/// <summary>Paging, mirroring the runtime enum.</summary>
internal enum Paging { None = 0, Cursor = 1, Offset = 2 }

/// <summary>
/// Everything about one chain that decides what its document says.
/// </summary>
/// <remarks>
/// Values are deliberately absent. <c>Take(n)</c> and <c>Where(p =&gt; p.Age &gt; age)</c> bind
/// their values to variables, so the document records only <em>that</em> they were bound and in
/// what order — which is exactly why a document can be printed before the values exist.
/// </remarks>
internal sealed class ChainFacts
{
    public string RootField = "";
    public ITypeSymbol ElementType = null!;
    public LambdaExpressionSyntax? Projection;
    public bool HasFilter;

    /// <summary>
    /// Every predicate the chain applied, in the order it applied them.
    /// </summary>
    /// <remarks>
    /// Kept because the filter's <em>shape</em> is compile-time knowledge even though its values
    /// are not: which fields, which operations, how they nest. Only the leaves have to wait for
    /// the expression tree. A predicate this cannot see is simply absent, and the chain falls
    /// back to being lowered whole at runtime.
    /// </remarks>
    public readonly List<LambdaExpressionSyntax> Predicates = [];

    /// <summary>True when a Where was applied whose predicate could not be captured.</summary>
    /// <remarks>
    /// A filter-shape overload, or a predicate that is not a lambda written at the call site.
    /// The document is still printable — it says only that a filter exists — but its payload is
    /// not, so anything reading <see cref="Predicates"/> has to decline.
    /// </remarks>
    public bool HasOpaquePredicate;
    public bool HasOrdering;
    public bool HasSkip;
    public bool HasTake;
    public bool HasLast;

    /// <summary>
    /// The keys the chain ordered by, in the order it applied them.
    /// </summary>
    /// <remarks>
    /// Unlike a filter, an ordering binds no value at all: which member and which direction are
    /// both in the syntax, so a chain that only orders has a payload the compiler can write out
    /// whole. Captured for the callers that can use that; the document says only that an order
    /// exists either way.
    /// </remarks>
    public readonly List<(LambdaExpressionSyntax Key, bool Descending)> Ordering = [];

    /// <summary>True when an ordering was applied whose key could not be captured.</summary>
    public bool HasOpaqueOrdering;

    /// <summary>The expression <c>Take</c> was given, when it was given one this can see.</summary>
    public ExpressionSyntax? TakeValue;

    /// <summary>The expression <c>Skip</c> was given, when it was given one this can see.</summary>
    public ExpressionSyntax? SkipValue;

    /// <summary>Whether the chain asked for a page itself, rather than a terminal asking for one.</summary>
    /// <remarks>
    /// The difference is whether the page's size is knowable here. <c>Take(n)</c> binds whatever
    /// <c>n</c> turns out to be; <c>First()</c> binds one, always, and the compiler can say so.
    /// </remarks>
    public bool ExplicitTake;

    /// <summary>The page size a terminal asked the server for, when one did.</summary>
    public int? ResultPage;
    public ResultKind Result = ResultKind.Sequence;
    public Paging Paging;
    public string? FilterInput;
    public string? SortInput;

    /// <summary>True when the chain chose where to post, or which dialect to lower filters in.</summary>
    /// <remarks>
    /// Neither changes a character of the document, which is why the document printer ignores
    /// both. They matter to a caller that writes the <em>request</em> rather than the text: one
    /// decides the URL and the other decides the operation names inside the payload, and getting
    /// either wrong is a request that goes somewhere else or asks something else.
    /// </remarks>
    public bool SetsEndpoint;

    /// <inheritdoc cref="SetsEndpoint"/>
    public bool SetsFilterProvider;

    // Argument names, defaulted to HotChocolate's.
    public string FilterArgument = "where";
    public string OrderArgument = "order";
    public string TakeArgument = "take";
    public string FirstArgument = "first";
    public string SkipArgument = "skip";
    public string LastArgument = "last";

    public bool IsCount => Result is ResultKind.Count or ResultKind.LongCount;
    public bool HasPaging => HasSkip || HasTake || HasLast;

    /// <summary>
    /// A copy, so each use of a stored queryable can be walked without the others seeing it.
    /// </summary>
    public ChainFacts Copy() => (ChainFacts)MemberwiseClone();
}

/// <summary>
/// Reads a LINQ chain from syntax, from its entry point to whatever consumes it.
/// </summary>
/// <remarks>
/// Declines far more readily than the runtime parser does, and the asymmetry is the point: the
/// runtime sees the whole chain as one expression tree and has no choice but to translate it,
/// while this only has to recognise the chains it can be certain about. Everything else falls
/// through to the runtime, which is where it was handled before any of this existed.
/// </remarks>
internal static class QueryChainReader
{
    private const string WhereExtensions = "Feather.GraphQL.Linq.Filtering.GraphQLWhereExtensions";
    private const string AsyncExtensions = "Feather.GraphQL.Linq.Query.GraphQLAsyncQueryableExtensions";
    private const string QueryableExtensions = "Feather.GraphQL.Linq.Query.GraphQLQueryableExtensions";

    /// <summary>
    /// How many times a chain may be picked up again out of a local before this gives up.
    /// </summary>
    /// <remarks>
    /// Progressive composition is worth following — it is how most real code reads — but each
    /// hop multiplies the branches to check, so it is bounded rather than unbounded.
    /// </remarks>
    private const int MaximumHops = 3;

    /// <summary>
    /// Reads the chain hanging off an entry point, returning one description per place it is
    /// finally consumed, or null when any part of it is not certain.
    /// </summary>
    /// <remarks>
    /// More than one description means the chain was stored and used several times. They all
    /// share a provider, so they must all agree on the document — which the caller checks.
    /// </remarks>
    public static IReadOnlyList<ChainFacts>? Read(
        InvocationExpressionSyntax entry,
        IMethodSymbol method,
        SemanticModel model,
        CancellationToken token)
    {
        if (method.TypeArguments.Length != 1)
            return null;

        var facts = new ChainFacts { ElementType = method.TypeArguments[0] };

        return ReadEntryArguments(entry, method, model, facts, token)
            ? Continue(entry, facts, model, token, hops: 0)
            : null;
    }

    /// <summary>
    /// Walks the operators hanging off one expression, then either finishes or follows the local
    /// the result was stored in.
    /// </summary>
    private static IReadOnlyList<ChainFacts>? Continue(
        ExpressionSyntax start,
        ChainFacts facts,
        SemanticModel model,
        CancellationToken token,
        int hops)
    {
        var current = start;

        while (current.Parent is MemberAccessExpressionSyntax access
               && access.Expression == current
               && access.Parent is InvocationExpressionSyntax call)
        {
            if (model.GetSymbolInfo(call, token).Symbol is not IMethodSymbol op)
                return null;

            var outcome = Apply(op, call, facts, model, token);

            if (outcome == Step.Decline)
                return null;

            if (outcome == Step.NotAnOperator)
                break;

            current = call;
        }

        if (IsComplete(current, facts, model, token))
            return [facts];

        return hops < MaximumHops
            ? FollowLocal(current, facts, model, token, hops)
            : null;
    }

    /// <summary>
    /// Picks the chain back up where it was stored, and walks each use of it.
    /// </summary>
    /// <remarks>
    /// Every use has to be visible and has to consume the chain itself. A local that is returned,
    /// passed somewhere, captured, or reassigned could be composed anywhere, and a document
    /// printed for what is visible here would be the wrong document rather than a missing one.
    /// </remarks>
    private static IReadOnlyList<ChainFacts>? FollowLocal(
        ExpressionSyntax chain,
        ChainFacts facts,
        SemanticModel model,
        CancellationToken token,
        int hops)
    {
        if (chain.Parent is not EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax declarator }
            || model.GetDeclaredSymbol(declarator, token) is not ILocalSymbol local)
            return null;

        var scope = Scope(declarator);
        if (scope is null)
            return null;

        var completions = new List<ChainFacts>();

        foreach (var identifier in scope.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (identifier.Identifier.ValueText != local.Name
                || !SymbolEqualityComparer.Default.Equals(
                    model.GetSymbolInfo(identifier, token).Symbol, local))
                continue;

            // Assigned to again, so what it holds later is not what was read here.
            if (identifier.Parent is AssignmentExpressionSyntax assignment && assignment.Left == identifier)
                return null;

            var branch = Continue(identifier, facts.Copy(), model, token, hops + 1);
            if (branch is null)
                return null;

            completions.AddRange(branch);
        }

        // Never used is never executed, and there is nothing to precompile.
        return completions.Count > 0 ? completions : null;
    }

    /// <summary>The body a local lives in, which is as far as its uses can be.</summary>
    private static SyntaxNode? Scope(SyntaxNode declarator)
    {
        foreach (var ancestor in declarator.Ancestors())
        {
            switch (ancestor)
            {
                case BaseMethodDeclarationSyntax:
                case LocalFunctionStatementSyntax:
                case AccessorDeclarationSyntax:
                case AnonymousFunctionExpressionSyntax:
                // Top-level statements, where the body is the file itself.
                case CompilationUnitSyntax:
                    return ancestor;
            }
        }

        return null;
    }

    private enum Step { Applied, NotAnOperator, Decline }

    /// <summary>
    /// Folds one call into the chain. Mirrors <c>QueryChain.Apply</c>, minus everything that
    /// reads a value.
    /// </summary>
    private static Step Apply(
        IMethodSymbol op,
        InvocationExpressionSyntax call,
        ChainFacts facts,
        SemanticModel model,
        CancellationToken token)
    {
        string? container = Outermost(op.ContainingType)?.ToDisplayString();

        if (container == WhereExtensions)
        {
            // A predicate written against the server's filter input rather than against the
            // element. It is captured like any other: the runtime adds it to the same list of
            // predicates and lowers it the same way, and what differs is only the type it is
            // written against — which neither the lowering nor this needs to know, since both
            // walk member paths off the lambda's own parameter.
            bool written = false;

            foreach (var argument in call.ArgumentList.Arguments)
            {
                if (argument.Expression is LambdaExpressionSyntax lambda)
                {
                    facts.Predicates.Add(lambda);
                    written = true;
                    continue;
                }

                // The overload that names the filter argument inline sets what
                // WithGraphQLArguments would have set, so a non-literal name is one this cannot
                // know.
                if (model.GetConstantValue(argument.Expression, token).Value is string name)
                    facts.FilterArgument = name;
                else
                    return Step.Decline;
            }

            facts.HasFilter = true;

            // A predicate handed in rather than written at the call is one there is no syntax to
            // read, which is what opaque has always meant here.
            if (!written)
                facts.HasOpaquePredicate = true;

            return Step.Applied;
        }

        if (container != "System.Linq.Queryable")
            return Step.NotAnOperator;

        switch (op.Name)
        {
            case "Where":
                facts.HasFilter = true;
                Predicate(facts, call);
                return Step.Applied;

            case "OrderBy":
            case "OrderByDescending":
            case "ThenBy":
            case "ThenByDescending":
                facts.HasOrdering = true;
                Ordering(facts, call, op.Name.EndsWith("Descending", StringComparison.Ordinal));
                return Step.Applied;

            case "Select":
                if (call.ArgumentList.Arguments.Count != 1
                    || call.ArgumentList.Arguments[0].Expression is not LambdaExpressionSyntax lambda)
                    return Step.Decline;

                // Last Select wins, as the runtime parser does.
                facts.Projection = lambda;
                return Step.Applied;

            case "Skip":
                facts.HasSkip = true;
                facts.SkipValue = Argument(call);
                return Step.Applied;

            case "Take":
                facts.HasTake = true;
                facts.TakeValue = Argument(call);
                return Step.Applied;

            case "First": return Result(facts, ResultKind.First, call);
            case "FirstOrDefault": return Result(facts, ResultKind.FirstOrDefault, call);
            case "Single": return Result(facts, ResultKind.Single, call);
            case "SingleOrDefault": return Result(facts, ResultKind.SingleOrDefault, call);
            case "Last": return Result(facts, ResultKind.Last, call);
            case "LastOrDefault": return Result(facts, ResultKind.LastOrDefault, call);
            case "Any": return Result(facts, ResultKind.Any, call);
            case "Count": return Result(facts, ResultKind.Count, call);
            case "LongCount": return Result(facts, ResultKind.LongCount, call);

            default:
                return Step.Decline;
        }
    }

    /// <summary>Captures an ordering's key, or notes that it could not be.</summary>
    /// <remarks>
    /// A comparer overload is declined rather than recorded: it orders by something the server's
    /// sort input cannot express, and the runtime rejects it too.
    /// </remarks>
    private static void Ordering(ChainFacts facts, InvocationExpressionSyntax call, bool descending)
    {
        if (call.ArgumentList.Arguments.Count == 1
            && call.ArgumentList.Arguments[0].Expression is LambdaExpressionSyntax key)
        {
            facts.Ordering.Add((key, descending));
            return;
        }

        facts.HasOpaqueOrdering = true;
    }

    /// <summary>The single argument a call was given, when it has exactly one.</summary>
    private static ExpressionSyntax? Argument(InvocationExpressionSyntax call)
        => call.ArgumentList.Arguments.Count == 1
            ? call.ArgumentList.Arguments[0].Expression
            : null;

    /// <summary>Captures a Where's predicate, or notes that it could not be.</summary>
    private static void Predicate(ChainFacts facts, InvocationExpressionSyntax call)
    {
        if (call.ArgumentList.Arguments.Count == 1
            && call.ArgumentList.Arguments[0].Expression is LambdaExpressionSyntax lambda)
        {
            facts.Predicates.Add(lambda);
            return;
        }

        facts.HasOpaquePredicate = true;
    }

    /// <summary>
    /// Records a terminal operator, folding its optional predicate overload into the chain's
    /// filter exactly as <c>First(p =&gt; …)</c> means <c>Where(…).First()</c>.
    /// </summary>
    private static Step Result(ChainFacts facts, ResultKind kind, InvocationExpressionSyntax call)
    {
        if (facts.Result != ResultKind.Sequence)
            return Step.Decline;

        // `First(p => …)` means `Where(…).First()`, so its predicate is part of the filter.
        if (call.ArgumentList.Arguments.Count > 0)
        {
            facts.HasFilter = true;

            foreach (var argument in call.ArgumentList.Arguments)
            {
                if (argument.Expression is LambdaExpressionSyntax lambda)
                    facts.Predicates.Add(lambda);
                else
                    facts.HasOpaquePredicate = true;
            }
        }

        facts.Result = kind;
        return Step.Applied;
    }

    /// <summary>
    /// Whether the chain visibly ends here.
    /// </summary>
    /// <remarks>
    /// The load-bearing check. A chain that continues out of sight — assigned to a variable,
    /// returned, passed as an argument — may have a <c>First()</c> put on it later, and a
    /// document printed without that <c>First()</c> would be a wrong document rather than a
    /// missing one. So the chain must end in something that consumes it here: a terminal
    /// operator, a materializing call, or a foreach.
    /// </remarks>
    private static bool IsComplete(
        ExpressionSyntax chain,
        ChainFacts facts,
        SemanticModel model,
        CancellationToken token)
    {
        // A result operator is itself the end of the chain.
        if (facts.Result != ResultKind.Sequence)
            return true;

        switch (chain.Parent)
        {
            // foreach (var x in chain)
            case ForEachStatementSyntax loop when loop.Expression == chain:
                return true;

            case MemberAccessExpressionSyntax access
                when access.Expression == chain && access.Parent is InvocationExpressionSyntax call:
            {
                if (model.GetSymbolInfo(call, token).Symbol is not IMethodSymbol consumer)
                    return false;

                string? container = Outermost(consumer.ContainingType)?.ToDisplayString();

                // Materializers read the sequence here and cannot compose further.
                if (container == "System.Linq.Enumerable")
                    return consumer.Name is "ToArray" or "ToList" or "ToHashSet" or "ToDictionary";

                // Translating the document is not executing it, and reads no result.
                if (container == QueryableExtensions)
                    return true;

                // Half the async terminals are result operators wearing a different name, and
                // a result operator changes the document — FirstAsync asks for a page of one.
                // Reading them as plain consumers would print a document missing that argument.
                if (container == AsyncExtensions)
                    return AsyncTerminal(consumer, call, facts);

                return false;
            }

            default:
                return false;
        }
    }

    /// <summary>
    /// Folds an async terminal into the chain, since each is the async spelling of a
    /// <c>Queryable</c> operator and means exactly what that operator means.
    /// </summary>
    private static bool AsyncTerminal(IMethodSymbol consumer, InvocationExpressionSyntax call, ChainFacts facts)
    {
        var kind = consumer.Name switch
        {
            "ToArrayAsync" or "ToListAsync" or "AsAsyncEnumerable" => ResultKind.Sequence,
            "FirstAsync" => ResultKind.First,
            "FirstOrDefaultAsync" => ResultKind.FirstOrDefault,
            "SingleAsync" => ResultKind.Single,
            "SingleOrDefaultAsync" => ResultKind.SingleOrDefault,
            "LastAsync" => ResultKind.Last,
            "LastOrDefaultAsync" => ResultKind.LastOrDefault,
            "AnyAsync" => ResultKind.Any,
            "CountAsync" => ResultKind.Count,
            "LongCountAsync" => ResultKind.LongCount,
            _ => (ResultKind?)null
        };

        if (kind is not { } result)
            return false;

        if (result == ResultKind.Sequence)
            return true;

        // The predicate overloads take one before the cancellation token, and mean Where.
        foreach (var argument in call.ArgumentList.Arguments)
        {
            if (argument.Expression is not LambdaExpressionSyntax lambda)
                continue;

            facts.HasFilter = true;
            facts.Predicates.Add(lambda);
        }

        facts.Result = result;
        return true;
    }

    /// <summary>Reads the root field and whatever the configure delegate settles.</summary>
    private static bool ReadEntryArguments(
        InvocationExpressionSyntax entry,
        IMethodSymbol method,
        SemanticModel model,
        ChainFacts facts,
        CancellationToken token)
    {
        var arguments = Positional(entry, method);
        if (arguments is null)
            return false;

        for (int i = 0; i < method.Parameters.Length; i++)
        {
            var parameter = method.Parameters[i];
            var value = arguments[i];

            switch (parameter.Name)
            {
                case "rootField":
                    if (value is null || model.GetConstantValue(value, token).Value is not string root)
                        return false;

                    facts.RootField = root;
                    break;

                case "configure":
                    if (value is null || value is LiteralExpressionSyntax { Token.ValueText: "null" })
                        break;

                    if (value is not LambdaExpressionSyntax lambda || !ReadOptions(lambda, model, facts, token))
                        return false;

                    break;

                // The executor and the client are forwarded untouched.
                case "executor":
                case "client":
                    break;

                default:
                    return false;
            }
        }

        return facts.RootField.Length > 0;
    }

    /// <summary>
    /// Reads a configure delegate, which must be a lambda of plain assignments to settings this
    /// understands. Anything else — a loop, a call, a conditional — and the document cannot be
    /// known.
    /// </summary>
    private static bool ReadOptions(
        LambdaExpressionSyntax lambda,
        SemanticModel model,
        ChainFacts facts,
        CancellationToken token)
    {
        var assignments = new List<AssignmentExpressionSyntax>();

        switch (lambda.Body)
        {
            case AssignmentExpressionSyntax single:
                assignments.Add(single);
                break;

            case BlockSyntax block:
                foreach (var statement in block.Statements)
                {
                    if (statement is not ExpressionStatementSyntax
                        { Expression: AssignmentExpressionSyntax assignment })
                        return false;

                    assignments.Add(assignment);
                }

                break;

            default:
                return false;
        }

        foreach (var assignment in assignments)
        {
            if (assignment.Left is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: var setting })
                return false;

            var constant = model.GetConstantValue(assignment.Right, token);

            switch (setting)
            {
                case "RootField" when constant.Value is string root:
                    facts.RootField = root;
                    break;

                case "FilterInput" when constant.Value is string filter:
                    facts.FilterInput = filter;
                    break;

                case "SortInput" when constant.Value is string sort:
                    facts.SortInput = sort;
                    break;

                case "Paging" when constant.Value is int paging and >= 0 and <= 2:
                    facts.Paging = (Paging)paging;
                    break;

                // Neither changes a single character of the document: one picks the dialect the
                // filter's *value* is written in, the other picks the URL it is posted to. Both
                // are recorded all the same, for the callers that write more than the document.
                case "FilterProvider":
                    facts.SetsFilterProvider = true;
                    break;

                case "EndpointPath":
                    facts.SetsEndpoint = true;
                    break;

                default:
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Lays a call's arguments out by parameter position, resolving named arguments and leaving
    /// omitted optional ones null.
    /// </summary>
    private static ExpressionSyntax?[]? Positional(InvocationExpressionSyntax call, IMethodSymbol method)
    {
        var slots = new ExpressionSyntax?[method.Parameters.Length];
        var arguments = call.ArgumentList.Arguments;

        for (int i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            int index = i;

            if (argument.NameColon is { Name.Identifier.ValueText: var named })
            {
                index = -1;
                for (int p = 0; p < method.Parameters.Length; p++)
                {
                    if (method.Parameters[p].Name == named)
                        index = p;
                }
            }

            if (index < 0 || index >= slots.Length)
                return null;

            slots[index] = argument.Expression;
        }

        return slots;
    }

    /// <summary>
    /// The outermost containing type, because an extension member's own container is a
    /// compiler-generated nested type.
    /// </summary>
    public static INamedTypeSymbol? Outermost(INamedTypeSymbol? type)
    {
        while (type?.ContainingType is not null)
            type = type.ContainingType;

        return type;
    }
}
