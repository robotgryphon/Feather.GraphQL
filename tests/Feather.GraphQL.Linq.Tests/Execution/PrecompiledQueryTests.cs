using Feather.GraphQL.Linq.Query;
using Feather.GraphQL.Linq.Tests.Query;

namespace Feather.GraphQL.Linq.Tests.Execution;

/// <summary>
/// The precompiled path, end to end: the interceptor the compiler emitted for a chain in this
/// very file runs, and what goes on the wire is what the runtime would have sent.
/// </summary>
/// <remarks>
/// These tests are the only place the generated code is actually executed rather than inspected.
/// The document tests in the analyzer suite prove the compiler prints the right string; this
/// proves the string reaches the transport — that the interceptor compiles, binds to the entry
/// point, and does not change the result.
/// </remarks>
[TestFixture]
public class PrecompiledQueryTests
{
    private const string Rows = """{"people":[{"name":"Ada","age":36}]}""";

    /// <summary>
    /// A chain written as one expression is precompiled, and sends the precompiled document.
    /// </summary>
    [Test]
    public void An_inline_chain_runs_from_the_precompiled_document()
    {
        var executor = StubExecutor.Returning(Rows);
        int before = GraphQLPrecompiled.Attachments;

        var people = GraphQLQueryable.For<Person>(executor, "people")
            .Where(p => p.Age > 30)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(GraphQLPrecompiled.Attachments, Is.EqualTo(before + 1),
                "the chain was not intercepted, so this test proves nothing about the generated code");

            Assert.That(executor.Document,
                Is.EqualTo("query($v0: PersonFilterInput) { people(where: $v0) { name age emailAddress } }"));

            Assert.That(people[0].Name, Is.EqualTo("Ada"));
        });
    }

    /// <summary>
    /// The values are still read at runtime: the document is shape-only, so the predicate's
    /// captured value has to arrive through the variables payload.
    /// </summary>
    [Test]
    public void A_precompiled_document_still_carries_its_values()
    {
        var executor = StubExecutor.Returning(Rows);
        int age = 30;

        _ = GraphQLQueryable.For<Person>(executor, "people")
            .Where(p => p.Age > age)
            .ToArray();

        Assert.That(executor.Variables, Is.EqualTo("""{"v0":{"age":{"gt":30}}}"""));
    }

    /// <summary>
    /// Two chains of the same shape and different values print one document, which is what makes
    /// a precompiled string correct for every execution of that call site.
    /// </summary>
    [Test]
    public void One_document_serves_every_value()
    {
        var first = StubExecutor.Returning(Rows);
        var second = StubExecutor.Returning(Rows);

        _ = GraphQLQueryable.For<Person>(first, "people").Where(p => p.Age > 30).ToArray();
        _ = GraphQLQueryable.For<Person>(second, "people").Where(p => p.Age > 99).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(second.Document, Is.EqualTo(first.Document));
            Assert.That(second.Variables, Is.Not.EqualTo(first.Variables));
        });
    }

    /// <summary>
    /// A chain built up through locals is followed to where it is used, and precompiled there.
    /// </summary>
    /// <remarks>
    /// The pattern most real code is written in, including the example — which is why following
    /// it was worth the analysis rather than asking people to write one long expression.
    /// </remarks>
    [Test]
    public void A_chain_built_in_steps_is_precompiled_and_agrees()
    {
        var executor = StubExecutor.Returning(Rows);
        int before = GraphQLPrecompiled.Attachments;

        var query = GraphQLQueryable.For<Person>(executor, "people");
        var filtered = query.Where(p => p.Age > 30);

        _ = filtered.ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(GraphQLPrecompiled.Attachments, Is.EqualTo(before + 1));

            Assert.That(executor.Document,
                Is.EqualTo("query($v0: PersonFilterInput) { people(where: $v0) { name age emailAddress } }"));
        });
    }

    /// <summary>
    /// Two uses of one chain that need different documents are left to the runtime: they share a
    /// provider, so there is no single document that would be right for both.
    /// </summary>
    [Test]
    public void Uses_that_need_different_documents_are_left_alone()
    {
        var executor = StubExecutor.Returning(Rows);
        int before = GraphQLPrecompiled.Attachments;

        var query = GraphQLQueryable.For<Person>(executor, "people").Where(p => p.Age > 30);

        _ = query.First();
        _ = query.ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(GraphQLPrecompiled.Attachments, Is.EqualTo(before),
                "one document cannot serve both a First() and a ToArray()");

            // The runtime translated the second use on its own, as it always did.
            Assert.That(executor.Document,
                Is.EqualTo("query($v0: PersonFilterInput) { people(where: $v0) { name age emailAddress } }"));
        });
    }

    /// <summary>
    /// A chain handed to something else could be composed there, so what is visible is not the
    /// whole chain — and the gate still holds.
    /// </summary>
    [Test]
    public void A_chain_handed_to_a_helper_is_not_precompiled()
    {
        var executor = StubExecutor.Returning(Rows);
        int before = GraphQLPrecompiled.Attachments;

        var query = GraphQLQueryable.For<Person>(executor, "people").Where(p => p.Age > 30);

        _ = Youngest(query);

        Assert.Multiple(() =>
        {
            Assert.That(GraphQLPrecompiled.Attachments, Is.EqualTo(before),
                "a queryable passed elsewhere may be composed elsewhere");

            // Composed inside the helper, which is exactly what could not be seen.
            Assert.That(executor.Document, Does.Contain("order: $v1, take: $v2"));
        });
    }

    private static Person Youngest(IQueryable<Person> people)
        => people.OrderBy(p => p.Age).First();

    /// <summary>
    /// A projection's fields come from the compiler here, which is the part of the document most
    /// likely to differ between the two implementations.
    /// </summary>
    [Test]
    public void A_projected_chain_runs_from_the_precompiled_document()
    {
        var executor = StubExecutor.Returning("""{"people":[{"name":"Ada"}]}""");
        int before = GraphQLPrecompiled.Attachments;

        var names = GraphQLQueryable.For<Person>(executor, "people")
            .Select(p => new { p.Name })
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(GraphQLPrecompiled.Attachments, Is.EqualTo(before + 1));
            Assert.That(executor.Document, Is.EqualTo("query { people { name } }"));
            Assert.That(names[0].Name, Is.EqualTo("Ada"));
        });
    }
}
