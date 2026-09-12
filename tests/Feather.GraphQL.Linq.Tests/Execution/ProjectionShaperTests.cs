using Feather.GraphQL.Linq.Metadata;
using Feather.GraphQL.Linq.Execution;
using Feather.GraphQL.Linq.Query;
using Feather.GraphQL.Linq.Tests.Query;

namespace Feather.GraphQL.Linq.Tests.Execution;

/// <summary>
/// A projection written in one visible chain is compiled at build time, not at run time.
/// </summary>
/// <remarks>
/// Every chain here is a single expression starting at <c>GraphQLQueryable.For</c>, because that
/// is the condition for a shaper to exist at all — the generator has to see where the queryable
/// came from, exactly as the analyzer does.
/// </remarks>
[TestFixture]
public class ProjectionShaperTests
{
    [Test]
    public void An_anonymous_projection_is_shaped_by_generated_code()
    {
        var executor = StubExecutor.Returning(
            """{"people":[{"name":"Ada","emailAddress":"ada@example.test"}]}""");

        var rows = GraphQLQueryable.For<Person>(executor, "people")
            .Where(p => p.Age > 30)
            .Select(p => new { p.Name, p.Email })
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(rows[0].Name, Is.EqualTo("Ada"));
            Assert.That(rows[0].Email, Is.EqualTo("ada@example.test"));
        });
    }

    /// <summary>A renamed member, and a path through a nested object.</summary>
    [Test]
    public void A_nested_path_is_shaped()
    {
        var executor = StubExecutor.Returning(
            """{"countries":[{"name":"France","continent":{"name":"Europe"}}]}""");

        var rows = GraphQLQueryable.For<Country>(executor, "countries")
            .Where(c => c.Name == "France")
            .Select(c => new { c.Name, Continent = c.Continent.Name })
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(rows[0].Name, Is.EqualTo("France"));
            Assert.That(rows[0].Continent, Is.EqualTo("Europe"));
        });
    }

    [Test]
    public void A_bare_member_projection_is_shaped()
    {
        var executor = StubExecutor.Returning("""{"people":[{"age":36},{"age":45}]}""");

        var ages = GraphQLQueryable.For<Person>(executor, "people")
            .Where(p => p.Age > 30)
            .Select(p => p.Age)
            .ToArray();

        Assert.That(ages, Is.EqualTo(new[] { 36, 45 }));
    }

    /// <summary>
    /// The rows above prove the result is right, which the compiled fallback would also manage.
    /// This proves the generated shaper is the thing that produced them: the key derived from the
    /// runtime expression tree has to find what the generator registered from the syntax.
    /// </summary>
    [Test]
    public void The_runtime_key_finds_the_generated_shaper()
    {
        var plan = GraphQLQueryable.For<Person>("people")
            .Where(p => p.Age > 30)
            .Select(p => new { p.Name, p.Email })
            .ToQueryPlan();

        Assert.That(GraphQLProjectionRegistry.Find(plan.Projection!), Is.Not.Null,
            "the key built from the expression tree did not match the one built from syntax");
    }

    [Test]
    public void A_bare_member_key_matches_too()
    {
        var plan = GraphQLQueryable.For<Person>("people")
            .Where(p => p.Age > 30)
            .Select(p => p.Age)
            .ToQueryPlan();

        Assert.That(GraphQLProjectionRegistry.Find(plan.Projection!), Is.Not.Null);
    }

    /// <summary>
    /// A projection into a type of one's own is shaped too, which is what most of them are.
    /// </summary>
    /// <remarks>
    /// It used to be declined, and declining cost more than it looked: the fallback compiles the
    /// lambda with <c>Expression.Compile</c> on <em>every</em> execution, so a chain projecting
    /// into a named type paid tens of microseconds a call, forever.
    /// </remarks>
    [Test]
    public void A_named_type_projection_finds_its_shaper()
    {
        var plan = GraphQLQueryable.For<Person>("people")
            .Where(p => p.Age > 30)
            .Select(p => new Dimensions { Minimum = p.Name, Maximum = p.Name })
            .ToQueryPlan();

        Assert.That(GraphQLProjectionRegistry.Find(plan.Projection!), Is.Not.Null,
            "the key built from the expression tree did not match the one built from syntax");
    }

    /// <summary>The same, filling a member from another object built the same way.</summary>
    /// <remarks>
    /// The shape a projection takes when it rebuilds a nested object rather than flattening it,
    /// which is where the renderer has to recurse instead of only ever taking a path.
    /// </remarks>
    [Test]
    public void A_nested_named_type_projection_finds_its_shaper()
    {
        var plan = GraphQLQueryable.For<Person>("people")
            .Select(p => new Nested { Name = p.Name, Size = new Bounds { Minimum = p.Name } })
            .ToQueryPlan();

        Assert.That(GraphQLProjectionRegistry.Find(plan.Projection!), Is.Not.Null);
    }

    /// <summary>
    /// Two types filled with the same members are two shapes, and must not share a shaper.
    /// </summary>
    /// <remarks>
    /// What the type in the key is for. An anonymous type needs no name — matching members make it
    /// the same type by construction — but <c>new A { X = p.Name }</c> and <c>new B { X = p.Name }</c>
    /// build different things, and a shaper for one would hand back the other.
    /// </remarks>
    [Test]
    public void Two_types_with_the_same_members_do_not_share_a_shaper()
    {
        var first = GraphQLQueryable.For<Person>("people")
            .Select(p => new Dimensions { Minimum = p.Name, Maximum = p.Name })
            .ToQueryPlan();

        var second = GraphQLQueryable.For<Person>("people")
            .Select(p => new Bounds { Minimum = p.Name, Maximum = p.Name })
            .ToQueryPlan();

        Assert.Multiple(() =>
        {
            var shaped = GraphQLProjectionRegistry.Find(first.Projection!)!(new Person { Name = "Ada" });
            var other = GraphQLProjectionRegistry.Find(second.Projection!)!(new Person { Name = "Ada" });

            Assert.That(shaped, Is.TypeOf<Dimensions>());
            Assert.That(other, Is.TypeOf<Bounds>());
        });
    }

    /// <summary>
    /// A shape the generator declines must also fail to match, rather than matching the wrong
    /// shaper — the failure mode that would corrupt data instead of merely slowing it down.
    /// </summary>
    /// <remarks>
    /// A constructor takes its arguments by position, so the members they fill have no names to
    /// key on. It is declined rather than guessed at.
    /// </remarks>
    [Test]
    public void A_declined_projection_finds_no_shaper()
    {
        var plan = GraphQLQueryable.For<Person>("people")
            .Where(p => p.Age > 30)
            .Select(p => new Named(p.Name))
            .ToQueryPlan();

        Assert.That(GraphQLProjectionRegistry.Find(plan.Projection!), Is.Null);
    }

    [Test]
    public void Shapers_were_generated_for_this_assembly()
        => Assert.That(GraphQLProjectionRegistry.Count, Is.GreaterThan(0),
            "no shaper was generated — every projection is still being compiled at runtime");

    /// <summary>
    /// A projection the generator declines still runs. Nothing has to be shaped for the library
    /// to work; a missing shaper costs speed and nothing else.
    /// </summary>
    [Test]
    public void An_unshaped_projection_still_materializes()
    {
        var executor = StubExecutor.Returning("""{"people":[{"name":"Ada"}]}""");

        var rows = executor.Queryable<Person>("people")
            .Where(p => p.Age > 30)
            .Select(p => new { p.Name })
            .ToArray();

        Assert.That(rows[0].Name, Is.EqualTo("Ada"));
    }
}

/// <summary>A second type with the same members, to prove a shaper is not shared across them.</summary>
public class Bounds
{
    public string Minimum { get; set; } = "";

    public string Maximum { get; set; } = "";
}

/// <summary>A type filled by its constructor, which names nothing a key could use.</summary>
public sealed record Named(string Name);

/// <summary>A target whose own member is another target, for the recursive case.</summary>
public class Nested
{
    public string Name { get; set; } = "";

    public Bounds Size { get; set; } = new();
}
