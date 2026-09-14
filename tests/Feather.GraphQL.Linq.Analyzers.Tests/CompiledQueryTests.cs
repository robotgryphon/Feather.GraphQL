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
            """);

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
            """);

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
            """);

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

    /// <summary>What one run of the generator produced.</summary>
    private readonly record struct Result(string? Source, string[] Diagnostics);

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

        var compilation = CSharpCompilation.Create(
            "Compiled",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview))],
            SnippetReferences.All(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToArray();

        Assert.That(errors, Is.Empty,
            $"the snippet did not compile: {string.Join("; ", errors.Select(e => e.ToString()))}");

        var run = CSharpGeneratorDriver
            .Create(new CompiledQueryGenerator())
            .RunGenerators(compilation)
            .GetRunResult();

        return new Result(
            run.GeneratedTrees.Length == 0 ? null : run.GeneratedTrees[0].ToString(),
            [.. run.Diagnostics.Select(d => d.Id)]);
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
