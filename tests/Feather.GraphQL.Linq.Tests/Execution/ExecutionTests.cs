using Feather.GraphQL.Linq.Query;
using Feather.GraphQL.Linq.Tests.Query;

namespace Feather.GraphQL.Linq.Tests.Execution;

/// <summary>
/// A queryable from the source executes: ordinary LINQ terminals return data rather than
/// throwing, and the projection is applied to what comes back.
/// </summary>
[TestFixture]
public class ExecutionTests
{
    [Test]
    public void ToArray_materializes_the_rows()
    {
        var executor = StubExecutor.Returning(
            """{"people":[{"name":"Ada","age":36},{"name":"Grace","age":45}]}""");

        var people = executor.Queryable<Person>("people")
            .Where(p => p.Age > 30)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(people, Has.Length.EqualTo(2));
            Assert.That(people[0].Name, Is.EqualTo("Ada"));
            Assert.That(people[1].Age, Is.EqualTo(45));
        });
    }

    [Test]
    public void Foreach_executes_the_query()
    {
        var executor = StubExecutor.Returning("""{"people":[{"name":"Ada"}]}""");

        var names = new List<string>();
        foreach (var person in executor.Queryable<Person>("people").Where(p => p.Age > 30))
            names.Add(person.Name);

        Assert.That(names, Is.EqualTo(new[] { "Ada" }));
    }

    [Test]
    public void Projection_is_applied_to_the_materialized_rows()
    {
        var executor = StubExecutor.Returning(
            """{"people":[{"name":"Ada","emailAddress":"ada@example.test"}]}""");

        var rows = executor.Queryable<Person>("people")
            .Where(p => p.Age > 30)
            .Select(p => new { p.Name, p.Email })
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(rows[0].Name, Is.EqualTo("Ada"));
            Assert.That(rows[0].Email, Is.EqualTo("ada@example.test"));
        });
    }

    /// <summary>
    /// A scalar projection leaves the type's other members — including required ones — absent
    /// from the response, which materialization has to tolerate.
    /// </summary>
    [Test]
    public void Scalar_projection_materializes_without_the_other_members()
    {
        var executor = StubExecutor.Returning("""{"people":[{"age":36},{"age":45}]}""");

        var ages = executor.Queryable<Person>("people")
            .Where(p => p.Age > 30)
            .Select(p => p.Age)
            .ToArray();

        Assert.That(ages, Is.EqualTo(new[] { 36, 45 }));
    }

    [Test]
    public void Cursor_paging_reads_through_nodes()
    {
        var executor = StubExecutor.Returning("""{"connected":{"nodes":[{"name":"Ada"}]}}""");

        var people = executor.Queryable<CursorPerson>("connected", o => o.Paging = PagingKind.Cursor).Take(1).ToArray();

        Assert.That(people[0].Name, Is.EqualTo("Ada"));
    }

    [Test]
    public void Offset_paging_reads_through_items()
    {
        var executor = StubExecutor.Returning("""{"offset":{"items":[{"name":"Ada"}]}}""");

        var people = executor.Queryable<OffsetPerson>("offset", o => o.Paging = PagingKind.Offset).Take(1).ToArray();

        Assert.That(people[0].Name, Is.EqualTo("Ada"));
    }

    [Test]
    public void A_null_root_field_reads_as_an_empty_result()
    {
        var executor = StubExecutor.Returning("""{"people":null}""");

        Assert.That(executor.Queryable<Person>("people").Where(p => p.Age > 30).ToArray(), Is.Empty);
    }

    [Test]
    public void A_paging_mismatch_is_FGQL018()
    {
        var executor = StubExecutor.Returning("""{"connected":{"items":[]}}""");

        var exception = Assert.Throws<GraphQLTranslationException>(
            () => executor.Queryable<CursorPerson>("connected", o => o.Paging = PagingKind.Cursor).Take(1).ToArray());

        Assert.That(exception!.DiagnosticId, Is.EqualTo("FGQL018"));
    }

    /// <summary>
    /// What a server error looks like is the transport's business — this layer only has to not
    /// swallow it. The HTTP package's own tests cover the GraphQL errors array.
    /// </summary>
    [Test]
    public void A_transport_failure_reaches_the_caller()
    {
        var executor = StubExecutor.Failing(new InvalidOperationException("transport is down"));

        var exception = Assert.Throws<InvalidOperationException>(
            () => executor.Queryable<Person>("people").Where(p => p.Age > 30).ToArray());

        Assert.That(exception!.Message, Is.EqualTo("transport is down"));
    }

    [Test]
    public async Task Async_terminals_execute_the_same_chain()
    {
        var executor = StubExecutor.Returning("""{"people":[{"name":"Ada"}]}""");

        var people = await executor.Queryable<Person>("people")
            .Where(p => p.Age > 30)
            .ToListAsync();

        Assert.That(people[0].Name, Is.EqualTo("Ada"));
    }

    [Test]
    public async Task Await_foreach_executes_the_query()
    {
        var executor = StubExecutor.Returning("""{"people":[{"name":"Ada"},{"name":"Grace"}]}""");

        var names = new List<string>();
        await foreach (var person in executor.Queryable<Person>("people").Where(p => p.Age > 30)
                     .AsAsyncEnumerable())
            names.Add(person.Name);

        Assert.That(names, Is.EqualTo(new[] { "Ada", "Grace" }));
    }

    /// <summary>The client-less entry point can still translate, but has nowhere to send.</summary>
    [Test]
    public void A_queryable_with_no_endpoint_is_FGQL016()
    {
        var exception = Assert.Throws<GraphQLTranslationException>(
            () => Schema.People.Where(p => p.Age > 30).ToArray());

        Assert.That(exception!.DiagnosticId, Is.EqualTo("FGQL016"));
    }

    [Test]
    public void Async_terminals_refuse_a_foreign_provider()
    {
        var exception = Assert.Throws<GraphQLTranslationException>(
            () => Array.Empty<Person>().AsQueryable().ToListAsync().GetAwaiter().GetResult());

        Assert.That(exception!.DiagnosticId, Is.EqualTo("FGQL019"));
    }
}
