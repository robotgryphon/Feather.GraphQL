using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Feather.GraphQL.Linq.Analyzers.Tests;

/// <summary>
/// What the compiler makes of a chain isolated to a <c>[GraphQLQuery]</c> method.
/// </summary>
/// <remarks>
/// <para>
/// The provider tests prove the compiled call sends the right bytes; these prove which chains
/// become one. The distinction matters because declining is not a failure — the method keeps its
/// body and the body keeps working — so the only way to tell a chain that was compiled from one
/// that was quietly left alone is to look at what came out.
/// </para>
/// <para>
/// Every declining case therefore asserts two things: that nothing was emitted, and that
/// <c>FGQL015</c> said why. An attribute that silently does nothing is the failure mode this
/// whole surface has to avoid.
/// </para>
/// </remarks>
[TestFixture]
public class CompiledQueryTests
{
    [Test]
    public void A_chain_bound_to_the_methods_parameters_is_compiled()
    {
        var run = Run("""
            [GraphQLQuery]
            private static Task<Country[]> ByName(HttpClient client, string name, CancellationToken token)
                => client.CreateQueryable<Country>("countries")
                    .Where(c => c.Name == name)
                    .ToArrayAsync(token);
            """);

        Assert.Multiple(() =>
        {
            Assert.That(run.Source,
                Does.Contain("query($v0: String) { countries(where: { name: { eq: $v0 } }) { name code } }"));

            // The value arrives as an argument, so the body is written from it directly — no
            // tree, nothing read back out of one, and no payload type between the two.
            Assert.That(run.Source, Does.Contain("body.Write(name)"));

            // Everything around the value was printed by the compiler and is copied, not built.
            Assert.That(run.Source, Does.Contain("""\"variables\":{\"v0\":"""));
            Assert.That(run.Diagnostics, Is.Empty);
        });
    }

    /// <summary>A chain that binds nothing is a body with nothing to write into it.</summary>
    [Test]
    public void A_chain_that_binds_nothing_posts_the_shared_empty_payload()
    {
        var run = Run("""
            [GraphQLQuery]
            private static Task<Country[]> All(HttpClient client, CancellationToken token)
                => client.CreateQueryable<Country>("countries").ToArrayAsync(token);
            """);

        Assert.Multiple(() =>
        {
            // No variables member, and so nothing at all left to do at run time: the whole body
            // is one constant the compiler already wrote.
            Assert.That(run.Source,
                Does.Contain("""body.WriteRaw("{\"query\":\"query { countries { name code } }\"}"u8);"""));

            Assert.That(run.Source, Does.Not.Contain("body.Write("));

            // The member, not the word — the comment above the body says "variables" too.
            Assert.That(run.Source, Does.Not.Contain("\\\"variables\\\""));
        });
    }

    /// <summary>
    /// A method called twice is compiled once, with both calls pointing at it.
    /// </summary>
    [Test]
    public void Every_call_to_a_compiled_method_is_replaced()
    {
        var run = Run("""
            [GraphQLQuery]
            private static Task<Country[]> ByName(HttpClient client, string name, CancellationToken token)
                => client.CreateQueryable<Country>("countries").Where(c => c.Name == name).ToArrayAsync(token);
            """,
            calls: """
                _ = ByName(client, "a", default);
                _ = ByName(client, "b", default);
                """);

        Assert.Multiple(() =>
        {
            Assert.That(Occurrences(run.Source, "InterceptsLocationAttribute(1,"), Is.EqualTo(2));
            Assert.That(Occurrences(run.Source, "public static async"), Is.EqualTo(1));
        });
    }

    /// <summary>Nothing to intercept, so nothing is written — and nothing is wrong either.</summary>
    [Test]
    public void A_compiled_method_nobody_calls_emits_nothing()
    {
        var run = Run("""
            [GraphQLQuery]
            private static Task<Country[]> All(HttpClient client, CancellationToken token)
                => client.CreateQueryable<Country>("countries").ToArrayAsync(token);
            """,
            calls: "");

        Assert.Multiple(() =>
        {
            Assert.That(run.Source, Is.Null);
            Assert.That(run.Diagnostics, Is.Empty);
        });
    }

    /// <summary>
    /// A value from outside the method's parameters is not knowable where the call is, which is
    /// the boundary the attribute exists to draw.
    /// </summary>
    [Test]
    public void A_value_the_method_does_not_take_declines()
        => Declines("""
            private static string _name = "x";

            [GraphQLQuery]
            private static Task<Country[]> ByField(HttpClient client, CancellationToken token)
                => client.CreateQueryable<Country>("countries").Where(c => c.Name == _name).ToArrayAsync(token);
            """);

    /// <summary>
    /// An ordering binds no value at all, so the whole sort argument is written out.
    /// </summary>
    /// <remarks>
    /// Which member and which direction are both in the syntax — unlike a filter, there is
    /// nothing left for the caller to supply, and the payload has no holes.
    /// </remarks>
    [Test]
    public void An_ordering_is_written_out_whole()
    {
        var run = Run("""
            [GraphQLQuery]
            private static Task<Country[]> Ordered(HttpClient client, CancellationToken token)
                => client.CreateQueryable<Country>("countries")
                    .OrderByDescending(c => c.Name)
                    .ToArrayAsync(token);
            """);

        Assert.Multiple(() =>
        {
            Assert.That(run.Source, Does.Contain("query($v0: [CountrySortInput!]) { countries(order: $v0) { name code } }"));
            // An ordering binds nothing, so the whole sort argument is part of the constant.
            Assert.That(run.Source, Does.Contain("""\"v0\":[{\"name\":\"DESC\"}]"""));
            Assert.That(run.Diagnostics, Is.Empty);
        });
    }

    /// <summary>A page size is a value like any other, and a parameter is where one can come from.</summary>
    [Test]
    public void A_page_is_bound_from_a_parameter()
    {
        var run = Run("""
            [GraphQLQuery]
            private static Task<Country[]> Paged(HttpClient client, int size, CancellationToken token)
                => client.CreateQueryable<Country>("countries").Take(size).ToArrayAsync(token);
            """);

        Assert.Multiple(() =>
        {
            Assert.That(run.Source, Does.Contain("{ countries(take: $v0) { name code } }"));
            Assert.That(run.Source, Does.Contain("body.Write(size)"));
            Assert.That(run.Diagnostics, Is.Empty);
        });
    }

    /// <summary>
    /// A result operator asks the server for a page and reduces what comes back.
    /// </summary>
    /// <remarks>
    /// The page size is the compiler's own decision — First means one — so it is a constant in
    /// the payload rather than a hole.
    /// </remarks>
    [Test]
    public void A_result_operator_asks_for_its_page_and_reduces_to_it()
    {
        var run = Run("""
            [GraphQLQuery]
            private static Task<Country> One(HttpClient client, string name, CancellationToken token)
                => client.CreateQueryable<Country>("countries").FirstAsync(c => c.Name == name, token);
            """);

        Assert.Multiple(() =>
        {
            Assert.That(run.Source, Does.Contain("take: $v1"));
            Assert.That(run.Source, Does.Contain("""\"v1\":1}}"""));
            Assert.That(run.Source, Does.Contain("The query returned no elements."));
            Assert.That(run.Diagnostics, Is.Empty);
        });
    }

    /// <summary>A count asks the connection, so it reads no rows.</summary>
    [Test]
    public void A_count_reads_the_connections_own_field()
    {
        var run = Run("""
            [GraphQLQuery]
            private static Task<int> HowMany(HttpClient client, CancellationToken token)
                => client.CreateQueryable<Country>("countries", o => o.Paging = PagingKind.Offset)
                    .CountAsync(token);
            """);

        Assert.Multiple(() =>
        {
            Assert.That(run.Source, Does.Contain("{ countries { totalCount } }"));

            // A count has no rows to model, so the generated reply is the envelope alone — and
            // the envelope already reads the count on its way past.
            Assert.That(run.Source, Does.Contain("ParseCount"));
            Assert.That(run.Source, Does.Contain("\"totalCount\"u8"));
            Assert.That(run.Source, Does.Not.Contain("_Row"));
            Assert.That(run.Diagnostics, Is.Empty);
        });
    }

    /// <summary>A paged field's rows sit inside a wrapper, which the paged reader unwraps.</summary>
    [Test]
    public void A_paged_field_is_read_through_the_wrapper()
    {
        var run = Run("""
            [GraphQLQuery]
            private static Task<Country[]> Paged(HttpClient client, CancellationToken token)
                => client.CreateQueryable<Country>("countries", o => o.Paging = PagingKind.Cursor)
                    .ToArrayAsync(token);
            """);

        Assert.Multiple(() =>
        {
            Assert.That(run.Source, Does.Contain("{ countries { nodes { name code } } }"));

            // The wrapper is stepped through by the generated reader rather than by a reader of
            // this library's, so what proves it is the emitted code naming the members.
            Assert.That(run.Source, Does.Contain("ReadGraphQLReplyAsync"));
            Assert.That(run.Source, Does.Contain("\"nodes\"u8"));

            // The document asked for nodes, so the reader reads nodes. The other wrapper is not a
            // fallback this reply could need — the same compilation printed both — and a reader
            // that looked for it would compare every property of the connection against a name
            // this server was never asked to send.
            Assert.That(run.Source, Does.Not.Contain("\"items\"u8"));
            Assert.That(run.Source, Does.Not.Contain("\"totalCount\"u8"));
        });
    }

