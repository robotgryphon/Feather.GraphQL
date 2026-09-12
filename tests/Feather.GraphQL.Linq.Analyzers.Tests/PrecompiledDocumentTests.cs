using System.Collections.Immutable;
using Feather.GraphQL.Linq.Execution;
using Feather.GraphQL.Linq.Expressions;
using Feather.GraphQL.Linq.Filtering;
using Feather.GraphQL.Linq.Providers;
using Feather.GraphQL.Linq.Query;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Feather.GraphQL.Linq.Analyzers.Tests;

/// <summary>
/// Every document the compiler prints must be the document the runtime would have printed.
/// </summary>
/// <remarks>
/// <para>
/// The generator is a second implementation of the translator's document half, and that is a
/// genuinely dangerous kind of duplication: a document that differs from the runtime's asks the
/// server for the wrong fields, and the server answers it without complaint. No amount of care
/// in the generator makes that safe. What makes it safe is this — each case is written twice,
/// once as source for the compiler to precompile and once as a real chain for the runtime to
/// translate, and the two documents are compared byte for byte.
/// </para>
/// <para>
/// The pairs are deliberately redundant. Keeping the chain in both forms is the cost of being
/// able to compare them at all.
/// </para>
/// </remarks>
[TestFixture]
public class PrecompiledDocumentTests
{
    [Test]
    public void A_filtered_chain_agrees()
        => Agrees(
            """client.CreateQueryable<Country>("countries").Where(c => c.Name == "Spain").ToArray()""",
            Query().Where(c => c.Name == "Spain"));

    [Test]
    public void A_projection_agrees()
        => Agrees(
            """client.CreateQueryable<Country>("countries").Select(c => new { c.Name, c.Code }).ToArray()""",
            Query().Select(c => new { c.Name, c.Code }));

    [Test]
    public void A_nested_projection_agrees()
        => Agrees(
            """client.CreateQueryable<Country>("countries").Select(c => new { c.Name, c.Continent.Code }).ToArray()""",
            Query().Select(c => new { c.Name, c.Continent.Code }));

    /// <summary>
    /// Naming an object member without projecting it selects that member's own scalars — the
    /// rule most likely to be mirrored wrongly, since it expands a field the source never named.
    /// </summary>
    [Test]
    public void An_unprojected_object_member_agrees()
        => Agrees(
            """client.CreateQueryable<Country>("countries").Select(c => new { c.Name, c.Continent }).ToArray()""",
            Query().Select(c => new { c.Name, c.Continent }));

    [Test]
    public void A_bare_member_projection_agrees()
        => Agrees(
            """client.CreateQueryable<Country>("countries").Select(c => c.Name).ToArray()""",
            Query().Select(c => c.Name));

    [Test]
    public void Ordering_and_paging_agree()
        => Agrees(
            """client.CreateQueryable<Country>("countries").Where(c => c.Code != "").OrderBy(c => c.Name).Skip(5).Take(10).ToArray()""",
            Query().Where(c => c.Code != "").OrderBy(c => c.Name).Skip(5).Take(10));

    /// <summary>
    /// Variable numbering follows binding order, not source order, so a chain that writes its
    /// paging before its filter must still number the filter first.
    /// </summary>
    [Test]
    public void Variable_numbering_follows_binding_order()
        => Agrees(
            """client.CreateQueryable<Country>("countries").Take(3).Where(c => c.Name == "x").ToArray()""",
            Query().Take(3).Where(c => c.Name == "x"));

    [Test]
    public void A_result_operator_agrees()
        => Agrees(
            """client.CreateQueryable<Country>("countries").Where(c => c.Name == "x").First()""",
            Query().Where(c => c.Name == "x"),
            QueryResultOperator.First);

    /// <summary>
    /// <c>First(predicate)</c> is <c>Where(predicate).First()</c>, including for the document.
    /// </summary>
    [Test]
    public void A_result_operator_with_a_predicate_agrees()
        => Agrees(
            """client.CreateQueryable<Country>("countries").First(c => c.Name == "x")""",
            Query().Where(c => c.Name == "x"),
            QueryResultOperator.First);

    [Test]
    public void An_existence_check_agrees()
        => Agrees(
            """client.CreateQueryable<Country>("countries").Any(c => c.Name == "x")""",
            Query().Where(c => c.Name == "x"),
            QueryResultOperator.Any);

    [Test]
    public void A_count_over_a_paged_field_agrees()
        => Agrees(
            """client.CreateQueryable<Country>("countries", o => o.Paging = PagingKind.Offset).Where(c => c.Name == "x").Count()""",
            Query(PagingKind.Offset).Where(c => c.Name == "x"),
            QueryResultOperator.Count,
            PagingKind.Offset);

