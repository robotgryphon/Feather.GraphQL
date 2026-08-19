namespace Feather.GraphQL.Linq.Tests.Where;

/// <summary>
/// How <c>&amp;&amp;</c> and <c>||</c> combine: object merge where they can, <c>and:</c>/<c>or:</c>
/// arrays where they cannot.
/// </summary>
[TestFixture]
public class JunctionTests
{
    [Test]
    public void And_across_two_fields_merges_into_one_object()
        => Assert.That(Lower.Where(p => p.Name == "John" && p.Age > 30),
            Is.EqualTo("""{"name":{"eq":"John"},"age":{"gt":30}}"""));

    /// <summary>
    /// Distinct operations on one field share its operation object — HotChocolate ANDs the set
    /// fields of a comparable filter input, so this is both valid and the idiomatic form.
    /// </summary>
    [Test]
    public void Distinct_operations_on_one_field_share_its_object()
        => Assert.That(Lower.Where(p => p.Age > 18 && p.Age < 65),
            Is.EqualTo("""{"age":{"gt":18,"lt":65}}"""));

    /// <summary>The same operation twice genuinely collides, and needs an explicit and: array.</summary>
    [Test]
    public void Repeating_one_operation_falls_back_to_an_and_array()
        => Assert.That(Lower.Where(p => p.Age > 18 && p.Age > 20),
            Is.EqualTo("""{"and":[{"age":{"gt":18}},{"age":{"gt":20}}]}"""));

    [Test]
    public void Multiple_where_calls_merge_as_and()
        => Assert.That(Lower.Chain(q => q.Where(p => p.Name == "John").Where(p => p.Age > 30)),
            Is.EqualTo("""{"name":{"eq":"John"},"age":{"gt":30}}"""));

    [Test]
    public void Or_across_different_fields_stays_an_or()
        => Assert.That(Lower.Where(p => p.Age == 1 || p.Name == "John"),
            Is.EqualTo("""{"or":[{"age":{"eq":1}},{"name":{"eq":"John"}}]}"""));

    [Test]
    public void Or_of_different_operations_stays_an_or()
        => Assert.That(Lower.Where(p => p.Age == 1 || p.Age > 5),
            Is.EqualTo("""{"or":[{"age":{"eq":1}},{"age":{"gt":5}}]}"""));

    /// <summary>De Morgan: negation is pushed to the leaves rather than wrapped in a not:.</summary>
    [Test]
    public void Negated_and_becomes_or_of_negations()
        => Assert.That(Lower.Where(p => !(p.Name == "John" && p.Age > 30)),
            Is.EqualTo("""{"or":[{"name":{"neq":"John"}},{"age":{"lte":30}}]}"""));
}
