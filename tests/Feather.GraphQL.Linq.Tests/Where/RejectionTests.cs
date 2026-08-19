namespace Feather.GraphQL.Linq.Tests.Where;

/// <summary>
/// Decision #2: an untranslatable predicate throws rather than emitting something plausible or
/// quietly falling back to in-memory evaluation.
/// </summary>
[TestFixture]
public class RejectionTests
{
    private static string ThrowsWith(TestDelegate action)
        => Assert.Throws<GraphQLTranslationException>(action)!.DiagnosticId;

    [Test]
    public void Arbitrary_method_call_in_a_predicate_is_FGQL002()
        => Assert.That(ThrowsWith(() => Lower.Where(p => p.Name.ToUpperInvariant() == "JOHN")),
            Is.EqualTo("FGQL002"));

    [Test]
    public void Comparing_two_members_is_rejected()
        => Assert.That(ThrowsWith(() => Lower.Where(p => p.Name == p.Secret)),
            Is.AnyOf("FGQL002", "FGQL003", "FGQL005"));

    [Test]
    public void Referencing_a_JsonIgnore_member_is_FGQL005()
        => Assert.That(ThrowsWith(() => Lower.Where(p => p.Secret == "x")),
            Is.EqualTo("FGQL005"));

    [Test]
    public void Unsupported_operator_is_FGQL001()
        => Assert.That(ThrowsWith(() => Lower.Chain(q => q.Distinct().Where(p => p.Age > 1))),
            Is.EqualTo("FGQL001"));

    /// <summary>
    /// <c>none</c> means "no element matches", which is not the negation of "every element
    /// matches". Guessing here would silently change the query's meaning.
    /// </summary>
    [Test]
    public void Negated_All_is_rejected_rather_than_guessed()
        => Assert.That(ThrowsWith(() => Lower.Where(p => !p.Tags.All(t => t.Weight > 0))),
            Is.EqualTo("FGQL002"));
}
