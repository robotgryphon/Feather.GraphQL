namespace Feather.GraphQL.Linq.Tests.Where;

/// <summary><c>some</c>, <c>all</c>, <c>none</c> over collection members.</summary>
[TestFixture]
public class QuantifierTests
{
    [Test]
    public void Any_with_a_predicate_lowers_to_some()
        => Assert.That(Lower.Where(p => p.Tags.Any(t => t.Name == "vip")),
            Is.EqualTo("""{"tags":{"some":{"name":{"eq":"vip"}}}}"""));

    [Test]
    public void All_lowers_to_all()
        => Assert.That(Lower.Where(p => p.Tags.All(t => t.Weight > 0)),
            Is.EqualTo("""{"tags":{"all":{"weight":{"gt":0}}}}"""));

    /// <summary><c>none</c> is a distinct operation, so the negation is consumed here rather than
    /// pushed into the quantified body.</summary>
    [Test]
    public void Negated_any_lowers_to_none()
        => Assert.That(Lower.Where(p => !p.Tags.Any(t => t.Name == "vip")),
            Is.EqualTo("""{"tags":{"none":{"name":{"eq":"vip"}}}}"""));

    [Test]
    public void Any_without_a_predicate_asks_for_non_empty()
        => Assert.That(Lower.Where(p => p.Tags.Any()),
            Is.EqualTo("""{"tags":{"some":{}}}"""));

    [Test]
    public void Member_collection_containing_a_constant_lowers_to_some_eq()
        => Assert.That(Lower.Where(p => p.Nicknames.Contains("Jonny")),
            Is.EqualTo("""{"nicknames":{"some":{"eq":"Jonny"}}}"""));
}