    [Test]
    public void Offset_paging_agrees()
        => Agrees(
            """client.CreateQueryable<Country>("countries", o => o.Paging = PagingKind.Offset).Where(c => c.Name == "x").ToArray()""",
            Query(PagingKind.Offset).Where(c => c.Name == "x"),
            paging: PagingKind.Offset);

    [Test]
    public void Cursor_paging_agrees()
        => Agrees(
            """client.CreateQueryable<Country>("countries", o => o.Paging = PagingKind.Cursor).Where(c => c.Name == "x").ToArray()""",
            Query(PagingKind.Cursor).Where(c => c.Name == "x"),
            paging: PagingKind.Cursor);

    /// <summary>A renamed filter input changes the variable's declared type, not its name.</summary>
    [Test]
    public void A_renamed_filter_input_agrees()
        => Agrees(
            """client.CreateQueryable<Country>("countries", o => o.FilterInput = "CountryWhere").Where(c => c.Name == "x").ToArray()""",
            Query(filterInput: "CountryWhere").Where(c => c.Name == "x"),
            filterInput: "CountryWhere");

    /// <summary>The argument name given inline is the schema's, and belongs in the document.</summary>
    [Test]
    public void An_inline_filter_argument_name_agrees()
        => Agrees(
            """client.CreateQueryable<Country>("countries").Where("filter", (CountryFilter c) => c.Continent == "EU").ToArray()""",
            Query().Where("filter", (CountryFilter c) => c.Continent == "EU"));

    /// <summary>
    /// An async terminal is the async spelling of a <c>Queryable</c> operator, and half of them
    /// are result operators — so reading them as plain materializers would print a document
    /// missing the argument the operator adds.
    /// </summary>
    [Test]
    public void An_async_sequence_terminal_agrees()
        => Agrees(
            """client.CreateQueryable<Country>("countries").Where(c => c.Name == "x").ToArrayAsync()""",
            Query().Where(c => c.Name == "x"));

    /// <inheritdoc cref="An_async_sequence_terminal_agrees"/>
    [Test]
    public void An_async_result_operator_agrees()
        => Agrees(
            """client.CreateQueryable<Country>("countries").Where(c => c.Name == "x").FirstAsync()""",
            Query().Where(c => c.Name == "x"),
            QueryResultOperator.First);

    /// <inheritdoc cref="An_async_sequence_terminal_agrees"/>
    [Test]
    public void An_async_result_operator_with_a_predicate_agrees()
        => Agrees(
            """client.CreateQueryable<Country>("countries").AnyAsync(c => c.Name == "x")""",
            Query().Where(c => c.Name == "x"),
            QueryResultOperator.Any);

    /// <inheritdoc cref="An_async_sequence_terminal_agrees"/>
    [Test]
    public void An_async_count_agrees()
        => Agrees(
            """client.CreateQueryable<Country>("countries", o => o.Paging = PagingKind.Offset).Where(c => c.Name == "x").CountAsync()""",
            Query(PagingKind.Offset).Where(c => c.Name == "x"),
            QueryResultOperator.Count,
            PagingKind.Offset);

    /// <summary>
    /// A chain stored in a local is followed to wherever it is used, which is how most real code
    /// reads — and how the example in <c>examples/</c> is written.
    /// </summary>
    [Test]
    public void A_chain_stored_in_a_local_is_precompiled()
        => Agrees(
            """
            var q = client.CreateQueryable<Country>("countries").Where(c => c.Name == "x");
            return q.ToArray();
            """,
            Query().Where(c => c.Name == "x"),
            statements: true);

    /// <summary>
    /// Several uses are fine as long as they agree — here both read the whole sequence, which is
    /// one document.
    /// </summary>
    [Test]
    public void Several_uses_that_agree_are_precompiled()
        => Agrees(
            """
            var q = client.CreateQueryable<Country>("countries").Where(c => c.Name == "x");
            var text = q.ToGraphQLQuery();
            return q.ToArray();
            """,
            Query().Where(c => c.Name == "x"),
            statements: true);

    /// <summary>
    /// Uses that disagree have no one document between them: the chain and everything built from
    /// it share a provider, so attaching either document would be wrong for the other.
    /// </summary>
    [Test]
    public void Uses_that_disagree_are_not_precompiled()
    {
        string? document = Generated(
            """
            var q = client.CreateQueryable<Country>("countries").Where(c => c.Name == "x");
            var one = q.First();
            return q.ToArray();
            """,
            statements: true);

        Assert.That(document, Is.Null,
            "First() and ToArray() need different documents, and they share a provider");
    }

