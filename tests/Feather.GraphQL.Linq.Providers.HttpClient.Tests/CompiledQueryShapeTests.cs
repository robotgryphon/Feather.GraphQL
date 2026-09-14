using Feather.GraphQL;
using Feather.GraphQL.Linq.Filtering;
using Feather.GraphQL.Linq.Query;

namespace Feather.GraphQL.Linq.Providers.Tests;

/// <summary>
/// The chain shapes <c>[GraphQLQuery]</c> covers beyond a plain filtered sequence.
/// </summary>
/// <remarks>
/// <para>
/// Each of these used to be a decline, and each asks a different question of the generated code:
/// an ordering has to be written out as a constant, a page has to be bound from a parameter, and
/// a terminal has to reduce the rows the way the runtime would — including by throwing the same
/// exception with the same message.
/// </para>
/// <para>
/// The counter is the guard, as in <see cref="CompiledQueryTests"/>: it moves only if the chain
/// ran, which would mean the compiled call had not replaced anything and the assertion below it
/// was measuring the runtime translator instead.
/// </para>
/// </remarks>
[TestFixture]
public class CompiledQueryShapeTests
{
    private const string Rows = """{"data":{"people":[{"name":"Ada","age":36}]}}""";
    private const string Two = """{"data":{"people":[{"name":"Ada","age":36},{"name":"Alan","age":41}]}}""";
    private const string Empty = """{"data":{"people":[]}}""";
    private const string Teams = """{"data":{"teams":[{"lead":{"name":"Ada","age":36}}]}}""";

    // ---- ordering ---------------------------------------------------------------------------

    /// <summary>
    /// An ordering binds no value, so the whole sort argument is a constant.
    /// </summary>
    /// <remarks>
    /// Unbounded on purpose — FGQL012 is about the chain asking for every record, which is what
    /// keeps these two rows about the ordering and nothing else.
    /// </remarks>
#pragma warning disable FGQL012
    [GraphQLQuery]
    private static Task<Person[]> OrderedAsync(HttpClient client, CancellationToken cancellationToken)
        => client.CreateQueryable<Person>("people")
            .OrderBy(p => p.Name)
            .ToArrayAsync(cancellationToken);

    [GraphQLQuery]
    private static Task<Person[]> OrderedDownAsync(HttpClient client, CancellationToken cancellationToken)
        => client.CreateQueryable<Person>("people")
            .OrderByDescending(p => p.Age)
            .ThenBy(p => p.Name)
            .ToArrayAsync(cancellationToken);
#pragma warning restore FGQL012