    /// <summary>The other paging kind unwraps the other member, and only that one.</summary>
    [Test]
    public void An_offset_paged_field_is_read_through_its_own_wrapper()
    {
        var run = Run("""
            [GraphQLQuery]
            private static Task<Country[]> Paged(HttpClient client, CancellationToken token)
                => client.CreateQueryable<Country>("countries", o => o.Paging = PagingKind.Offset)
                    .ToArrayAsync(token);
            """);

        Assert.Multiple(() =>
        {
            Assert.That(run.Source, Does.Contain("{ countries { items { name code } } }"));
            Assert.That(run.Source, Does.Contain("\"items\"u8"));
            Assert.That(run.Source, Does.Not.Contain("\"nodes\"u8"));
            Assert.That(run.Source, Does.Not.Contain("\"totalCount\"u8"));
        });
    }

    /// <summary>
    /// An un-paged field has no wrapper at all, and its reader steps into none.
    /// </summary>
    /// <remarks>
    /// The rows are the field's own value. A reader that checked for a connection first would pay
    /// for the possibility on every reply of a query whose document can never produce one.
    /// </remarks>
    [Test]
    public void An_unpaged_field_is_read_without_a_wrapper()
    {
        var run = Run("""
            [GraphQLQuery]
            private static Task<Country[]> Plain(HttpClient client, CancellationToken token)
                => client.CreateQueryable<Country>("countries").ToArrayAsync(token);
            """);

        Assert.Multiple(() =>
        {
            Assert.That(run.Source, Does.Contain("{ countries { name code } }"));
            Assert.That(run.Source, Does.Not.Contain("\"nodes\"u8"));
            Assert.That(run.Source, Does.Not.Contain("\"items\"u8"));

            // Nothing selected a count, so the reply has no field to carry one.
            Assert.That(run.Source, Does.Not.Contain("\"totalCount\"u8"));
            Assert.That(run.Source, Does.Not.Contain("TotalCount"));
        });
    }

    /// <summary>
    /// A projection is re-emitted, not matched by name, so a rename shapes rather than declines.
    /// </summary>
    /// <remarks>
    /// The rows are read as the queried type and the projection runs over them, which is what
    /// makes this the author's own code rather than a mapping this generator has to get right.
    /// </remarks>
    [Test]
    public void A_projection_is_re_emitted_over_the_rows_it_read()
    {
        var run = Run("""
            [GraphQLQuery]
            private static Task<Renamed[]> Renaming(HttpClient client, CancellationToken token)
                => client.CreateQueryable<Country>("countries")
                    .Select(c => new Renamed { Title = c.Name })
                    .ToArrayAsync(token);
            """);

        Assert.Multiple(() =>
        {
            Assert.That(run.Source, Does.Contain("{ countries { name } }"));

            // The payload is mirrored, and the projection runs over the mirror — which its own
            // members are named for, so the author's lambda needs no rewriting to compile.
            Assert.That(run.Source, Does.Contain("file readonly struct Renaming_Row"));
            Assert.That(run.Source, Does.Contain("ReadGraphQLReplyAsync"));
            Assert.That(run.Source, Does.Contain("Title = c.Name"));
            Assert.That(run.Diagnostics, Is.Empty);
        });
    }

    /// <summary>A member reached through another, which no name could have matched.</summary>
    [Test]
    public void A_projection_reaching_through_a_member_shapes_it()
    {
        var run = Run("""
            [GraphQLQuery]
            private static Task<Renamed[]> Reaching(HttpClient client, CancellationToken token)
                => client.CreateQueryable<Country>("countries")
                    .Select(c => new Renamed { Title = c.Continent.Name })
                    .ToArrayAsync(token);
            """);

        Assert.Multiple(() =>
        {
            Assert.That(run.Source, Does.Contain("{ countries { continent { name } } }"));
            Assert.That(run.Source, Does.Contain("Title = c.Continent.Name"));
            Assert.That(run.Diagnostics, Is.Empty);
        });
    }

    /// <summary>
    /// A projection whose own Select reaches into a nested sequence.
    /// </summary>
    /// <remarks>
    /// Nothing about the inner chain is the compiler's business: it runs client-side, over rows
    /// that have already arrived, and what it decides is the shape of the answer. What the
    /// compiler has to get right is the field it names, two levels down — and that the lambda
    /// comes out the other side as the one that was written.
    /// </remarks>
    [Test]
    public void A_nested_Select_is_compiled()
    {
        var run = Run("""
            [GraphQLQuery]
            private static Task<string[][]> Nested(HttpClient client, CancellationToken token)
                => client.CreateQueryable<Country>("countries")
                    .Select(c => c.Continent.Countries.Select(n => n.Name).ToArray())
                    .ToArrayAsync(token);
            """);

        Assert.Multiple(() =>
        {
            Assert.That(run.Source, Does.Contain("{ countries { continent { countries { name } } } }"));
            Assert.That(run.Source, Does.Contain("c.Continent.Countries.Select(n => n.Name).ToArray()"));
            Assert.That(run.Diagnostics, Is.Empty);
        });
    }

    /// <summary>
    /// A method of the caller's own, which is where the tracing stops rather than where it gives
    /// up.
    /// </summary>
    /// <remarks>
    /// What the method does is not knowable here and does not have to be: it runs client-side
    /// over the values the projection hands it, and those are named at the call. So the fields
    /// are traced through it and the call itself is copied into the shaping, where it runs over
    /// the rows exactly as it would have.
    /// </remarks>
    [Test]
    public void A_method_of_the_callers_own_is_where_the_tracing_stops()
    {
        var run = Run("""
            [GraphQLQuery]
            private static Task<string[]> Listing(HttpClient client, CancellationToken token)
                => client.CreateQueryable<Country>("countries")
                    .Select(c => c.Continent.Countries.Select(n => n.Name).Joined())
                    .ToArrayAsync(token);

            internal static string Joined(this System.Collections.Generic.IEnumerable<string> values)
                => string.Join(", ", values);
            """);

        Assert.Multiple(() =>
        {
            // The inner Select said which field, so that is the only one asked for.
            Assert.That(run.Source, Does.Contain("{ countries { continent { countries { name } } } }"));
            Assert.That(run.Source,
                Does.Contain("c.Continent.Countries.Select(n => n.Name).Joined()"));

            Assert.That(run.Diagnostics, Is.Empty);
        });
    }

    /// <summary>
    /// A method handed the objects themselves, which may read any of them.
    /// </summary>
    /// <remarks>
    /// Which of the fields it reads is not visible, so all of them are asked for — the same
    /// answer naming a member without projecting gives. The alternative is a row whose other
    /// fields are silently empty, which the method has no way to tell from data.
    /// </remarks>
    [Test]
    public void A_method_handed_a_member_gets_that_members_own_fields()
    {
        var run = Run("""
            [GraphQLQuery]
            private static Task<int[]> Counting(HttpClient client, CancellationToken token)
                => client.CreateQueryable<Country>("countries")
                    .Select(c => c.Continent.Countries.Weigh())
                    .ToArrayAsync(token);

            internal static int Weigh(this System.Collections.Generic.IEnumerable<Country> countries)
                => countries.Count();
            """);

        Assert.Multiple(() =>
        {
            Assert.That(run.Source,
                Does.Contain("{ countries { continent { countries { name code } } } }"));

            Assert.That(run.Diagnostics, Is.Empty);
        });
    }

    /// <summary>
    /// An inner chain that names no field of its own still leaves the member it ran over needing
    /// a selection set.
    /// </summary>
    /// <remarks>
    /// Which is asked of that member rather than of what the chain turned it into. The two are
    /// the same type whenever the chain only filters or orders, and different the moment it
    /// projects — at which point the fields of the projected type are fields no country has, and
    /// a document naming them is one the server rejects.
    /// </remarks>
    [Test]
    public void A_nested_chain_naming_no_field_expands_the_member_it_read()
    {
        var run = Run("""
            [GraphQLQuery]
            private static Task<Renamed[][]> Blank(HttpClient client, string title, CancellationToken token)
                => client.CreateQueryable<Country>("countries")
                    .Select(c => c.Continent.Countries.Select(n => new Renamed { Title = title }).ToArray())
                    .ToArrayAsync(token);
            """);

        Assert.Multiple(() =>
        {
            Assert.That(run.Source, Does.Contain("{ countries { continent { countries { name code } } } }"));

            // An array of arrays, whose size goes in the brackets the type's spelling starts with.
            Assert.That(run.Source, Does.Contain("new global::Renamed[source.Length][]"));
            Assert.That(run.Diagnostics, Is.Empty);
        });
    }

    /// <summary>A predicate inside the projection names fields too, and they are traced.</summary>
    [Test]
    public void A_predicate_inside_the_projection_is_traced()
    {
        var run = Run("""
            [GraphQLQuery]
            private static Task<string[][]> Filtered(HttpClient client, string code, CancellationToken token)
                => client.CreateQueryable<Country>("countries")
                    .Select(c => c.Continent.Countries
                        .Where(n => n.Code == code)
                        .Select(n => n.Name)
                        .ToArray())
                    .ToArrayAsync(token);
            """);

        Assert.Multiple(() =>
        {
            Assert.That(run.Source,
                Does.Contain("{ countries { continent { countries { code name } } } }"));

            // The value the predicate compares against is the method's own parameter, which the
            // replacement has in scope: the filter runs client-side, so nothing is bound for it.
            Assert.That(run.Source, Does.Contain("n.Code == code"));
            Assert.That(run.Diagnostics, Is.Empty);
        });
    }

