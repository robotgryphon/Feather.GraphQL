namespace Feather.GraphQL.Linq.Tests.Where;

/// <summary>
/// <c>in</c> and <c>nin</c> — both the direct <c>Contains</c> form and the junction collapse
/// described in §4.3.
/// </summary>
[TestFixture]
public class SetOperationTests
{
    [Test]
    public void Constant_collection_containing_a_member_lowers_to_in()
    {
        string[] names = ["John", "Jane"];
        Assert.That(Lower.Where(p => names.Contains(p.Name)),
            Is.EqualTo("""{"name":{"in":["John","Jane"]}}"""));
    }

    [Test]
    public void Or_of_equalities_on_one_field_collapses_to_in()
        => Assert.That(Lower.Where(p => p.Age == 1 || p.Age == 2 || p.Age == 3),
            Is.EqualTo("""{"age":{"in":[1,2,3]}}"""));

    [Test]
    public void Two_equalities_collapse_to_in_as_well()
        => Assert.That(Lower.Where(p => p.Name == "John" || p.Name == "Jane"),
            Is.EqualTo("""{"name":{"in":["John","Jane"]}}"""));

    [Test]
    public void Collapse_follows_nested_member_paths()
        => Assert.That(Lower.Where(p => p.Address!.City == "Leeds" || p.Address.City == "York"),
            Is.EqualTo("""{"address":{"city":{"in":["Leeds","York"]}}}"""));

    /// <summary>The mirror image: negating a disjunction gives and-of-neq, which becomes nin.</summary>
    [Test]
    public void Negated_or_of_equalities_collapses_to_nin()
        => Assert.That(Lower.Where(p => !(p.Age == 1 || p.Age == 2)),
            Is.EqualTo("""{"age":{"nin":[1,2]}}"""));

    [Test]
    public void Collapse_reaches_inside_a_quantifier()
        => Assert.That(Lower.Where(p => p.Tags.Any(t => t.Name == "vip" || t.Name == "staff")),
            Is.EqualTo("""{"tags":{"some":{"name":{"in":["vip","staff"]}}}}"""));
}