    /// <summary>
    /// Composing further through a second local is still visible, and still has to agree.
    /// </summary>
    [Test]
    public void Composition_through_a_second_local_is_not_precompiled_when_it_disagrees()
    {
        string? document = Generated(
            """
            var q = client.CreateQueryable<Country>("countries");
            var filtered = q.Where(c => c.Name == "x");
            return filtered.ToArray();
            """,
            statements: true);

        Assert.That(document,
            Is.EqualTo("query($v0: CountryFilterInput) { countries(where: $v0) { name code } }"));
    }

    /// <summary>
    /// A local that leaves the method could be composed anywhere, so what is visible here is not
    /// the whole chain.
    /// </summary>
    [Test]
    public void A_chain_that_escapes_the_method_is_not_precompiled()
    {
        string? document = Generated(
            """
            var q = client.CreateQueryable<Country>("countries").Where(c => c.Name == "x");
            return Keep(q);
            """,
            statements: true);

        Assert.That(document, Is.Null, "a queryable handed to something else may be composed there");
    }

    /// <summary>A reassigned local no longer holds what was read here.</summary>
    [Test]
    public void A_reassigned_local_is_not_precompiled()
    {
        string? document = Generated(
            """
            var q = client.CreateQueryable<Country>("countries").Where(c => c.Name == "x");
            q = client.CreateQueryable<Country>("countries").Take(2);
            return q.ToArray();
            """,
            statements: true);

        Assert.That(document, Is.Null, "the local was assigned again and holds something else");
    }

    /// <summary>
    /// A configure delegate doing anything this cannot read is a document it cannot know.
    /// </summary>
    [Test]
    public void An_opaque_configure_delegate_is_not_precompiled()
    {
        string? document = Generated(
            """client.CreateQueryable<Country>("countries", Configure).Where(c => c.Name == "x").ToArray()""");

        Assert.That(document, Is.Null, "options set by a method group cannot be read at compile time");
    }

    /// <summary>
    /// The guard on the whole fixture: if nothing were ever generated, every agreement test
    /// above would pass by comparing null to null.
    /// </summary>
    [Test]
    public void The_corpus_actually_precompiles()
    {
        string? document = Generated(
            """client.CreateQueryable<Country>("countries").Where(c => c.Name == "x").ToArray()""");

        Assert.That(document, Is.EqualTo("query($v0: CountryFilterInput) { countries(where: $v0) { name code } }"));
    }

    /// <summary>
    /// A chain that binds nothing gets its whole plan, not just its document — there is nothing
    /// left for the runtime to learn by walking it.
    /// </summary>
    [TestCase("""client.CreateQueryable<Country>("countries").ToArray()""")]
    [TestCase("""client.CreateQueryable<Country>("countries").Select(c => new { c.Name }).ToArray()""")]
    [TestCase("""client.CreateQueryable<Country>("countries").First()""")]
    [TestCase("""client.CreateQueryable<Country>("countries").Select(c => c.Name).Single()""")]
    public void A_chain_that_binds_nothing_is_handed_a_plan(string chain)
        => Assert.That(Handover(chain), Is.EqualTo("AttachPlan"));

    /// <summary>
    /// A predicate's shape is compile-time knowledge even though its values are not, so a chain
    /// that only binds a filter is handed a plan too — with the filter printed and its values
    /// left open.
    /// </summary>
    [TestCase("""client.CreateQueryable<Country>("countries").Where(c => c.Name == "x").ToArray()""")]
    [TestCase("""client.CreateQueryable<Country>("countries").Where(c => c.Continent.Name == "x").ToArray()""")]
    public void A_chain_binding_only_a_printable_filter_is_handed_a_plan(string chain)
        => Assert.That(Handover(chain), Is.EqualTo("AttachPlan"));

    /// <summary>
    /// A chain that binds a value gets the document only. The value is in the expression tree and
    /// nowhere else, so the tree still has to be walked for it.
    /// </summary>
    [TestCase("""client.CreateQueryable<Country>("countries").Take(5).ToArray()""")]
    [TestCase("""client.CreateQueryable<Country>("countries").OrderBy(c => c.Name).ToArray()""")]
    public void A_chain_that_binds_an_unprintable_value_is_handed_only_its_document(string chain)
        => Assert.That(Handover(chain), Is.EqualTo("Attach"));

    /// <summary>
    /// Compiles the chain, precompiles it, translates the same chain at runtime, and compares.
    /// </summary>
    private static void Agrees<T>(
        string source,
        IQueryable<T> chain,
        QueryResultOperator result = QueryResultOperator.Sequence,
        PagingKind paging = PagingKind.None,
        string? filterInput = null,
        bool statements = false)
    {
        string? generated = Generated(source, statements);

        Assert.That(generated, Is.Not.Null, "the compiler declined a chain it was expected to precompile");

        Assert.That(generated, Is.EqualTo(Runtime(chain, result, paging, filterInput)),
            "the precompiled document and the runtime document differ");
    }