    /// <summary>
    /// The row a projection runs over is the payload's, not the queried type.
    /// </summary>
    /// <remarks>
    /// Its fields are the element's and are read through as such, but it is not that type, so
    /// handing it to something that wants the element cannot work. Declined here rather than left
    /// to the generated file, where it would be the compiler's error about generated code.
    /// </remarks>
    [Test]
    public void A_projection_passing_the_row_itself_declines()
        => Declines("""
            [GraphQLQuery]
            private static Task<string[]> Describing(HttpClient client, CancellationToken token)
                => client.CreateQueryable<Country>("countries")
                    .Select(c => c.Describe())
                    .ToArrayAsync(token);

            internal static string Describe(this Country country) => country.Name;
            """,
            says: "'c' is used here as a 'Country' rather than read through",
            at: "c");

    /// <summary>
    /// A type the projection names is written out in full, since the generated file does not
    /// share the scope the projection was written in.
    /// </summary>
    [Test]
    public void A_type_a_projection_names_is_written_out_in_full()
    {
        var run = Run("""
            [GraphQLQuery]
            private static Task<Renamed[]> Naming(HttpClient client, CancellationToken token)
                => client.CreateQueryable<Country>("countries")
                    .Select(c => new Renamed { Title = c.Name })
                    .ToArrayAsync(token);
            """);

        Assert.That(run.Source, Does.Contain("new global::Renamed"));
    }

    /// <summary>
    /// A value the projection reads from the declaring type is not available where the call is.
    /// </summary>
    /// <remarks>
    /// The same boundary the attribute draws for everything else: what the replacement can see is
    /// what the caller handed it. A local cannot appear here at all — the body is one expression,
    /// so there is nowhere to declare one — which leaves the declaring type's own state as the
    /// way to reach outside it.
    /// </remarks>
    [Test]
    public void A_projection_reading_the_declaring_types_state_declines()
        => Declines("""
            private static readonly string _suffix = "!";

            [GraphQLQuery]
            private static Task<Renamed[]> Capturing(HttpClient client, CancellationToken token)
                => client.CreateQueryable<Country>("countries")
                    .Select(c => new Renamed { Title = _suffix })
                    .ToArrayAsync(token);
            """,
            says: "'_suffix' is private to 'Snippet'",
            at: "_suffix");

    /// <summary>
    /// A helper the generated file can see is written with the type that declares it.
    /// </summary>
    /// <remarks>
    /// The same reason every type name is written out: the replacement lives in a file of its
    /// own, where a name that resolved because the declaring type was around it resolves as
    /// nothing at all.
    /// </remarks>
    [Test]
    public void A_helper_named_on_its_own_is_written_with_its_type()
    {
        var run = Run("""
            [GraphQLQuery]
            private static Task<Renamed[]> Shouting(HttpClient client, CancellationToken token)
                => client.CreateQueryable<Country>("countries")
                    .Select(c => new Renamed { Title = Shout(c.Name) })
                    .ToArrayAsync(token);

            internal static string Shout(string value) => value.ToUpperInvariant();
            """);

        Assert.Multiple(() =>
        {
            Assert.That(run.Source, Does.Contain("Title = global::Snippet.Shout(c.Name)"));
            Assert.That(run.Diagnostics, Is.Empty);
        });
    }

    /// <summary>A private helper is not reachable from the file the replacement lives in.</summary>
    [Test]
    public void A_projection_calling_a_private_helper_declines()
        => Declines("""
            private static string Shout(string value) => value;

            [GraphQLQuery]
            private static Task<Renamed[]> Shouting(HttpClient client, CancellationToken token)
                => client.CreateQueryable<Country>("countries")
                    .Select(c => new Renamed { Title = Shout(c.Name) })
                    .ToArrayAsync(token);
            """,
            says: "'Shout' is private to 'Snippet'",
            at: "Shout(c.Name)");

