namespace Feather.GraphQL.Linq.Tests.Where;

/// <summary><c>contains</c>, <c>startsWith</c>, <c>endsWith</c> and their negated forms.</summary>
[TestFixture]
public class StringOperationTests
{
    [Test]
    public void Contains_lowers_to_contains()
        => Assert.That(Lower.Where(p => p.Name.Contains("oh")),
            Is.EqualTo("""{"name":{"contains":"oh"}}"""));

    [Test]
    public void StartsWith_lowers_to_startsWith()
        => Assert.That(Lower.Where(p => p.Name.StartsWith("Jo")),
            Is.EqualTo("""{"name":{"startsWith":"Jo"}}"""));

    [Test]
    public void EndsWith_lowers_to_endsWith()
        => Assert.That(Lower.Where(p => p.Name.EndsWith("hn")),
            Is.EqualTo("""{"name":{"endsWith":"hn"}}"""));

    [Test]
    public void Negated_contains_lowers_to_ncontains()
        => Assert.That(Lower.Where(p => !p.Name.Contains("oh")),
            Is.EqualTo("""{"name":{"ncontains":"oh"}}"""));

    [Test]
    public void Negated_startsWith_lowers_to_nstartsWith()
        => Assert.That(Lower.Where(p => !p.Name.StartsWith("Jo")),
            Is.EqualTo("""{"name":{"nstartsWith":"Jo"}}"""));
}
