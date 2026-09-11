using System.Runtime.CompilerServices;
using Feather.GraphQL.Linq.Query;

namespace Feather.GraphQL.Linq.Providers.Tests;

/// <summary>
/// Precompilation across the HTTP entry point, which is a C# 14 extension member rather than a
/// plain static method.
/// </summary>
/// <remarks>
/// Worth its own fixture because the entry point's shape is the part interception is pickiest
/// about: an interceptor has to match the method it replaces, and an extension member reports no
/// <c>ReducedFrom</c> and is not an extension method — its receiver lives on the extension
/// container. If the signature the generator derives from that were wrong, this file would not
/// compile.
/// </remarks>
[TestFixture]
public class PrecompiledHttpQueryTests
{
    private const string Rows = """{"data":{"people":[{"name":"Ada","age":36}]}}""";

    [Test]
    public void An_inline_chain_over_an_http_client_is_precompiled()
    {
        var handler = new StubHandler(Rows);
        int before = GraphQLPrecompiled.Attachments;

        var people = handler.Client()
            .CreateQueryable<Person>("people")
            .Where(p => p.Age > 30)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(GraphQLPrecompiled.Attachments, Is.EqualTo(before + 1),
                "the extension-member entry point was not intercepted");

            Assert.That(handler.SentBody,
                Does.Contain(@"query($v0: PersonFilterInput) { people(where: $v0) { name age } }"));

            Assert.That(people[0].Name, Is.EqualTo("Ada"));
        });
    }

    /// <summary>
    /// The same chain built through a helper is not precompiled, and posts the same document —
    /// the comparison that makes the precompiled one trustworthy.
    /// </summary>
    [Test]
    public void The_precompiled_document_is_what_the_translator_would_have_sent()
    {
        var precompiled = new StubHandler(Rows);
        var translated = new StubHandler(Rows);

        _ = precompiled.Client().CreateQueryable<Person>("people").Where(p => p.Age > 30).ToArray();

        var chain = translated.Client().CreateQueryable<Person>("people");
        _ = chain.Where(p => p.Age > 30).ToArray();

        Assert.That(precompiled.SentBody, Is.EqualTo(translated.SentBody));
    }

    /// <summary>
    /// A terminal that asks the server for a page binds its size, and the compiler now supplies
    /// that too — so what it supplies has to be what the translator would have bound.
    /// </summary>
    /// <remarks>
    /// A document comparison would not catch this. The page is a <em>variable</em>, so a wrong
    /// size prints the same document and posts a different payload; only the whole body shows it.
    /// </remarks>
    [Test]
    public void A_precompiled_first_posts_the_page_the_translator_would_have()
    {
        var precompiled = new StubHandler(Rows);
        var translated = new StubHandler(Rows);

        _ = precompiled.Client().CreateQueryable<Person>("people").First();
        _ = Source(translated.Client()).First();

        Assert.That(precompiled.SentBody, Is.EqualTo(translated.SentBody));
    }

    [Test]
    public void A_precompiled_single_posts_the_page_the_translator_would_have()
    {
        var precompiled = new StubHandler(Rows);
        var translated = new StubHandler(Rows);

        _ = precompiled.Client().CreateQueryable<Person>("people").Single();
        _ = Source(translated.Client()).Single();

        Assert.That(precompiled.SentBody, Is.EqualTo(translated.SentBody));
    }

    /// <summary>
    /// A precompiled plan carries the projection as the key of a generated shaper rather than as
    /// a lambda, so the rows it produces are what proves the right shaper was found.
    /// </summary>
    [Test]
    public void A_precompiled_projection_shapes_the_rows_the_translator_would_have()
    {
        var precompiled = new StubHandler(Rows);
        var translated = new StubHandler(Rows);

        var fast = precompiled.Client().CreateQueryable<Person>("people")
            .Select(p => new { p.Name, p.Age })
            .ToArray();

        var slow = Source(translated.Client()).Select(p => new { p.Name, p.Age }).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(precompiled.SentBody, Is.EqualTo(translated.SentBody));
            Assert.That(fast, Is.EqualTo(slow));
            Assert.That(fast[0].Name, Is.EqualTo("Ada"));
            Assert.That(fast[0].Age, Is.EqualTo(36));
        });
    }

    /// <summary>
    /// The same queryable, behind a boundary the generator declines to look through — so the
    /// chain is translated at runtime and can be compared against the precompiled one.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IQueryable<Person> Source(HttpClient client)
        => client.CreateQueryable<Person>("people");

    /// <summary>
    /// A precompiled filter is printed at build time and filled at run time, so the only thing
    /// that proves the two halves agree is the body they produce together.
    /// </summary>
    /// <remarks>
    /// The shape comes from the compiler and the values from the expression tree, numbered by two
    /// different walks over the same predicate. A disagreement would put the right values under
    /// the wrong fields — a well-formed request that filters differently — which no document
    /// comparison would catch.
    /// </remarks>
    [TestCase(30)]
    [TestCase(50)]
    public void A_precompiled_filter_posts_what_the_translator_would_have(int age)
    {
        var precompiled = new StubHandler(Rows);
        var translated = new StubHandler(Rows);

        _ = precompiled.Client().CreateQueryable<Person>("people").Where(p => p.Age > age).ToArray();
        _ = Source(translated.Client()).Where(p => p.Age > age).ToArray();

        Assert.That(precompiled.SentBody, Is.EqualTo(translated.SentBody));
    }

    /// <summary>Several clauses, which is where the hole ordering could go wrong.</summary>
    [Test]
    public void A_precompiled_multi_clause_filter_posts_what_the_translator_would_have()
    {
        var precompiled = new StubHandler(Rows);
        var translated = new StubHandler(Rows);

        _ = precompiled.Client().CreateQueryable<Person>("people")
            .Where(p => p.Name == "Ada" && p.Age > 30)
            .ToArray();

        _ = Source(translated.Client()).Where(p => p.Name == "Ada" && p.Age > 30).ToArray();

        Assert.That(precompiled.SentBody, Is.EqualTo(translated.SentBody));
    }

    /// <summary>Several Where calls merge, and they have to merge in the same order on both sides.</summary>
    [Test]
    public void A_precompiled_chain_of_wheres_posts_what_the_translator_would_have()
    {
        var precompiled = new StubHandler(Rows);
        var translated = new StubHandler(Rows);

        _ = precompiled.Client().CreateQueryable<Person>("people")
            .Where(p => p.Name == "Ada")
            .Where(p => p.Age > 30)
            .ToArray();

        _ = Source(translated.Client()).Where(p => p.Name == "Ada").Where(p => p.Age > 30).ToArray();

        Assert.That(precompiled.SentBody, Is.EqualTo(translated.SentBody));
    }
}
