namespace Feather.GraphQL.Linq.Tests.Where;

/// <summary><c>eq</c> and <c>neq</c>.</summary>
[TestFixture]
public class EqualityTests
{
    [Test]
    public void Equality_lowers_to_eq()
        => Assert.That(Lower.Where(p => p.Name == "John"),
            Is.EqualTo("""{"name":{"eq":"John"}}"""));

    [Test]
    public void Inequality_lowers_to_neq()
        => Assert.That(Lower.Where(p => p.Name != "John"),
            Is.EqualTo("""{"name":{"neq":"John"}}"""));

    [Test]
    public void Negating_an_equality_lowers_to_neq()
        => Assert.That(Lower.Where(p => !(p.Name == "John")),
            Is.EqualTo("""{"name":{"neq":"John"}}"""));

    [Test]
    public void Null_comparison_lowers_to_a_null_value()
        => Assert.That(Lower.Where(p => p.Email == null),
            Is.EqualTo("""{"emailAddress":{"eq":null}}"""));

    [Test]
    public void Bare_boolean_member_lowers_to_eq_true()
        => Assert.That(Lower.Where(p => p.IsActive),
            Is.EqualTo("""{"isActive":{"eq":true}}"""));

    [Test]
    public void Negated_boolean_member_lowers_to_eq_false()
        => Assert.That(Lower.Where(p => !p.IsActive),
            Is.EqualTo("""{"isActive":{"eq":false}}"""));
}
