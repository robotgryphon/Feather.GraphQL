using Feather.GraphQL.Linq.Query;
using Feather.GraphQL.Linq.Tests.Query;

namespace Feather.GraphQL.Linq.Tests.Execution;

/// <summary>
/// Result operators are translated, not applied after the fact: the document the server receives
/// already narrows the work, and only the reduction happens here.
/// </summary>
[TestFixture]
public class ResultOperatorTests
{
    private static string ThrowsWith(TestDelegate action)
        => Assert.Throws<GraphQLTranslationException>(action)!.DiagnosticId;

    [Test]
    public void First_asks_the_server_for_one_row()
    {
        var (source, executor) = StubExecutor.Returning("""{"people":[{"name":"Ada"}]}""");

        var person = source.Queryable<Person>().Where(p => p.Age > 30).First();

        Assert.Multiple(() =>
        {
            Assert.That(person.Name, Is.EqualTo("Ada"));
            Assert.That(executor.Document, Does.Contain("take: $v1"));
            Assert.That(executor.Variables, Does.Contain("""
                                                         "v1":1
                                                         """));
        });
    }

    /// <summary>A predicate overload filters exactly as a preceding <c>Where</c> would.</summary>
    [Test]
    public void First_with_a_predicate_folds_it_into_the_filter()
    {
        var (source, executor) = StubExecutor.Returning("""{"people":[{"name":"Ada"}]}""");

        source.Queryable<Person>().First(p => p.Name == "Ada");

        Assert.That(executor.Variables, Does.Contain("""{"name":{"eq":"Ada"}}"""));
    }

    [Test]
    public void First_over_nothing_throws()
    {
        var (source, executor) = StubExecutor.Returning("""{"people":[]}""");

        Assert.Throws<InvalidOperationException>(
            () => source.Queryable<Person>().Where(p => p.Age > 30).First());
    }

    [Test]
    public void FirstOrDefault_over_nothing_is_null()
    {
        var (source, executor) = StubExecutor.Returning("""{"people":[]}""");

        Assert.That(source.Queryable<Person>().Where(p => p.Age > 30).FirstOrDefault(),
            Is.Null);
    }

    /// <summary>Two rows are requested so "more than one" is provable without fetching all.</summary>
    [Test]
    public void Single_asks_for_two_rows_to_detect_ambiguity()
    {
        var (source, executor) = StubExecutor.Returning(
            """{"people":[{"name":"Ada"},{"name":"Grace"}]}""");

        var exception = Assert.Throws<InvalidOperationException>(
            () => source.Queryable<Person>().Where(p => p.Age > 30).Single());

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Message, Does.Contain("more than one"));
            Assert.That(executor.Variables, Does.Contain("""
                                                          "v1":2
                                                          """));
        });
    }

    [Test]
    public void Single_returns_the_only_row()
    {
        var (source, executor) = StubExecutor.Returning("""{"people":[{"name":"Ada"}]}""");

        Assert.That(source.Queryable<Person>().Where(p => p.Age > 30).Single().Name,
            Is.EqualTo("Ada"));
    }

    /// <summary>An existence check selects one scalar and one row, never the full projection.</summary>
    [Test]
    public void Any_selects_a_single_cheap_field()
    {
        var (source, executor) = StubExecutor.Returning("""{"people":[{"name":"Ada"}]}""");

        bool any = source.Queryable<Person>().Where(p => p.Age > 30).Any();

        Assert.Multiple(() =>
        {
            Assert.That(any, Is.True);
            Assert.That(executor.Document, Does.Contain("{ people(where: $v0, take: $v1) { name } }"));
        });
    }

    [Test]
    public void Any_over_nothing_is_false()
    {
        var (source, executor) = StubExecutor.Returning("""{"people":[]}""");

        Assert.That(source.Queryable<Person>().Where(p => p.Age > 30).Any(), Is.False);
    }

    [Test]
    public void Count_asks_the_connection_for_totalCount()
    {
        var (source, executor) = StubExecutor.Returning("""{"connected":{"totalCount":42}}""");

        int count = source.Queryable<CursorPerson>().Count();

        Assert.Multiple(() =>
        {
            Assert.That(count, Is.EqualTo(42));
            Assert.That(executor.Document, Does.Contain("{ connected { totalCount } }"));
        });
    }

    [Test]
    public void Count_over_offset_paging_also_reads_totalCount()
    {
        var (source, executor) = StubExecutor.Returning("""{"offset":{"totalCount":7}}""");

        Assert.That(source.Queryable<OffsetPerson>().Count(), Is.EqualTo(7));
    }

    [Test]
    public void Count_over_an_unpaged_root_field_is_FGQL009()
    {
        var (source, executor) = StubExecutor.Returning("""{"people":[]}""");

        Assert.That(ThrowsWith(() => source.Queryable<Person>().Count()),
            Is.EqualTo("FGQL009"));
    }

    /// <summary><c>totalCount</c> is the size of the collection, not of a page.</summary>
    [Test]
    public void Count_after_paging_is_FGQL009()
    {
        var (source, executor) = StubExecutor.Returning("""{"connected":{"totalCount":1}}""");

        Assert.That(ThrowsWith(() => source.Queryable<CursorPerson>().Take(5).Count()),
            Is.EqualTo("FGQL009"));
    }

    [Test]
    public void Count_without_totalCount_on_the_connection_is_FGQL009()
    {
        var (source, executor) = StubExecutor.Returning("""{"connected":{"nodes":[]}}""");

        Assert.That(ThrowsWith(() => source.Queryable<CursorPerson>().Count()),
            Is.EqualTo("FGQL009"));
    }

    /// <summary>Cursor paging reads backwards with <c>last:</c>.</summary>
    [Test]
    public void Last_over_cursor_paging_reads_backwards()
    {
        var (source, executor) = StubExecutor.Returning("""{"connected":{"nodes":[{"name":"Zoe"}]}}""");

        var person = source.Queryable<CursorPerson>().Last();

        Assert.Multiple(() =>
        {
            Assert.That(person.Name, Is.EqualTo("Zoe"));
            Assert.That(executor.Document, Does.Contain("{ connected(last: $v0) { nodes { name } } }"));
        });
    }

    [Test]
    public void Last_without_cursor_paging_is_FGQL015()
    {
        var (source, executor) = StubExecutor.Returning("""{"offset":{"items":[]}}""");

        Assert.That(ThrowsWith(() => source.Queryable<OffsetPerson>().Last()),
            Is.EqualTo("FGQL015"));
    }

    /// <summary>Cursor paging's backwards read is renameable like any other argument.</summary>
    [Test]
    public void The_backwards_read_can_be_renamed()
    {
        var (source, executor) = StubExecutor.Returning("""{"connected":{"nodes":[{"name":"Zoe"}]}}""");

        source.Queryable<CursorPerson>().WithGraphQLArguments(new() { Last = "tail" }).Last();

        Assert.That(executor.Document, Does.Contain("{ connected(tail: $v0) { nodes { name } } }"));
    }

    [Test]
    public async Task Async_result_operators_translate_identically()
    {
        var (source, executor) = StubExecutor.Returning("""{"connected":{"totalCount":42}}""");

        Assert.That(await source.Queryable<CursorPerson>().CountAsync(), Is.EqualTo(42));
        Assert.That(executor.Document, Does.Contain("{ connected { totalCount } }"));
    }
}