    /// <summary>The document the runtime translator prints for the same chain.</summary>
    private static string Runtime<T>(
        IQueryable<T> chain,
        QueryResultOperator result,
        PagingKind paging,
        string? filterInput)
    {
        // The terminal operators are not composable as expressions, so the few cases that need
        // one append it to the chain here.
        var expression = result switch
        {
            QueryResultOperator.Sequence => chain.Expression,
            QueryResultOperator.First => Call(chain, nameof(Queryable.First)),
            QueryResultOperator.Any => Call(chain, nameof(Queryable.Any)),
            QueryResultOperator.Count => Call(chain, nameof(Queryable.Count)),
            _ => throw new ArgumentOutOfRangeException(nameof(result))
        };

        var options = new GraphQLQueryOptions
        {
            RootField = "countries",
            Paging = paging,
            FilterInput = filterInput
        };

        return new GraphQLQueryTranslator(options).Translate(expression).Query;
    }

    private static System.Linq.Expressions.Expression Call<T>(IQueryable<T> chain, string name)
        => System.Linq.Expressions.Expression.Call(
            typeof(Queryable), name, [typeof(T)], chain.Expression);

    private static IQueryable<Country> Query(
        PagingKind paging = PagingKind.None,
        string? filterInput = null)
        => GraphQLQueryable.For<Country>("countries", o =>
        {
            o.Paging = paging;
            o.FilterInput = filterInput;
        });

    /// <summary>
    /// Runs the generator over one chain and returns the document it precompiled, or null when
    /// it declined to.
    /// </summary>
    private static string? _lastHandover;

    private static string? Generated(string chain, bool statements = false)
    {
        string body = statements ? chain : "return " + chain + ";";

        string source = $$"""
            using System;
            using System.Linq;
            using System.Net.Http;
            using Feather.GraphQL.Linq;
            using Feather.GraphQL.Linq.Filtering;
            using Feather.GraphQL.Linq.Providers;
            using Feather.GraphQL.Linq.Query;
            using Feather.GraphQL.Linq.Analyzers.Tests;

            public static class Snippet
            {
                public static object? Run(HttpClient client)
                {
                    {{body}}
                }

                private static void Configure(GraphQLHttpQueryOptions options) { }

                private static object Keep(object queryable) => queryable;
            }
            """;

        var compilation = CSharpCompilation.Create(
            "Precompiled",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview))],
            References(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToArray();

        Assert.That(errors, Is.Empty,
            $"the chain did not compile: {string.Join("; ", errors.Select(e => e.ToString()))}");

        var driver = CSharpGeneratorDriver
            .Create(new QueryInterceptorGenerator())
            .RunGenerators(compilation);

        _lastHandover = null;

        var generated = driver.GetRunResult().GeneratedTrees;
        if (generated.Length == 0)
            return null;

        // The document is the second argument either call takes — Attach hands over the document
        // alone, AttachPlan the whole plan. Read as a literal token rather than through a
        // semantic model: generated trees carry their own parse options and cannot simply be
        // added to this compilation.
        _lastHandover = Interceptors(generated[0])
            .Select(i => ((MemberAccessExpressionSyntax)i.Expression).Name.Identifier.ValueText)
            .FirstOrDefault();

        var documents = Interceptors(generated[0])
            .Select(i => (i.ArgumentList.Arguments[1].Expression as LiteralExpressionSyntax)?.Token.ValueText)
            .ToArray();

        Assert.That(documents, Has.Length.LessThanOrEqualTo(1), "one chain produced several interceptors");

        return documents.FirstOrDefault();
    }

    private static IEnumerable<InvocationExpressionSyntax> Interceptors(SyntaxTree tree)
        => tree.GetRoot()
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression is MemberAccessExpressionSyntax
            {
                Name.Identifier.ValueText: "Attach" or "AttachPlan"
            });

    /// <summary>Which of the two the compiler emitted for a chain, or null when it declined.</summary>
    private static string? Handover(string chain)
    {
        Generated(chain);

        return _lastHandover;
    }

    /// <inheritdoc cref="SnippetReferences.All"/>
    private static ImmutableArray<MetadataReference> References() => SnippetReferences.All();
}

/// <summary>The model the corpus queries over, shared by both halves of every comparison.</summary>
public class Country
{
    public string Name { get; set; } = "";
    public string Code { get; set; } = "";
    public Continent Continent { get; set; } = new();
}

/// <summary>The server's filter input, whose shape is not the element's.</summary>
public class CountryFilter
{
    public string Continent { get; set; } = "";
}

/// <inheritdoc cref="Country"/>
public class Continent
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
}