    [Test]
    public async Task An_ordering_is_written_out_as_a_constant()
    {
        var handler = new StubHandler(Rows);

        await OrderedAsync(handler.Client(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(handler.SentBody,
                Does.Contain(@"query($v0: [PersonSortInput!]) { people(order: $v0) { name age } }"));

            Assert.That(handler.SentBody, Does.Contain(@"""variables"":{""v0"":[{""name"":""ASC""}]}"));
        });
    }

    [Test]
    public async Task Several_keys_keep_their_order_and_direction()
    {
        var handler = new StubHandler(Rows);

        await OrderedDownAsync(handler.Client(), CancellationToken.None);

        Assert.That(handler.SentBody,
            Does.Contain(@"""v0"":[{""age"":""DESC""},{""name"":""ASC""}]"));
    }

    // ---- paging -----------------------------------------------------------------------------

    /// <summary>A page size the caller passes, which arrives as an argument rather than a tree.</summary>
    [GraphQLQuery]
    private static Task<Person[]> PagedAsync(
        HttpClient client, int size, int from, CancellationToken cancellationToken)
        => client.CreateQueryable<Person>("people")
            .Skip(from)
            .Take(size)
            .ToArrayAsync(cancellationToken);

    [Test]
    public async Task A_page_is_bound_from_the_methods_parameters()
    {
        var handler = new StubHandler(Rows);

        await PagedAsync(handler.Client(), 10, 20, CancellationToken.None);

        Assert.Multiple(() =>
        {
            // Binding order is the document's numbering: the page before the offset.
            Assert.That(handler.SentBody,
                Does.Contain(@"query($v0: Int, $v1: Int) { people(take: $v0, skip: $v1) { name age } }"));

            Assert.That(handler.SentBody, Does.Contain(@"""variables"":{""v0"":10,""v1"":20}"));
        });
    }

    /// <summary>An ordering and a filter and a page together, to pin the numbering.</summary>
    [GraphQLQuery]
    private static Task<Person[]> EverythingAsync(
        HttpClient client, int min, int size, CancellationToken cancellationToken)
        => client.CreateQueryable<Person>("people")
            .Where(p => p.Age > min)
            .OrderBy(p => p.Name)
            .Take(size)
            .ToArrayAsync(cancellationToken);

    [Test]
    public async Task Filter_order_and_page_are_numbered_as_the_document_declares_them()
    {
        var handler = new StubHandler(Rows);

        await EverythingAsync(handler.Client(), 30, 5, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(handler.SentBody, Does.Contain(
                "query($v0: Int, $v1: [PersonSortInput!], $v2: Int) "
                + "{ people(where: { age: { gt: $v0 } }, order: $v1, take: $v2) { name age } }"));

            Assert.That(handler.SentBody, Does.Contain(
                @"""variables"":{""v0"":30,""v1"":[{""name"":""ASC""}],""v2"":5}"));
        });
    }

    // ---- result operators -------------------------------------------------------------------

    /// <summary>First asks the server for a page of one, and reduces to it.</summary>
    [GraphQLQuery]
    private static Task<Person> FirstAsync(HttpClient client, string name, CancellationToken cancellationToken)
        => client.CreateQueryable<Person>("people")
            .FirstAsync(p => p.Name == name, cancellationToken);

    [GraphQLQuery]
    private static Task<Person?> FirstOrNoneAsync(HttpClient client, CancellationToken cancellationToken)
        => client.CreateQueryable<Person>("people").FirstOrDefaultAsync(cancellationToken);

    [GraphQLQuery]
    private static Task<Person> OnlyAsync(HttpClient client, CancellationToken cancellationToken)
        => client.CreateQueryable<Person>("people").SingleAsync(cancellationToken);

    [GraphQLQuery]
    private static Task<bool> AnyoneAsync(HttpClient client, int min, CancellationToken cancellationToken)
        => client.CreateQueryable<Person>("people").AnyAsync(p => p.Age > min, cancellationToken);

    [Test]
    public async Task First_asks_for_one_row_and_returns_it()
    {
        var handler = new StubHandler(Rows);

        var person = await FirstAsync(handler.Client(), "Ada", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(handler.SentBody, Does.Contain("take: $v1"));
            Assert.That(handler.SentBody, Does.Contain(@"""v1"":1"));
            Assert.That(person.Name, Is.EqualTo("Ada"));
        });
    }

    /// <summary>
    /// The empty case throws what the runtime throws, message included — a compiled query has to
    /// mean the same thing as the chain it was written from.
    /// </summary>
    [Test]
    public void First_over_nothing_throws_what_the_runtime_throws()
    {
        var handler = new StubHandler(Empty);

        var thrown = Assert.ThrowsAsync<InvalidOperationException>(
            async () => await FirstAsync(handler.Client(), "Ada", CancellationToken.None));

        Assert.That(thrown.Message, Is.EqualTo("The query returned no elements."));
    }

    [Test]
    public async Task FirstOrDefault_over_nothing_returns_null()
        => Assert.That(await FirstOrNoneAsync(new StubHandler(Empty).Client(), CancellationToken.None),
            Is.Null);

    /// <summary>Single asks for two rows, which is how it can tell there was only one.</summary>
    [Test]
    public async Task Single_asks_for_two_rows()
    {
        var handler = new StubHandler(Rows);

        await OnlyAsync(handler.Client(), CancellationToken.None);

        Assert.That(handler.SentBody, Does.Contain(@"""v0"":2"));
    }

    [Test]
    public void Single_over_several_throws_what_the_runtime_throws()
    {
        var handler = new StubHandler(Two);

        var thrown = Assert.ThrowsAsync<InvalidOperationException>(
            async () => await OnlyAsync(handler.Client(), CancellationToken.None));

        Assert.That(thrown.Message, Is.EqualTo("The query returned more than one element."));
    }

    /// <summary>An existence check selects one cheap scalar and reads no rows out at all.</summary>
    [Test]
    public async Task Any_selects_one_field_and_answers_from_the_row_count()
    {
        var present = new StubHandler(Rows);
        var absent = new StubHandler(Empty);

        bool found = await AnyoneAsync(present.Client(), 30, CancellationToken.None);
        bool none = await AnyoneAsync(absent.Client(), 30, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(present.SentBody,
                Does.Contain("{ people(where: { age: { gt: $v0 } }, take: $v1) { name } }"));
            Assert.That(found, Is.True);
            Assert.That(none, Is.False);
        });
    }

    // ---- projection -------------------------------------------------------------------------

    /// <summary>
    /// The projections that would drop data if the reply were read into the projected type.
    /// </summary>
    /// <remarks>
    /// Each of these has a member whose name is not the field's name, or is not a field at all.
    /// Read by name they would come back null; shaped by running the projection, they come back
    /// right — which is the whole reason the compiled path reads rows as the queried type and
    /// projects afterwards, exactly as the runtime does.
    /// </remarks>
    [GraphQLQuery]
    private static Task<NameOnly[]> NamesAsync(HttpClient client, CancellationToken cancellationToken)
        => client.CreateQueryable<Person>("people")
            .Select(p => new NameOnly { Name = p.Name })
            .ToArrayAsync(cancellationToken);

    /// <summary>A renamed member: the field is <c>name</c>, the property is <c>Title</c>.</summary>
    [GraphQLQuery]
    private static Task<Renamed[]> RenamedAsync(HttpClient client, CancellationToken cancellationToken)
        => client.CreateQueryable<Person>("people")
            .Select(p => new Renamed { Title = p.Name, Years = p.Age })
            .ToArrayAsync(cancellationToken);

    /// <summary>
    /// A member reached through another, which flattens in the projection but not in the JSON.
    /// </summary>
    /// <remarks>
    /// The strongest case for shaping rather than reading by name: the reply carries
    /// <c>{"lead":{"name":…}}</c> and the target has a flat <c>Title</c>, so there is no name to
    /// match at all. Read by name it would be null; projected, it is the lead's name.
    /// </remarks>
    [GraphQLQuery]
    private static Task<Renamed[]> LeadsAsync(HttpClient client, CancellationToken cancellationToken)
        => client.CreateQueryable<Team>("teams")
            .Select(t => new Renamed { Title = t.Lead.Name, Years = t.Lead.Age })
            .ToArrayAsync(cancellationToken);

    /// <summary>A bare member, where the row is not an object at all.</summary>
    [GraphQLQuery]
    private static Task<string[]> JustNamesAsync(HttpClient client, CancellationToken cancellationToken)
        => client.CreateQueryable<Person>("people")
            .Select(p => p.Name)
            .ToArrayAsync(cancellationToken);

    /// <summary>A projection with a result operator, which shapes the one row it keeps.</summary>
    [GraphQLQuery]
    private static Task<Renamed> FirstRenamedAsync(HttpClient client, CancellationToken cancellationToken)
        => client.CreateQueryable<Person>("people")
            .Select(p => new Renamed { Title = p.Name, Years = p.Age })
            .FirstAsync(cancellationToken);

    /// <summary>A constructor rather than an initializer, which names nothing at all.</summary>
    [GraphQLQuery]
    private static Task<Pair[]> PairsAsync(HttpClient client, CancellationToken cancellationToken)
        => client.CreateQueryable<Person>("people")
            .Select(p => new Pair(p.Name, p.Age))
            .ToArrayAsync(cancellationToken);

    [Test]
    public async Task A_projection_narrows_the_selection_and_keeps_its_data()
    {
        var handler = new StubHandler(Rows);

        var names = await NamesAsync(handler.Client(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(handler.SentBody, Does.Contain("{ people { name } }"));
            Assert.That(names[0].Name, Is.EqualTo("Ada"));
        });
    }

    /// <summary>The case that used to be declined, and would have read as null if it were not.</summary>
    [Test]
    public async Task A_renamed_member_keeps_its_value()
    {
        var handler = new StubHandler(Rows);

        var rows = await RenamedAsync(handler.Client(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(handler.SentBody, Does.Contain("{ people { name age } }"));
            Assert.That(rows[0].Title, Is.EqualTo("Ada"));
            Assert.That(rows[0].Years, Is.EqualTo(36));
        });
    }

    [Test]
    public async Task A_member_reached_through_another_keeps_its_value()
    {
        var handler = new StubHandler(Teams);

        var rows = await LeadsAsync(handler.Client(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(handler.SentBody, Does.Contain("{ teams { lead { name age } } }"));
            Assert.That(rows[0].Title, Is.EqualTo("Ada"));
            Assert.That(rows[0].Years, Is.EqualTo(36));
        });
    }

    [Test]
    public async Task A_bare_member_projection_returns_the_member()
    {
        var handler = new StubHandler(Rows);

        var names = await JustNamesAsync(handler.Client(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(handler.SentBody, Does.Contain("{ people { name } }"));
            Assert.That(names, Is.EqualTo(new[] { "Ada" }));
        });
    }

    [Test]
    public async Task A_projection_with_a_result_operator_shapes_the_row_it_keeps()
    {
        var row = await FirstRenamedAsync(new StubHandler(Rows).Client(), CancellationToken.None);

        Assert.That(row.Title, Is.EqualTo("Ada"));
    }

    [Test]
    public async Task A_constructed_projection_keeps_its_arguments()
    {
        var rows = await PairsAsync(new StubHandler(Rows).Client(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(rows[0].Name, Is.EqualTo("Ada"));
            Assert.That(rows[0].Age, Is.EqualTo(36));
        });
    }

    /// <summary>
    /// Every projection above, composed instead of compiled, gives the same rows and posts the
    /// same bytes.
    /// </summary>
    /// <remarks>
    /// The assertion that makes the shaping trustworthy. A projection that compiled to something
    /// subtly different would return data that looks plausible, which no amount of care in the
    /// generator rules out — only comparing it with the runtime's own answer does.
    /// </remarks>
    [Test]
    public async Task A_compiled_projection_gives_what_the_runtime_gives()
    {
        var compiled = new StubHandler(Rows);

        var mine = await RenamedAsync(compiled.Client(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            // Frozen from the runtime translation while both paths still existed. When the runtime
            // path goes, this literal is what is left of the comparison — the bytes the two agreed
            // on, rather than a guess at what the compiler ought to emit.
            const string Agreed =
                "{\"query\":\"query { people { name age } }\"}";

            Assert.That(compiled.SentBody, Is.EqualTo(Agreed));

            // The shaped values, which the runtime half used to supply for comparison. Frozen the
            // same way and for the same reason: they are what the two paths agreed on.
            Assert.That(mine[0].Title, Is.EqualTo("Ada"));
            Assert.That(mine[0].Years, Is.EqualTo(36));
        });
    }

    // ---- a predicate written against the server's filter input -------------------------------

    /// <summary>
    /// A filter written against the server's own input model, compiled.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The shape a real schema forces: a reply's <c>continent</c> is an object with a name, while
    /// the filter input's <c>continent</c> takes a string filter directly. A predicate over the
    /// element would lower one level too deep, so it is written against a model of the input
    /// instead — and the argument the server calls it by travels with it.
    /// </para>
    /// <para>
    /// Nothing about that is opaque to the compiler. The predicate is still a member path and an
    /// operation; only the type it is written against differs, and neither the runtime's lowering
    /// nor the compiled one needs to know that type to walk it.
    /// </para>
    /// </remarks>
    [GraphQLQuery]
    private static Task<Person[]> InContinentAsync(
        HttpClient client, string continent, CancellationToken cancellationToken)
        => client.CreateQueryable<Person>("people")
            .Where("filter", (PersonFilter f) => f.City == continent)
            .ToArrayAsync(cancellationToken);

    [Test]
    public async Task A_filter_shape_predicate_is_compiled()
    {
        var handler = new StubHandler(Rows);

        var people = await InContinentAsync(handler.Client(), "London", CancellationToken.None);

        Assert.Multiple(() =>
        {
            // The argument the overload named, and the filter written from the model's own field.
            Assert.That(handler.SentBody,
                Does.Contain(@"query($v0: String) { people(filter: { city: { eq: $v0 } }) { name age } }"));

            Assert.That(handler.SentBody, Does.Contain(@"""variables"":{""v0"":""London""}"));
            Assert.That(people[0].Name, Is.EqualTo("Ada"));
        });
    }

    /// <summary>The unnamed overload, which takes the argument name the options give.</summary>
    [GraphQLQuery]
    private static Task<Person[]> InCityAsync(
        HttpClient client, string city, CancellationToken cancellationToken)
        => client.CreateQueryable<Person>("people")
            .Where((PersonFilter f) => f.City == city)
            .ToArrayAsync(cancellationToken);

    [Test]
    public async Task A_filter_shape_predicate_without_a_name_uses_the_default_argument()
    {
        var handler = new StubHandler(Rows);

        await InCityAsync(handler.Client(), "London", CancellationToken.None);

        Assert.That(handler.SentBody, Does.Contain("people(where: { city: { eq: $v0 } })"));
    }

    /// <summary>
    /// The comparison that makes it trustworthy: the same chain composed sends the same bytes.
    /// </summary>
    [Test]
    public async Task A_compiled_filter_shape_is_what_composing_it_would_have_sent()
    {
        var compiled = new StubHandler(Rows);

        await InContinentAsync(compiled.Client(), "London", CancellationToken.None);

        // A whole body, asserted byte for byte rather than by parts. It was frozen from the
        // runtime translation while both paths still existed; the filter has since moved out of
        // a variable and into the document, so the two no longer agree about it and what this
        // pins is the compiled output itself — nothing else asserts the envelope, the spacing
        // and the variable numbering all at once.
        const string Agreed =
            "{\"query\":\"query($v0: String) { people(filter: { city: { eq: $v0 } }) { name age } }\","
        + "\"variables\":{\"v0\":\"London\"}}";

        Assert.That(compiled.SentBody, Is.EqualTo(Agreed));
    }

    // ---- paged fields -------------------------------------------------------------------------

    /// <summary>
    /// An offset-paged field wraps its rows in <c>items</c>, which the compiled read unwraps.
    /// </summary>
    /// <remarks>
    /// Unwrapped in the reader rather than by declaring a connection type: a generated wrapper
    /// would need a contract in this assembly's own <c>JsonSerializerContext</c>, which its
    /// generator cannot write for a type another generator emitted.
    /// </remarks>
    [GraphQLQuery]
    private static Task<Person[]> PagedFieldAsync(
        HttpClient client, int min, CancellationToken cancellationToken)
        => client.CreateQueryable<Person>("people", o => o.Paging = PagingKind.Offset)
            .Where(p => p.Age > min)
            .ToArrayAsync(cancellationToken);

    /// <summary>A count asks the connection rather than the rows, and reads no rows at all.</summary>
    [GraphQLQuery]
    private static Task<int> HowManyAsync(
        HttpClient client, int min, CancellationToken cancellationToken)
        => client.CreateQueryable<Person>("people", o => o.Paging = PagingKind.Offset)
            .Where(p => p.Age > min)
            .CountAsync(cancellationToken);

    [Test]
    public async Task A_paged_field_is_read_out_of_its_wrapper()
    {
        var handler = new StubHandler("""{"data":{"people":{"items":[{"name":"Ada","age":36}]}}}""");

        var people = await PagedFieldAsync(handler.Client(), 30, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(handler.SentBody,
                Does.Contain("{ people(where: { age: { gt: $v0 } }) { items { name age } } }"));
            Assert.That(people[0].Name, Is.EqualTo("Ada"));
        });
    }

    [Test]
    public async Task A_count_reads_the_connections_own_field()
    {
        var handler = new StubHandler("""{"data":{"people":{"totalCount":42}}}""");

        int count = await HowManyAsync(handler.Client(), 30, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(handler.SentBody,
                Does.Contain("{ people(where: { age: { gt: $v0 } }) { totalCount } }"));
            Assert.That(count, Is.EqualTo(42));
        });
    }

    [Test]
    public async Task A_paged_field_posts_what_composing_it_would_have_posted()
    {
        var compiled = new StubHandler("""{"data":{"people":{"items":[]}}}""");

        await PagedFieldAsync(compiled.Client(), 30, CancellationToken.None);

        // A whole body, asserted byte for byte rather than by parts. It was frozen from the
        // runtime translation while both paths still existed; the filter has since moved out of
        // a variable and into the document, so the two no longer agree about it and what this
        // pins is the compiled output itself — nothing else asserts the envelope, the spacing
        // and the variable numbering all at once.
        const string Agreed =
            "{\"query\":\"query($v0: Int) { people(where: { age: { gt: $v0 } }) { items { name age } } }\","
        + "\"variables\":{\"v0\":30}}";

        Assert.That(compiled.SentBody, Is.EqualTo(Agreed));
    }

    // ---- the comparison that makes all of it trustworthy -------------------------------------

    /// <summary>
    /// Every shape above, composed instead of compiled, posts the same bytes.
    /// </summary>
    /// <remarks>
    /// The assertion that matters most in this file. The compiled path is a second implementation
    /// of the translator's output, and a second implementation that quietly disagrees asks the
    /// server for something other than what the chain said — which the server answers without
    /// complaint.
    /// </remarks>
    [Test]
    public async Task The_compiled_request_is_what_composing_the_chain_would_have_sent()
    {
        var compiled = new StubHandler(Rows);

        await EverythingAsync(compiled.Client(), 30, 5, CancellationToken.None);

        // Through a local, so the entry-point interceptor declines it too: this is the chain
        // translated end to end at run time.

        // A whole body, asserted byte for byte rather than by parts. It was frozen from the
        // runtime translation while both paths still existed; the filter has since moved out of
        // a variable and into the document, so the two no longer agree about it and what this
        // pins is the compiled output itself — nothing else asserts the envelope, the spacing
        // and the variable numbering all at once.
        const string Agreed =
            "{\"query\":\"query($v0: Int, $v1: [PersonSortInput!], $v2: Int) "
        + "{ people(where: { age: { gt: $v0 } }, order: $v1, take: $v2) { name age } }\","
        + "\"variables\":{\"v0\":30,\"v1\":[{\"name\":\"ASC\"}],\"v2\":5}}";

        Assert.That(compiled.SentBody, Is.EqualTo(Agreed));
    }

    [Test]
    public async Task The_compiled_ordering_is_what_composing_it_would_have_sent()
    {
        var compiled = new StubHandler(Rows);

        await OrderedDownAsync(compiled.Client(), CancellationToken.None);

        // Reported at the entry point, which is where the chain the analyzer judged begins.
#pragma warning disable FGQL012
#pragma warning restore FGQL012

        // A whole body, asserted byte for byte rather than by parts. It was frozen from the
        // runtime translation while both paths still existed; the filter has since moved out of
        // a variable and into the document, so the two no longer agree about it and what this
        // pins is the compiled output itself — nothing else asserts the envelope, the spacing
        // and the variable numbering all at once.
        const string Agreed =
            "{\"query\":\"query($v0: [PersonSortInput!]) { people(order: $v0) { name age } }\",\"variables\":{\"v0\":[{\"age\":\"DESC\"},{\"name\":\"ASC\"}]}}";

        Assert.That(compiled.SentBody, Is.EqualTo(Agreed));
    }
}

/// <summary>What a projection narrows a person to.</summary>
public class NameOnly
{
    public required string Name { get; init; }
}

/// <summary>
/// A projection target whose members are named nothing like the fields they come from.
/// </summary>
/// <remarks>
/// Read into by name this would be two nulls. It is the shape the compiled path has to get right
/// rather than decline, because it is what projecting is for.
/// </remarks>
public class Renamed
{
    public string Title { get; set; } = "";

    public int Years { get; set; }
}

/// <summary>A target with no settable members at all, built by its constructor.</summary>
public sealed record Pair(string Name, int Age);

/// <summary>An element with a nested object, so a projection has something to reach through.</summary>
public class Team
{
    public required Person Lead { get; init; }
}

/// <summary>
/// A model of the server's filter input, whose shape is not the element's.
/// </summary>
/// <remarks>
/// <c>Person.Home</c> is an object with a city; the filter input takes the city directly. That
/// mismatch is what the filter-shape overloads exist for.
/// </remarks>
public class PersonFilter
{
    public string? City { get; set; }
}
