using Feather.GraphQL.Linq.Tests.Where;

namespace Feather.GraphQL.Linq.Tests.Filtering;

/// <summary>
/// §4.2: attributed types resolve through generated metadata, un-attributed types through
/// reflection. The two paths must agree, or a filter would mean different things depending on
/// whether the caller happened to own the type.
/// </summary>
[TestFixture]
public class MetadataParityTests
{
    [Test]
    public void Attributed_and_unattributed_types_lower_identically()
    {
        string unattributed = Lower.Of(Array.Empty<Person>().AsQueryable()
            .Where(p => p.Name == "John" && p.Age > 30 && p.Email == "a@b.c")
            .Expression);

        string attributed = Lower.Of(Array.Empty<AttributedPerson>().AsQueryable()
            .Where(p => p.Name == "John" && p.Age > 30 && p.Email == "a@b.c")
            .Expression);

        Assert.That(attributed, Is.EqualTo(unattributed));
    }
}
