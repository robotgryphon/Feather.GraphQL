using System.Linq.Expressions;
using Feather.GraphQL.Linq.Metadata;
using Feather.GraphQL.Linq.Query;
using Feather.GraphQL.Linq.Tests.Query;

namespace Feather.GraphQL.Linq.Tests.Execution;

/// <summary>
/// The generated shaper and the compiled lambda are two implementations of one projection. This
/// runs both over the same input and requires them to agree.
/// </summary>
/// <remarks>
/// <para>
/// Two things can drift independently, and they fail differently. The <em>key</em> is rendered
/// from syntax by the generator and derived from the expression tree at runtime; if those
/// disagree the shaper is never found, which costs speed and nothing else. The <em>body</em> is
/// the projection rewritten as C#; if that disagrees — a member transposed, a name bound to the
/// wrong path — the query returns wrong data and no other test notices.
/// </para>
/// <para>
/// So both are asserted: the key must resolve, and the shaper's output must equal what compiling
/// the lambda produces. Anonymous types compare structurally, which is what makes the second
/// assertion meaningful rather than a reference check.
/// </para>
/// </remarks>
[TestFixture]
public class ProjectionDifferentialTests
{
    private static readonly Person _person = new()
    {
        Name = "Ada", Age = 36, Email = "ada@example.test", Secret = "hidden"
    };

    private static readonly Country _country = new()
    {
        Name = "France", Continent = new CountryContinent { Name = "Europe" }
    };

    /// <summary>
    /// Runs one projection both ways. The chain is passed in already built, because the generator
    /// only shapes a chain written as a single visible expression.
    /// </summary>
    private static void Agrees<T>(IQueryable<T> chain, object source)
    {
        var projection = chain.ToQueryPlan().Projection;

        Assert.That(projection, Is.Not.Null, "the chain carried no projection");

        var shaper = GraphQLProjectionRegistry.Find(projection!);

        Assert.That(shaper, Is.Not.Null,
            "no shaper matched — the key from syntax and the key from the expression tree disagree");

        object? generated = shaper!(source);
        object? compiled = projection!.Compile().DynamicInvoke(source);

        Assert.That(generated, Is.EqualTo(compiled),
            "the generated shaper and the compiled lambda produced different values");
    }

    [Test]
    public void Two_members_agree()
        => Agrees(
            GraphQLQueryable.For<Person>("people").Where(p => p.Age > 30)
                .Select(p => new { p.Name, p.Email }),
            _person);

    [Test]
    public void A_single_member_agrees()
        => Agrees(
            GraphQLQueryable.For<Person>("people").Where(p => p.Age > 30)
                .Select(p => new { p.Name }),
            _person);

    /// <summary>Explicit and inferred names in one projection — the two ways a name is bound.</summary>
    [Test]
    public void Mixed_explicit_and_inferred_names_agree()
        => Agrees(
            GraphQLQueryable.For<Person>("people").Where(p => p.Age > 30)
                .Select(p => new { Renamed = p.Name, p.Age }),
            _person);

    /// <summary>
    /// Order is part of an anonymous type's identity, so a transposition would produce a
    /// different type — and the shaper would not even cast.
    /// </summary>
    [Test]
    public void Member_order_agrees()
        => Agrees(
            GraphQLQueryable.For<Person>("people").Where(p => p.Age > 30)
                .Select(p => new { p.Age, p.Name }),
            _person);

    [Test]
    public void A_nested_path_agrees()
        => Agrees(
            GraphQLQueryable.For<Country>("countries").Where(c => c.Name == "France")
                .Select(c => new { c.Name, Continent = c.Continent.Name }),
            _country);

    [Test]
    public void A_bare_member_agrees()
        => Agrees(
            GraphQLQueryable.For<Person>("people").Where(p => p.Age > 30).Select(p => p.Age),
            _person);

    [Test]
    public void A_bare_nested_member_agrees()
        => Agrees(
            GraphQLQueryable.For<Country>("countries").Where(c => c.Name == "France")
                .Select(c => c.Continent.Name),
            _country);

    /// <summary>
    /// The generator renders a projection; it does not re-judge one. A <c>[JsonIgnore]</c> member
    /// is refused by the translator, so the shaper emitted for it is simply never reachable — and
    /// the refusal, not the shaper, is what the caller sees.
    /// </summary>
    /// <remarks>
    /// Worth pinning: it is the boundary that keeps the two implementations from disagreeing
    /// about what is <em>legal</em>. The translator stays the single authority on that, and the
    /// generator only has to agree about what a legal projection <em>means</em>.
    /// </remarks>
    [Test]
    public void The_translator_remains_the_authority_on_what_is_legal()
    {
        var exception = Assert.Throws<GraphQLTranslationException>(
            () => GraphQLQueryable.For<Person>("people").Where(p => p.Age > 30)
                .Select(p => new { p.Name, p.Secret })
                .ToQueryPlan());

        Assert.That(exception!.DiagnosticId, Is.EqualTo("FGQL005"));
    }

    /// <summary>
    /// The corpus above is only meaningful if a mismatch would actually fail. A shaper registered
    /// under a real key, returning the wrong shape, must be caught.
    /// </summary>
    [Test]
    public void A_wrong_shaper_would_be_caught()
    {
        var projection = GraphQLQueryable.For<Person>("people").Where(p => p.Age > 30)
            .Select(p => new { p.Name, p.Email })
            .ToQueryPlan().Projection!;

        object? compiled = projection.Compile().DynamicInvoke(_person);
        object? wrong = new { Name = _person.Email, Email = _person.Name };

        Assert.That(wrong, Is.Not.EqualTo(compiled));
    }
}
