namespace Feather.GraphQL.Linq.Tests.Where;

/// <summary>How CLR values reach the filter payload.</summary>
[TestFixture]
public class ValueConversionTests
{
    [Test]
    public void Captured_locals_are_evaluated_into_values()
    {
        string name = "John";
        int minimum = 30;

        Assert.That(Lower.Where(p => p.Name == name && p.Age > minimum),
            Is.EqualTo("""{"name":{"eq":"John"},"age":{"gt":30}}"""));
    }

    /// <summary>
    /// The compiler lowers an enum comparison to an int comparison, so the enum identity survives
    /// only via the member's declared type.
    /// </summary>
    [Test]
    public void Enum_values_lower_to_constant_case()
        => Assert.That(Lower.Where(p => p.Status == Status.PendingReview),
            Is.EqualTo("""{"status":{"eq":"PENDING_REVIEW"}}"""));

    [Test]
    public void Dates_lower_to_iso_strings()
        => Assert.That(Lower.Where(p => p.Birthday > new DateOnly(1990, 1, 2)),
            Is.EqualTo("""{"birthday":{"gt":"1990-01-02"}}"""));
}
