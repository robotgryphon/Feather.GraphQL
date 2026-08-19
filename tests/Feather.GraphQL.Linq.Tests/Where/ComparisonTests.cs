namespace Feather.GraphQL.Linq.Tests.Where;

/// <summary><c>gt</c>, <c>gte</c>, <c>lt</c>, <c>lte</c>.</summary>
[TestFixture]
public class ComparisonTests
{
    [Test]
    public void GreaterThan_lowers_to_gt()
        => Assert.That(Lower.Where(p => p.Age > 30), Is.EqualTo("""{"age":{"gt":30}}"""));

    [Test]
    public void GreaterThanOrEqual_lowers_to_gte()
        => Assert.That(Lower.Where(p => p.Age >= 30), Is.EqualTo("""{"age":{"gte":30}}"""));

    [Test]
    public void LessThan_lowers_to_lt()
        => Assert.That(Lower.Where(p => p.Age < 30), Is.EqualTo("""{"age":{"lt":30}}"""));

    [Test]
    public void LessThanOrEqual_lowers_to_lte()
        => Assert.That(Lower.Where(p => p.Age <= 30), Is.EqualTo("""{"age":{"lte":30}}"""));

    /// <summary>The member may sit on either side: <c>30 &lt; p.Age</c> means <c>p.Age &gt; 30</c>.</summary>
    [Test]
    public void Reversed_operands_flip_the_operation()
        => Assert.That(Lower.Where(p => 30 < p.Age), Is.EqualTo("""{"age":{"gt":30}}"""));

    /// <summary>Negation inverts the comparison rather than needing a negated operation name.</summary>
    [Test]
    public void Negated_comparison_inverts_the_operation()
        => Assert.That(Lower.Where(p => !(p.Age > 30)), Is.EqualTo("""{"age":{"lte":30}}"""));
}
