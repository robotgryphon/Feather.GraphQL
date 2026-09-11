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
}