    /// <summary>
    /// One attribute, two kinds of query, told apart by the method's own shape.
    /// </summary>
    /// <remarks>
    /// A document on a partial method is implemented from the document; a body is compiled from
    /// the chain. Nothing about the attribute says which — the method does, which is what makes
    /// one attribute enough. Both generators run here, because the interesting claim is that each
    /// takes the shape that is its own and leaves the other alone.
    /// </remarks>
    [Test]
    public void One_attribute_sends_each_shape_to_its_own_generator()
    {
        string source = """
            using System.Linq;
            using System.Net.Http;
            using System.Threading;
            using System.Threading.Tasks;
            using Feather.GraphQL;
            using Feather.GraphQL.Linq.Providers;
            using Feather.GraphQL.Linq.Query;
            using Feather.GraphQL.Linq.Analyzers.Tests;

            public static partial class Snippet
            {
                [GraphQLQuery("query { countries { name } }")]
                public static partial Task<Country[]> Written(HttpClient client, CancellationToken token);

                [GraphQLQuery]
                public static Task<Country[]> Composed(HttpClient client, CancellationToken token)
                    => client.CreateQueryable<Country>("countries").Where(c => c.Name == "x").ToArrayAsync(token);

                public static void Run(HttpClient client)
                {
                    _ = Written(client, default);
                    _ = Composed(client, default);
                }
            }
            """;

        var compilation = CSharpCompilation.Create(
            "Merged",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview))],
            SnippetReferences.All(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var run = CSharpGeneratorDriver
            .Create(new GraphQLQueryMethodGenerator(), new CompiledQueryGenerator())
            .RunGenerators(compilation)
            .GetRunResult();

        string declared = run.Results
            .Single(x => x.Generator.GetGeneratorType() == typeof(GraphQLQueryMethodGenerator))
            .GeneratedSources.Single().SourceText.ToString();

        string compiled = run.Results
            .Single(x => x.Generator.GetGeneratorType() == typeof(CompiledQueryGenerator))
            .GeneratedSources.Single().SourceText.ToString();

        Assert.Multiple(() =>
        {
            // The document became the partial method's body, and nothing intercepted its calls.
            Assert.That(declared, Does.Contain("partial global::System.Threading.Tasks.Task"));
            Assert.That(declared, Does.Contain("Written("));

            // Named for the method rather than searched for as a word: the compiled file writes
            // `body.Written`, which is not this method and never was.
            Assert.That(compiled, Does.Not.Contain("Written("));

            // The chain became an interceptor, and the document generator left it alone.
            Assert.That(compiled, Does.Contain("InterceptsLocationAttribute"));
            Assert.That(declared, Does.Not.Contain("Composed"));

            Assert.That(run.Diagnostics, Is.Empty);
        });
    }

    /// <summary>An endpoint the options name is not the one the compiled call would post to.</summary>
    [Test]
    public void An_endpoint_in_the_options_declines()
        => Declines("""
            [GraphQLQuery]
            private static Task<Country[]> Elsewhere(HttpClient client, CancellationToken token)
                => client.CreateQueryable<Country>("countries", o => o.EndpointPath = "api/graphql")
                    .ToArrayAsync(token);
            """);

    /// <summary>
    /// A body that also does something would have that something skipped, since the call this
    /// replaces is the call that would have run it.
    /// </summary>
    [Test]
    public void A_body_that_is_more_than_the_chain_declines()
        => Declines("""
            [GraphQLQuery]
            private static Task<Country[]> Logged(HttpClient client, CancellationToken token)
            {
                Console.WriteLine("about to query");
                return client.CreateQueryable<Country>("countries").ToArrayAsync(token);
            }
            """);

    /// <summary>
    /// A body that awaits the chain and goes on to do something with the rows.
    /// </summary>
    /// <remarks>
    /// The other half of a projection ending in the caller's own code, one level out: what is
    /// written around the await runs client-side over the answer, so the replacement runs it in
    /// the same place — over the rows it awaited for, rather than not at all.
    /// </remarks>
    [Test]
    public void A_body_that_goes_on_after_the_await_is_compiled()
    {
        var run = Run("""
            [GraphQLQuery]
            private static async Task<string> Listed(HttpClient client, CancellationToken token)
                => (await client.CreateQueryable<Country>("countries")
                    .Select(c => c.Name)
                    .ToArrayAsync(token)).Joined();

            internal static string Joined(this System.Collections.Generic.IEnumerable<string> values)
                => string.Join(", ", values);
            """);

        Assert.Multiple(() =>
        {
            // The chain is still the whole of the document: what follows the await asks the
            // server for nothing.
            Assert.That(run.Source, Does.Contain("{ countries { name } }"));
            Assert.That(run.Source, Does.Contain("return (rows).Joined();"));
            Assert.That(run.Diagnostics, Is.Empty);
        });
    }

    /// <summary>The rows as an argument, which is the same substitution somewhere else.</summary>
    [Test]
    public void A_body_that_awaits_the_chain_into_an_argument_is_compiled()
    {
        var run = Run("""
            [GraphQLQuery]
            private static async Task<string> Joined(HttpClient client, CancellationToken token)
                => string.Join(", ", await client.CreateQueryable<Country>("countries")
                    .Select(c => c.Name)
                    .ToArrayAsync(token));
            """);

        Assert.Multiple(() =>
        {
            Assert.That(run.Source, Does.Contain("{ countries { name } }"));
            Assert.That(run.Source, Does.Contain("return string.Join(\", \", rows);"));
            Assert.That(run.Diagnostics, Is.Empty);
        });
    }

    /// <summary>
    /// What the terminal reduces to is what the body awaited, and what it wrote around it runs
    /// over that.
    /// </summary>
    /// <remarks>
    /// Over it and not before it: the document is the chain's, so a body reading a field the
    /// chain never asked for reads it off the row as the row arrived. Which is what the same two
    /// lines mean anywhere else — the rows are the queried type with its own scalars filled in,
    /// and nothing deeper.
    /// </remarks>
    [Test]
    public void A_body_that_reaches_through_a_result_operator_is_compiled()
    {
        var run = Run("""
            [GraphQLQuery]
            private static async Task<string> Coded(HttpClient client, string name, CancellationToken token)
                => (await client.CreateQueryable<Country>("countries")
                    .Where(c => c.Name == name)
                    .FirstAsync(token)).Code;
            """);

        Assert.Multiple(() =>
        {
            Assert.That(run.Source, Does.Contain(
                "{ countries(where: { name: { eq: $v0 } }, take: $v1) { name code } }"));

            // The reduction the terminal asked for, substituted where the await was.
            Assert.That(run.Source, Does.Contain("rows[0]"));
            Assert.That(run.Source, Does.Contain(").Code;"));
            Assert.That(run.Diagnostics, Is.Empty);
        });
    }

    /// <summary>
    /// A body that does something around the chain without awaiting it.
    /// </summary>
    /// <remarks>
    /// The rule the one-expression body only looked like it enforced: what the chain stands for
    /// ends at the call this recognised, and a wrapper around that is code the replacement would
    /// drop. Reproducing it is only possible for what the body awaited — everything else is
    /// something happening to the query rather than to its answer.
    /// </remarks>
    [Test]
    public void A_body_that_wraps_the_chain_without_awaiting_it_declines()
        => Declines("""
            [GraphQLQuery]
            private static Task<Country[]> Wrapped(HttpClient client, CancellationToken token)
                => Task.FromResult(client.CreateQueryable<Country>("countries")
                    .Select(c => new Country { Name = c.Name })
                    .ToArrayAsync(token).Result);
            """);

    /// <summary>
    /// What the body does after the await is copied, so it is read by the same rules the
    /// projection is.
    /// </summary>
    [Test]
    public void A_body_reading_the_declaring_types_state_after_the_await_declines()
        => Declines("""
            private static readonly string _suffix = "!";

            [GraphQLQuery]
            private static async Task<string> Suffixed(HttpClient client, CancellationToken token)
                => (await client.CreateQueryable<Country>("countries")
                    .Select(c => c.Name)
                    .ToArrayAsync(token)).Length + _suffix;
            """);

    /// <summary>The client has to be the caller's, because the caller is where this runs.</summary>
    [Test]
    public void A_client_the_method_does_not_take_declines()
        => Declines("""
            private static readonly HttpClient _client = new();

            [GraphQLQuery]
            private static Task<Country[]> Fixed(HttpClient client, CancellationToken token)
                => _client.CreateQueryable<Country>("countries").ToArrayAsync(token);
            """);

    // ---- where the filter goes ----------------------------------------------------------------

    /// <summary>
    /// A nested path is written out as the objects it nests through.
    /// </summary>
    /// <remarks>
    /// The point of writing the filter into the document rather than passing it whole: a server
    /// handed <c>where: $v0</c> sees an input object it knows nothing about until it coerces the
    /// variable, and a cost or complexity analyser that runs before that has to assume the worst
    /// of it. Written out, the predicate is in the document where such a rule can read it.
    /// </remarks>
    [Test]
    public void A_nested_filter_path_is_written_into_the_document()
    {
        var run = Run("""
            [GraphQLQuery]
            private static Task<Country[]> InRegion(HttpClient client, string code, CancellationToken token)
                => client.CreateQueryable<Country>("countries")
                    .Where(c => c.Continent.Code == code)
                    .ToArrayAsync(token);
            """);

        Assert.Multiple(() =>
        {
            Assert.That(run.Source, Does.Contain(
                "query($v0: String) "
                + "{ countries(where: { continent: { code: { eq: $v0 } } }) { name code } }"));

            // Nothing is left around the value: the shape that used to be the variable's is in
            // the document, so what the body writes is the value and nothing else.
            Assert.That(run.Source, Does.Contain("""\"variables\":{\"v0\":"""));
            Assert.That(run.Diagnostics, Is.Empty);
        });
    }

    /// <summary>Each comparison gets a variable, named and typed for the value it carries.</summary>
    [Test]
    public void Every_comparison_declares_a_variable_of_its_own()
    {
        var run = Run("""
            [GraphQLQuery]
            private static Task<Country[]> Both(HttpClient client, string name, string code, CancellationToken token)
                => client.CreateQueryable<Country>("countries")
                    .Where(c => c.Name == name && c.Code == code)
                    .ToArrayAsync(token);
            """);

        Assert.Multiple(() =>
        {
            Assert.That(run.Source, Does.Contain(
                "query($v0: String, $v1: String) "
                + "{ countries(where: { name: { eq: $v0 }, code: { eq: $v1 } }) { name code } }"));

            // Declared in the order the predicate names them, which is the order they are written.
            Assert.That(run.Source, Does.Contain("body.Write(name)"));
            Assert.That(run.Source, Does.Contain("body.Write(code)"));
            Assert.That(run.Diagnostics, Is.Empty);
        });
    }

    /// <summary>
    /// The opt-out puts the whole filter back in one variable of the filter input type.
    /// </summary>
    /// <remarks>
    /// It exists because writing a variable into the document means declaring its type, and what
    /// the schema calls a comparison's value is inferred rather than known. A schema that types
    /// the field some other way — <c>ID</c> against a <c>string</c> here — rejects the query, and
    /// the form below never has to name a scalar at all.
    /// </remarks>
    [Test]
    public void The_opt_out_passes_the_whole_filter_as_one_variable()
    {
        var run = Run("""
            [GraphQLQuery]
            private static Task<Country[]> ByName(HttpClient client, string name, CancellationToken token)
                => client.CreateQueryable<Country>("countries", o => o.InlineFilter = false)
                    .Where(c => c.Name == name)
                    .ToArrayAsync(token);
            """);

        Assert.Multiple(() =>
        {
            Assert.That(run.Source,
                Does.Contain("query($v0: CountryFilterInput) { countries(where: $v0) { name code } }"));

            // And the shape comes back with it, since the document no longer carries it.
            Assert.That(run.Source, Does.Contain("""\"variables\":{\"v0\":{\"name\":{\"eq\":"""));
            Assert.That(run.Diagnostics, Is.Empty);
        });
    }

    /// <summary>
    /// A value whose schema name is not certain keeps the whole filter in one variable.
    /// </summary>
    /// <remarks>
    /// Not an error and not a decline: the query still compiles and still sends the same filter.
    /// <c>char</c> stands in for the whole class of types the schema might call any of several
    /// things, which <see cref="GraphQLTypeFacts.ScalarName"/> answers null for rather than guess.
    /// </remarks>
    [Test]
    public void A_value_with_no_certain_schema_name_keeps_the_whole_filter_in_one_variable()
    {
        var run = Run("""
            [GraphQLQuery]
            private static Task<Country[]> ByInitial(HttpClient client, char initial, CancellationToken token)
                => client.CreateQueryable<Country>("countries")
                    .Where("where", (InitialFilter f) => f.Initial == initial)
                    .Select(c => new Country { Name = c.Name, Code = c.Code })
                    .ToArrayAsync(token);
            """);

        Assert.Multiple(() =>
        {
            Assert.That(run.Source,
                Does.Contain("query($v0: CountryFilterInput) { countries(where: $v0) { name code } }"));

            Assert.That(run.Diagnostics, Is.Empty);
        });
    }

    // ---- flattening and grouping ------------------------------------------------------------

    /// <summary>
    /// A chain's own <c>SelectMany</c> is not a query, and the refusal names it.
    /// </summary>
    /// <remarks>
    /// A document asks the server for rows of one field. Flattening them is a thing to do to rows
    /// — the server has no say in it and no syntax for it — so the operator has no translation
    /// rather than a missing one. What used to be said about it was that the chain "could not be
    /// read as one query ending in this method", which is true of a dozen other faults too.
    /// </remarks>
    [Test]
    public void A_chains_own_SelectMany_is_refused_by_name()
        => Declines("""
            [GraphQLQuery]
            private static Task<string[]> Flattened(HttpClient client, CancellationToken token)
                => client.CreateQueryable<Country>("countries")
                    .SelectMany(c => c.Continent.Countries)
                    .Select(n => n.Name)
                    .ToArrayAsync(token);
            """,
            says: "'SelectMany' is not one of the operators a query can be compiled from",
            at: "SelectMany");

    /// <summary>The same of <c>GroupBy</c>, and of everything else <c>Queryable</c> offers.</summary>
    [Test]
    public void A_chains_own_GroupBy_is_refused_by_name()
        => Declines("""
            [GraphQLQuery]
            private static Task<string[]> Grouped(HttpClient client, CancellationToken token)
                => client.CreateQueryable<Country>("countries")
                    .GroupBy(c => c.Code)
                    .Select(g => g.Key)
                    .ToArrayAsync(token);
            """,
            says: "'GroupBy' is not one of the operators a query can be compiled from",
            at: "GroupBy");

    /// <summary>
    /// The names of the countries in a continent, which is one path down and no more.
    /// </summary>
    /// <remarks>
    /// Pinned because it is the shape everything else here is measured against. A document that
    /// walks back up to a field it already had — <c>countries { continent { countries { name }
    /// } }</c> — is a second trip through the resolvers for rows the first trip fetched, and the
    /// only thing that should ever produce one is a projection that asked for it in so many words.
    /// </remarks>
    [Test]
    public void The_names_of_the_countries_in_a_continent_are_one_path_down()
    {
        var run = Run("""
            [GraphQLQuery]
            private static Task<string[][]> Names(HttpClient client, CancellationToken token)
                => client.CreateQueryable<Continent>("continents")
                    .Select(c => c.Countries.Select(n => n.Name).ToArray())
                    .ToArrayAsync(token);
            """);

        Assert.Multiple(() =>
        {
            Assert.That(run.Source, Does.Contain("{ continents { countries { name } } }"));
            Assert.That(run.Diagnostics, Is.Empty);
        });
    }

    /// <summary>
    /// A flatten one level down, which is the same path with one more step in it.
    /// </summary>
    /// <remarks>
    /// What <c>SelectMany</c> is for: the rows are two collections deep and the projection wants
    /// them as one. The document is still one path down — nothing asked for twice, and nothing
    /// asked for above the flattened rows, since nothing named a field there.
    /// </remarks>
    [Test]
    public void A_SelectMany_flattens_one_level_down()
    {
        var run = Run("""
            [GraphQLQuery]
            private static Task<string[][]> Names(HttpClient client, CancellationToken token)
                => client.CreateQueryable<Hemisphere>("hemispheres")
                    .Select(h => h.Continents.SelectMany(c => c.Countries).Select(n => n.Name).ToArray())
                    .ToArrayAsync(token);
            """);

        Assert.Multiple(() =>
        {
            Assert.That(run.Source, Does.Contain("{ hemispheres { continents { countries { name } } } }"));
            Assert.That(run.Diagnostics, Is.Empty);
        });
    }

    /// <summary>
    /// Inside the projection it is ordinary client-side code, and the fields it names are asked
    /// for where they land.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The related listings of the related listings: <c>SelectMany</c> runs over rows that have
    /// already arrived, so what the compiler has to get right is which fields it reads and where
    /// they sit — <c>name</c> belongs to the listings the selector reached, two deep, and not to
    /// the ones it ran over.
    /// </para>
    /// <para>
    /// Written over a member holding its own type on purpose, because that is the case that used
    /// to go wrong quietly: where the two levels are different types the mistake is a refusal,
    /// and where they are the same it was a document asking for a field at the wrong depth and
    /// every scalar of the right one besides.
    /// </para>
    /// </remarks>
    [Test]
    public void A_SelectMany_inside_the_projection_asks_where_it_lands()
    {
        var run = Run("""
            [GraphQLQuery]
            private static Task<string[][]> Related(HttpClient client, CancellationToken token)
                => client.CreateQueryable<Listing>("listings")
                    .Select(l => l.Related.SelectMany(r => r.Related).Select(x => x.Name).ToArray())
                    .ToArrayAsync(token);
            """);

        Assert.Multiple(() =>
        {
            // One `name`, on the listings the selector reached rather than the ones it ran over.
            Assert.That(run.Source, Does.Contain("{ listings { related { related { name } } } }"));
            Assert.That(run.Diagnostics, Is.Empty);
        });
    }

    /// <summary>A flattened member that is a list of scalars needs no selection set at all.</summary>
    [Test]
    public void A_SelectMany_over_scalars_asks_for_the_member_alone()
    {
        var run = Run("""
            [GraphQLQuery]
            private static Task<string[][]> Tagged(HttpClient client, CancellationToken token)
                => client.CreateQueryable<Listing>("listings")
                    .Select(l => l.Related.SelectMany(r => r.Tags).ToArray())
                    .ToArrayAsync(token);
            """);

        Assert.Multiple(() =>
        {
            Assert.That(run.Source, Does.Contain("{ listings { related { tags } } }"));
            Assert.That(run.Diagnostics, Is.Empty);
        });
    }

    /// <summary>
    /// A grouping's <c>Key</c> is not a field, and asking for one is not a document any server
    /// answers.
    /// </summary>
    /// <remarks>
    /// <c>Key</c> is a property, which is all this used to ask — so <c>g.Key</c> was written into
    /// the document beside the fields the key selector had already asked for, and the reply then
    /// could not be modelled because <c>Country</c> has no such member. The value is the key
    /// selector's, computed where the rows are, and the fields it reads are what the document
    /// needs.
    /// </remarks>
    [Test]
    public void A_groupings_key_is_read_where_the_rows_are()
    {
        var run = Run("""
            [GraphQLQuery]
            private static Task<string[][]> Grouped(HttpClient client, CancellationToken token)
                => client.CreateQueryable<Continent>("continents")
                    .Select(c => c.Countries.GroupBy(n => n.Code).Select(g => g.Key).ToArray())
                    .ToArrayAsync(token);
            """);

        Assert.Multiple(() =>
        {
            Assert.That(run.Source, Does.Contain("{ continents { countries { code } } }"));
            Assert.That(run.Source, Does.Not.Contain("key"));
            Assert.That(run.Diagnostics, Is.Empty);
        });
    }

    /// <summary>What a grouping holds is still rows, and fields read off those are asked for.</summary>
    [Test]
    public void A_field_read_through_a_grouping_is_asked_for()
    {
        var run = Run("""
            [GraphQLQuery]
            private static Task<string[][]> Grouped(HttpClient client, CancellationToken token)
                => client.CreateQueryable<Continent>("continents")
                    .Select(c => c.Countries.GroupBy(n => n.Code).Select(g => g.First().Name).ToArray())
                    .ToArrayAsync(token);
            """);

        Assert.Multiple(() =>
        {
            Assert.That(run.Source, Does.Contain("{ continents { countries { code name } } }"));
            Assert.That(run.Diagnostics, Is.Empty);
        });
    }

    /// <summary>
    /// A lambda that takes more than one row binds all of them, by type.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The regression this is here for was silent, which is the only kind worth writing a test
    /// this specific for. A two-parameter lambda had no parameter this could name, so both of its
    /// parameters were names in no scope — and a name in no scope had just been taught to mean "a
    /// value of the caller's own, which asks the server for nothing". <c>a.Name</c> asked for
    /// nothing, the document went out without <c>name</c>, the generated code compiled because
    /// the row was the caller's own type, and <c>Name</c> came back null.
    /// </para>
    /// <para>
    /// Deciding each parameter by its type rather than by its position is what makes one rule
    /// serve <c>Zip</c>, <c>SelectMany</c>'s result selector and the indexed overloads alike.
    /// </para>
    /// </remarks>
    [Test]
    public void A_lambda_taking_two_rows_binds_both()
    {
        var run = Run("""
            [GraphQLQuery]
            private static Task<string[][]> Paired(HttpClient client, CancellationToken token)
                => client.CreateQueryable<Continent>("continents")
                    .Select(c => c.Countries.Where(n => n.Code != "")
                        .Zip(c.Countries, (a, b) => a.Name + b.Code)
                        .ToArray())
                    .ToArrayAsync(token);
            """);

        Assert.Multiple(() =>
        {
            Assert.That(run.Source, Does.Contain("{ continents { countries { code name } } }"));
            Assert.That(run.Diagnostics, Is.Empty);
        });
    }

    /// <summary>Flattening the rows after the await, which is where a chain's own would have to go.</summary>
    [Test]
    public void The_rows_may_be_flattened_after_the_await()
    {
        var run = Run("""
            [GraphQLQuery]
            private static async Task<string[]> Names(HttpClient client, CancellationToken token)
                => (await client.CreateQueryable<Continent>("continents")
                    .Select(c => c.Countries.Select(n => n.Name).ToArray())
                    .ToArrayAsync(token)).SelectMany(x => x).ToArray();
            """);

        Assert.Multiple(() =>
        {
            Assert.That(run.Source, Does.Contain("{ continents { countries { name } } }"));
            Assert.That(run.Source, Does.Contain("return (rows).SelectMany(x => x).ToArray();"));
            Assert.That(run.Diagnostics, Is.Empty);
        });
    }

    // ---- a value the model takes whole ------------------------------------------------------

    /// <summary>
    /// A type whose model declared a converter is one field, not a selection set over its
    /// properties.
    /// </summary>
    /// <remarks>
    /// The half that reads the reply already knew this: a member the model converts is read
    /// through the converter whatever the query asked for beneath it. The half that writes the
    /// document did not, so it descended into the properties and asked for
    /// <c>issued { issuer serial }</c> — a selection set over a field the server sends as one
    /// value, which is the query being refused rather than anything subtle going wrong later.
    /// A converter is the model saying this value is not shaped by its properties, and that is as
    /// true of the document as it is of the reader.
    /// </remarks>
    [Test]
    public void A_type_its_model_converts_is_asked_for_as_one_field()
    {
        var run = Run("""
            [GraphQLQuery]
            private static Task<Invoice[]> All(HttpClient client, CancellationToken token)
                => client.CreateQueryable<Invoice>("invoices").ToArrayAsync(token);
            """);

        Assert.Multiple(() =>
        {
            // `origin` is the same type with nothing said about it, and so is not a leaf and not
            // selected — which is what the automatic selection does with any object.
            Assert.That(run.Source, Does.Contain("query { invoices { number total issued where } }"));
            Assert.That(run.Diagnostics, Is.Empty);
        });
    }

    /// <summary>
    /// A projection reading a property of such a value asks for the value, and reads the property
    /// where the rows are.
    /// </summary>
    /// <remarks>
    /// The same rule a scalar has already: <c>c.Name.Length</c> asks for <c>name</c> and takes the
    /// length client-side, because tracing on would ask for <c>name { length }</c>. A converted
    /// type is a value in exactly that sense — the converter hands back a whole <c>Stamp</c>, and
    /// <c>Serial</c> is read off it.
    /// </remarks>
    [Test]
    public void A_property_of_a_converted_value_is_read_where_the_rows_are()
    {
        var run = Run("""
            [GraphQLQuery]
            private static Task<string[]> Serials(HttpClient client, CancellationToken token)
                => client.CreateQueryable<Invoice>("invoices")
                    .Select(x => x.Issued.Serial)
                    .ToArrayAsync(token);
            """);

        Assert.Multiple(() =>
        {
            Assert.That(run.Source, Does.Contain("query { invoices { issued } }"));

            // And the two halves agree about it: the field the document asked for as one value is
            // the one the reader reads through the converter.
            Assert.That(run.Source, Does.Contain("_issuedConverter.Read(ref reader"));
            Assert.That(run.Diagnostics, Is.Empty);
        });
    }

    /// <summary>
    /// A converter on the member rather than on the type settles that member alone.
    /// </summary>
    /// <remarks>
    /// <c>Where</c> and <c>Origin</c> are the same type, and only one of them carries a converter.
    /// The reader has always read them differently; the document now does too, which is the whole
    /// of what agreeing means here.
    /// </remarks>
    [Test]
    public void A_converter_on_the_member_settles_that_member_alone()
    {
        var run = Run("""
            [GraphQLQuery]
            private static Task<Invoice[]> Places(HttpClient client, CancellationToken token)
                => client.CreateQueryable<Invoice>("invoices")
                    .Select(x => new Invoice { Where = x.Where, Origin = x.Origin })
                    .ToArrayAsync(token);
            """);

        Assert.Multiple(() =>
        {
            Assert.That(run.Source, Does.Contain("query { invoices { where origin { city country } } }"));
            Assert.That(run.Diagnostics, Is.Empty);
        });
    }

    /// <summary>
    /// A converter neither half could use leaves the type an object to both of them.
    /// </summary>
    /// <remarks>
    /// <c>System.Text.Json</c> wants a public constructor taking nothing and refuses the attribute
    /// without one, so a model that declares this is already broken — but the two halves of a
    /// query still have to be broken the same way. They read one rule, in
    /// <see cref="GraphQLTypeFacts"/>, so that "which converters count" cannot be answered twice.
    /// </remarks>
    [Test]
    public void A_converter_nothing_can_build_is_not_a_converter_to_either_half()
    {
        var run = Run("""
            [GraphQLQuery]
            private static Task<Sealed[]> All(HttpClient client, CancellationToken token)
                => client.CreateQueryable<Sealed>("sealeds").ToArrayAsync(token);
            """);

        Assert.Multiple(() =>
        {
            Assert.That(run.Source, Does.Contain("query { sealeds { label } }"));
            Assert.That(run.Source, Does.Not.Contain("Converter.Read(ref reader"));
            Assert.That(run.Diagnostics, Is.Empty);
        });
    }

    // ---- what a refusal says, and what it points at -----------------------------------------

    /// <summary>
    /// A member is held as the caller declared it, whatever way they spelled "many of these".
    /// </summary>
    /// <remarks>
    /// <para>
    /// The shape that used to send people looking: a projection that is perfectly ordinary C# and
    /// a document that is perfectly ordinary GraphQL, refused because of how one member of the
    /// queried type happened to be declared. Only <c>T[]</c> was accepted, which is a strange
    /// thing to be told about a model the serializer this replaced read without complaining.
    /// </para>
    /// <para>
    /// A reader accumulates a list as it reads, because that is the only shape that can be filled
    /// without knowing the count first — so a member declared as a <c>List</c> is that list, with
    /// no copy between them.
    /// </para>
    /// </remarks>
    [Test]
    public void A_collection_is_held_as_the_member_declares_it()
    {
        var run = Run("""
            [GraphQLQuery]
            private static Task<string[]> Listed(HttpClient client, CancellationToken token)
                => client.CreateQueryable<Listing>("listings")
                    .Select(l => l.Members.Select(m => m.Name).Joined())
                    .ToArrayAsync(token);

            internal static string Joined(this System.Collections.Generic.IEnumerable<string> values)
                => string.Join(", ", values);
            """);

        Assert.Multiple(() =>
        {
            Assert.That(run.Source, Does.Contain("{ listings { members { name } } }"));

            // Declared as the member is, because the projection is the caller's own code and was
            // written against that type — and filled with the list the reader accumulated.
            Assert.That(run.Source, Does.Contain(
                "public readonly global::System.Collections.Generic.List<"));

            Assert.That(run.Source, Does.Contain("_members = _membersItems;"));
            Assert.That(run.Diagnostics, Is.Empty);
        });
    }

    /// <summary>
    /// A member declared read-only is held read-only, and costs nothing to hold.
    /// </summary>
    /// <remarks>
    /// A row is a payload rather than a model: nothing may add to it, and a member typed as
    /// something that can be added to says otherwise. Where the caller declared a read-only
    /// collection this can say so — and where they did not, it cannot, because the projection
    /// they wrote against a <c>List</c> is copied verbatim and has to go on compiling.
    /// </remarks>
    [Test]
    public void A_member_declared_read_only_is_held_read_only()
    {
        var run = Run("""
            [GraphQLQuery]
            private static Task<string[]> Listed(HttpClient client, CancellationToken token)
                => client.CreateQueryable<Catalogue>("catalogues")
                    .Select(c => c.Entries.Select(e => e.Name).Joined())
                    .ToArrayAsync(token);

            internal static string Joined(this System.Collections.Generic.IEnumerable<string> values)
                => string.Join(", ", values);
            """);

        Assert.Multiple(() =>
        {
            Assert.That(run.Source, Does.Contain(
                "public readonly global::System.Collections.Generic.IReadOnlyList<"));

            Assert.That(run.Source, Does.Contain("_entries = _entriesItems;"));
            Assert.That(run.Diagnostics, Is.Empty);
        });
    }

    /// <summary>
    /// A member nothing knows how to build is refused, and told what would work.
    /// </summary>
    /// <remarks>
    /// The set of conversions is the set that can be recognised with certainty — a constructor
    /// taking a collection, or a collection expression the type says how to build. A conversion
    /// guessed at here would be a reply read into the wrong shape rather than a build that fails,
    /// so anything else is a refusal that names the member and the two ways out of it.
    /// </remarks>
    [Test]
    public void A_collection_the_reader_cannot_build_recommends_a_read_only_one()
        => Declines("""
            [GraphQLQuery]
            private static Task<int[]> Counted(HttpClient client, CancellationToken token)
                => client.CreateQueryable<Hoard>("hoards")
                    .Select(h => h.Tags.Count())
                    .ToArrayAsync(token);
            """,
            says: "Declare it 'IReadOnlyCollection<string>', which the rows satisfy as they are",
            at: "Tags");

    /// <summary>
    /// A projection of nothing but values that are not the row's still declines.
    /// </summary>

    /// <summary>
    /// A member whose type the generated reader has no read for is named, and pointed at.
    /// </summary>
    /// <remarks>
    /// The other half of the same message: the read is declined because of the member's type
    /// rather than because of how many of it there are, and the two are not fixed the same way.
    /// </remarks>
    [Test]
    public void A_member_with_no_certain_read_names_the_member()
        => Declines("""
            [GraphQLQuery]
            private static Task<Renamed[]> Timing(HttpClient client, CancellationToken token)
                => client.CreateQueryable<Listing>("listings")
                    .Select(l => new Renamed { Title = l.Span.ToString() })
                    .ToArrayAsync(token);
            """,
            says: "'Span' is a 'TimeSpan', which the generated reader has no read for that is certainly right",
            at: "Span");

    // ---- a projection that mixes the row with everything else -------------------------------

    /// <summary>
    /// A projection may name values that are not the row's.
    /// </summary>
    /// <remarks>
    /// A constant, a static of somebody else's, anything the call site can see: it reads no row,
    /// so it asks the server for nothing, and it is copied into the shaping where it goes on
    /// meaning what it meant. Naming a member is what puts a field in the document — but only
    /// when the member is a row's, which is a question about where the read starts rather than
    /// about the read.
    /// </remarks>
    [Test]
    public void A_projection_may_mix_in_a_value_that_is_not_the_rows()
    {
        var run = Run("""
            [GraphQLQuery]
            private static Task<Renamed[]> Labelled(HttpClient client, CancellationToken token)
                => client.CreateQueryable<Country>("countries")
                    .Select(c => new Renamed { Title = Labels.Default + c.Name })
                    .ToArrayAsync(token);
            """);

        Assert.Multiple(() =>
        {
            // The constant asked for nothing, and the field beside it still asked for itself.
            Assert.That(run.Source, Does.Contain("{ countries { name } }"));
            Assert.That(run.Source, Does.Contain("Title = global::Feather.GraphQL.Linq.Analyzers.Tests.Labels.Default + c.Name"));
            Assert.That(run.Diagnostics, Is.Empty);
        });
    }

    /// <summary>
    /// A nested lambda may read the row it is nested inside, and that field is asked for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The case this walk was rebuilt for. A projection nests, so more than one row is in scope
    /// inside an inner lambda, and a read belongs to whichever one it starts at. Tracking only
    /// the innermost made <c>c.Code</c> here name nothing the innermost row had, which meant it
    /// was skipped — the document went out without <c>code</c>, and the shaping that came back
    /// read <c>c.Code</c> off a row that had no such member.
    /// </para>
    /// <para>
    /// Which is the worst shape a bug in this file can take: not a refusal, but a wrong document
    /// and generated code that does not compile, from a projection that is ordinary C#. The
    /// harness compiles what the generator emitted for exactly this reason.
    /// </para>
    /// </remarks>
    [Test]
    public void A_nested_lambda_may_read_the_row_it_is_nested_in()
    {
        var run = Run("""
            [GraphQLQuery]
            private static Task<string[][]> Qualified(HttpClient client, CancellationToken token)
                => client.CreateQueryable<Country>("countries")
                    .Select(c => c.Continent.Countries.Select(n => n.Name + c.Code).ToArray())
                    .ToArrayAsync(token);
            """);

        Assert.Multiple(() =>
        {
            Assert.That(run.Source,
                Does.Contain("{ countries { continent { countries { name } } code } }"));

            Assert.That(run.Source, Does.Contain("c.Continent.Countries.Select(n => n.Name + c.Code).ToArray()"));
            Assert.That(run.Diagnostics, Is.Empty);
        });
    }

    /// <summary>
    /// A sequence of the caller's own, filtered by a field of the row.
    /// </summary>
    /// <remarks>
    /// Both halves at once: the sequence is not the graph's and asks for nothing, while the
    /// lambda over it reads a row from further out and asks for that. The operators run
    /// client-side either way, so the whole of it is copied into the shaping.
    /// </remarks>
    [Test]
    public void A_sequence_of_the_callers_own_may_be_filtered_by_a_row_field()
    {
        var run = Run("""
            [GraphQLQuery]
            private static Task<string[]> Matching(HttpClient client, CancellationToken token)
                => client.CreateQueryable<Country>("countries")
                    .Select(c => Labels.All.Where(l => l == c.Code).Joined())
                    .ToArrayAsync(token);

            internal static string Joined(this System.Collections.Generic.IEnumerable<string> values)
                => string.Join(", ", values);
            """);

        Assert.Multiple(() =>
        {
            // Only the field the predicate compared against: the list it filtered is not the
            // server's to send.
            Assert.That(run.Source, Does.Contain("{ countries { code } }"));
            Assert.That(run.Source, Does.Contain("global::Feather.GraphQL.Linq.Analyzers.Tests.Labels.All.Where(l => l == c.Code)"));
            Assert.That(run.Diagnostics, Is.Empty);
        });
    }

    /// <summary>
    /// A lambda over what an operator in front of it produced is refused, not guessed at.
    /// </summary>
    /// <remarks>
    /// The other side of knowing which row a read belongs to. <c>r</c> here ranges over
    /// <c>Renamed</c>, which the nested <c>Select</c> made up client-side — so <c>r.Title</c> is
    /// not a field of anything the server has, and placing it under <c>countries</c> would ask
    /// for a field no country has. It was placed there until this walk learned what each node
    /// stands for.
    /// </remarks>
    [Test]
    public void A_lambda_over_what_a_projection_produced_declines()
        => Declines("""
            [GraphQLQuery]
            private static Task<Renamed[][]> Titled(HttpClient client, CancellationToken token)
                => client.CreateQueryable<Country>("countries")
                    .Select(c => c.Continent.Countries
                        .Select(n => new Renamed { Title = n.Name })
                        .Where(r => r.Title != "")
                        .ToArray())
                    .ToArrayAsync(token);
            """,
            says: "ranges over what an operator in front of it produced rather than over rows of the graph",
            at: "r");

    /// <summary>
    /// What is written after a scalar field reads the value the server sent.
    /// </summary>
    /// <remarks>
    /// <c>Length</c> is a property, and for a while that was all this asked: the path was traced
    /// through it and the document went out asking for <c>name { length }</c>, which is not a
    /// thing a schema has. A field that needs no selection set is where a path ends — what
    /// follows it happens where the rows are, like everything else client-side.
    /// </remarks>
    [Test]
    public void A_member_of_a_scalar_field_is_read_where_the_rows_are()
    {
        var run = Run("""
            [GraphQLQuery]
            private static Task<int[]> Lengths(HttpClient client, CancellationToken token)
                => client.CreateQueryable<Country>("countries")
                    .Select(c => c.Name.Trim().Length)
                    .ToArrayAsync(token);
            """);

        Assert.Multiple(() =>
        {
            Assert.That(run.Source, Does.Contain("{ countries { name } }"));
            Assert.That(run.Source, Does.Contain("c.Name.Trim().Length"));
            Assert.That(run.Diagnostics, Is.Empty);
        });
    }

    /// <summary>
    /// A path may run through a client-side call that lands back on a row.
    /// </summary>
    /// <remarks>
    /// <c>First()</c> hands back one of the rows the member holds, so the field named after it is
    /// that member's field and the path goes on from where the chain landed. What makes this safe
    /// to follow rather than a guess is the node knowing which type its fields belong to: a chain
    /// that projected hands back something else, and is refused instead.
    /// </remarks>
    [Test]
    public void A_path_may_run_through_a_call_that_lands_back_on_a_row()
    {
        var run = Run("""
            [GraphQLQuery]
            private static Task<string[]> Neighbouring(HttpClient client, CancellationToken token)
                => client.CreateQueryable<Country>("countries")
                    .Select(c => c.Continent.Countries.First().Continent.Name)
                    .ToArrayAsync(token);
            """);

        Assert.Multiple(() =>
        {
            Assert.That(run.Source,
                Does.Contain("{ countries { continent { countries { continent { name } } } } }"));

            Assert.That(run.Diagnostics, Is.Empty);
        });
    }

    /// <summary>
    /// A path may run through an index, which changes which row is read and not which fields it
    /// has — including an index that is itself a field.
    /// </summary>
    [Test]
    public void A_path_may_run_through_an_index()
    {
        var run = Run("""
            [GraphQLQuery]
            private static Task<string[]> Looked(HttpClient client, CancellationToken token)
                => client.CreateQueryable<Country>("countries")
                    .Select(c => Labels.Map[c.Code] + c.Continent.Countries[0].Name)
                    .ToArrayAsync(token);
            """);

        Assert.Multiple(() =>
        {
            // The lookup is the caller's own and asks for nothing; the field it is indexed by,
            // and the field read off the row the index picked, are both asked for.
            Assert.That(run.Source, Does.Contain("{ countries { code continent { countries { name } } } }"));
            Assert.That(run.Diagnostics, Is.Empty);
        });
    }

    /// <summary>
    /// A projection of nothing but values that are not the row's still declines.
    /// </summary>
    /// <remarks>
    /// Not because the values are a problem — they are not — but because a GraphQL selection set
    /// cannot be empty, and a projection that reads no row names no field. The reason says that
    /// rather than blaming the value it found.
    /// </remarks>
    [Test]
    public void A_projection_that_reads_no_row_at_all_declines()
        => Declines("""
            [GraphQLQuery]
            private static Task<string[]> Labelled(HttpClient client, CancellationToken token)
                => client.CreateQueryable<Country>("countries")
                    .Select(c => Labels.Default)
                    .ToArrayAsync(token);
            """,
            says: "its Select names no field of 'Country'",
            at: "c => Labels.Default");

    /// <summary>Asserts a method was left to the runtime, and said so.</summary>
    private static void Declines(string method)
    {
        var run = Run(method);

        Assert.Multiple(() =>
        {
            Assert.That(run.Source, Is.Null, "a chain outside the compiled subset was compiled anyway");
            Assert.That(run.Diagnostics, Is.EqualTo(new[] { "FGQL015" }));
        });
    }

    /// <summary>
    /// Asserts a method was declined, that the reason named what was wrong, and that the
    /// diagnostic underlines the part of the chain it is about.
    /// </summary>
    /// <remarks>
    /// The location is asserted as the source it covers rather than as a line and a column, so a
    /// case reads as the thing an author would see underlined in the editor — and so that editing
    /// the method above does not move the assertion.
    /// </remarks>
    private static void Declines(string method, string says, string at)
    {
        var run = Run(method);

        Assert.Multiple(() =>
        {
            Assert.That(run.Source, Is.Null, "a chain outside the compiled subset was compiled anyway");
            Assert.That(run.Diagnostics, Is.EqualTo(new[] { "FGQL015" }));
        });

        var reported = run.Reported.Single();

        Assert.Multiple(() =>
        {
            Assert.That(reported.GetMessage(), Does.Contain(says));
            Assert.That(Underlined(reported), Is.EqualTo(at));
        });
    }

    /// <summary>The source a diagnostic covers, which is what an editor draws a squiggle under.</summary>
    private static string Underlined(Diagnostic diagnostic)
        => diagnostic.Location.SourceTree is { } tree
            ? tree.GetText().ToString(diagnostic.Location.SourceSpan)
            : "";

    /// <summary>
    /// What one run of the generator produced.
    /// </summary>
    /// <remarks>
    /// <c>Reported</c> is the diagnostics themselves, which the cases that care about a refusal
    /// read for their reason and their location — <c>Diagnostics</c> is the same thing as IDs,
    /// which is all most cases need.
    /// </remarks>
    private readonly record struct Result(string? Source, string[] Diagnostics, Diagnostic[] Reported);

    /// <summary>
    /// Compiles a method and a call to it, runs the generator, and returns what came out.
    /// </summary>
    /// <remarks>
    /// The call matters as much as the method: interception is by location, so a method nobody
    /// calls produces nothing however compilable it is. The default call passes whatever the
    /// method's parameters are in the order the cases above declare them.
    /// </remarks>
    private static Result Run(string method, string? calls = null)
    {
        string source = $$"""
            using System;
            using System.Linq;
            using System.Net.Http;
            using System.Threading;
            using System.Threading.Tasks;
            using Feather.GraphQL;
            using Feather.GraphQL.Linq;
            using Feather.GraphQL.Linq.Filtering;
            using Feather.GraphQL.Linq.Providers;
            using Feather.GraphQL.Linq.Query;
            using Feather.GraphQL.Linq.Analyzers.Tests;

            public class Renamed
            {
                public string Title { get; set; } = "";
            }

            public static class Snippet
            {
                {{method}}

                public static void Run(HttpClient client)
                {
                    {{calls ?? Calls(method)}}
                }
            }
            """;

        // The same opt-in the package ships, because what is generated is an interceptor and a
        // compilation without it rejects every one of them.
        var parse = new CSharpParseOptions(LanguageVersion.Preview)
            .WithFeatures([new KeyValuePair<string, string>(
                "InterceptorsNamespaces", "Feather.GraphQL.Linq.Generated")]);

        var compilation = CSharpCompilation.Create(
            "Compiled",
            [CSharpSyntaxTree.ParseText(source, parse)],
            SnippetReferences.All(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        Assert.That(Errors(compilation), Is.Empty, "the snippet did not compile");

        CSharpGeneratorDriver
            .Create([new CompiledQueryGenerator().AsSourceGenerator()], parseOptions: parse)
            .RunGeneratorsAndUpdateCompilation(compilation, out var compiled, out var reported);

        // What came out has to compile, which is the one thing reading the generated text cannot
        // tell you. A document missing a field the shaping goes on to read is exactly this: the
        // text looks like the projection that was written, and the row it runs over has no such
        // member. Asserted for every case rather than for the ones that thought to ask, because
        // the ones that do not think to ask are where it would land.
        Assert.That(Errors(compiled), Is.Empty, "the generated code did not compile");

        var generated = compiled.SyntaxTrees.Skip(1).FirstOrDefault();

        return new Result(
            generated?.ToString(),
            [.. reported.Select(d => d.Id)],
            [.. reported]);
    }

    /// <summary>
    /// A call to the method the case declared, made up from its parameters.
    /// </summary>
    /// <remarks>
    /// Written rather than passed in so that each case above is only the thing it is testing.
    /// The values are whatever binds — what they are never reaches the generated code, which
    /// takes the parameter's name, not its value.
    /// </remarks>
    private static string Calls(string method)
    {
        // From the attribute, so a case that also declares a field is not mistaken for one.
        int declaration = method.IndexOf("[GraphQLQuery]", StringComparison.Ordinal);
        int open = method.IndexOf('(', declaration);
        int close = method.IndexOf(')', open);

        var arguments = method.Substring(open + 1, close - open - 1)
            .Split(',')
            .Select(parameter => parameter.Trim().Split(' ')[0] switch
            {
                "HttpClient" => "client",
                "string" => "\"x\"",
                "int" => "1",
                _ => "default"
            });

        string name = method.Substring(declaration, open - declaration).Split(' ').Last();

        return "_ = " + name + "(" + string.Join(", ", arguments) + ");";
    }

    /// <summary>What a compilation rejects, as a message a failing case can read.</summary>
    private static string[] Errors(Compilation compilation)
        => [.. compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())];

    private static int Occurrences(string? text, string value)
    {
        int count = 0;

        for (int i = text?.IndexOf(value, StringComparison.Ordinal) ?? -1; i >= 0;
            i = text!.IndexOf(value, i + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}

/// <summary>
/// A queried type declared the two ways a generated reader cannot fill.
/// </summary>
/// <remarks>
/// Declared here rather than added to the corpus model for the same reason <c>Region</c> is: the
/// document tests compare the corpus byte for byte, and a member added there would change what
/// they pin.
/// </remarks>
public class Listing
{
    public string Name { get; set; } = "";

    /// <summary>Many of something, held as anything but the array a reader fills.</summary>
    public System.Collections.Generic.List<Country> Members { get; set; } = [];

    /// <summary>A type with no getter of the reader's and no converter of the model's.</summary>
    public TimeSpan Span { get; set; }

    /// <summary>Many of itself, so a projection has something to flatten.</summary>
    public Listing[] Related { get; set; } = [];

    /// <summary>A list of scalars, which needs no selection set of its own.</summary>
    public string[] Tags { get; set; } = [];
}

/// <summary>
/// A queried type two collections deep, so a projection has something real to flatten.
/// </summary>
/// <remarks>
/// Declared here rather than added to the corpus for the same reason the rest of these are: the
/// document tests compare the corpus byte for byte.
/// </remarks>
public class Hemisphere
{
    public string Name { get; set; } = "";

    public Continent[] Continents { get; set; } = [];
}

/// <summary>A queried type whose collections are read-only, which is the shape this prefers.</summary>
public class Catalogue
{
    public string Name { get; set; } = "";

    public System.Collections.Generic.IReadOnlyCollection<Country> Entries { get; set; } = [];
}

/// <summary>A queried type holding many of something nothing knows how to build.</summary>
public class Hoard
{
    public string Name { get; set; } = "";

    public Sack Tags { get; set; } = new("");
}

/// <summary>
/// Enumerable, and buildable no way this recognises: no constructor taking a collection, no
/// parameterless one to add to, and nothing saying how a collection expression would make one.
/// </summary>
public sealed class Sack(string only) : System.Collections.Generic.IEnumerable<string>
{
    private readonly string _only = only;

    public System.Collections.Generic.IEnumerator<string> GetEnumerator()
    {
        yield return _only;
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Somewhere a projection might read a value that is not the row's.</summary>
public static class Labels
{
    public static string Default { get; } = "none";

    /// <summary>A sequence of the caller's own, which a projection may filter against a row.</summary>
    public static string[] All { get; } = [];

    /// <summary>A lookup of the caller's own, which a projection may index with a row's field.</summary>
    public static System.Collections.Generic.Dictionary<string, string> Map { get; } = [];
}

/// <summary>
/// A queried type holding values their models take whole.
/// </summary>
/// <remarks>
/// Declared here rather than added to the corpus model for the same reason <c>Listing</c> is: the
/// document tests compare the corpus byte for byte, and a member added there would change what
/// they pin.
/// </remarks>
public class Invoice
{
    public string Number { get; set; } = "";

    /// <summary>A value type whose model converts it — two fields in C#, one on the wire.</summary>
    public Money Total { get; set; }

    /// <summary>The same again as a reference type, which is the shape this was reported as.</summary>
    public Stamp Issued { get; set; } = new();

    /// <summary>A member whose converter its own type does not carry.</summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(PlaceConverter))]
    public Place Where { get; set; } = new();

    /// <summary>The same type with nothing said about it, which is an object like any other.</summary>
    public Place Origin { get; set; } = new();
}

/// <summary>A type that arrives as one value however many properties it has.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(StampConverter))]
public class Stamp
{
    public string Issuer { get; set; } = "";

    public string Serial { get; set; } = "";
}

/// <summary>The same shape with no converter on it, so it stays an object.</summary>
public class Place
{
    public string City { get; set; } = "";

    public string Country { get; set; } = "";
}

/// <summary>A queried type naming a converter nothing can build.</summary>
public class Sealed
{
    public string Label { get; set; } = "";

    public Locked Lock { get; set; } = new();
}

/// <inheritdoc cref="Sealed"/>
[System.Text.Json.Serialization.JsonConverter(typeof(LockedConverter))]
public class Locked
{
    public string Key { get; set; } = "";
}

/// <summary>Reads the whole stamp out of the one string the server sends.</summary>
public sealed class StampConverter : System.Text.Json.Serialization.JsonConverter<Stamp>
{
    public override Stamp Read(
        ref System.Text.Json.Utf8JsonReader reader,
        Type typeToConvert,
        System.Text.Json.JsonSerializerOptions options)
    {
        string[] parts = (reader.GetString() ?? "").Split(':');

        return new Stamp { Issuer = parts[0], Serial = parts.Length > 1 ? parts[1] : "" };
    }

    public override void Write(
        System.Text.Json.Utf8JsonWriter writer,
        Stamp value,
        System.Text.Json.JsonSerializerOptions options)
        => writer.WriteStringValue(value.Issuer + ":" + value.Serial);
}

/// <inheritdoc cref="StampConverter"/>
public sealed class PlaceConverter : System.Text.Json.Serialization.JsonConverter<Place>
{
    public override Place Read(
        ref System.Text.Json.Utf8JsonReader reader,
        Type typeToConvert,
        System.Text.Json.JsonSerializerOptions options)
        => new() { City = reader.GetString() ?? "" };

    public override void Write(
        System.Text.Json.Utf8JsonWriter writer,
        Place value,
        System.Text.Json.JsonSerializerOptions options)
        => writer.WriteStringValue(value.City);
}

/// <summary>A converter with no constructor taking nothing, which nothing can ask for.</summary>
public sealed class LockedConverter(string salt) : System.Text.Json.Serialization.JsonConverter<Locked>
{
    private readonly string _salt = salt;

    public override Locked Read(
        ref System.Text.Json.Utf8JsonReader reader,
        Type typeToConvert,
        System.Text.Json.JsonSerializerOptions options)
        => new() { Key = _salt + reader.GetString() };

    public override void Write(
        System.Text.Json.Utf8JsonWriter writer,
        Locked value,
        System.Text.Json.JsonSerializerOptions options)
        => writer.WriteStringValue(value.Key);
}
