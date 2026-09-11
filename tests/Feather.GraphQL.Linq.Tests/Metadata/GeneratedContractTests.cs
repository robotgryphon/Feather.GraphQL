using System.Text.Json.Serialization.Metadata;
using Feather.GraphQL.Metadata;
using Feather.GraphQL.Linq.Tests.Execution;
using Feather.GraphQL.Linq.Tests.Query;

namespace Feather.GraphQL.Linq.Tests.Metadata;

/// <summary>
/// Results are materialized through a source-generated contract when one covers the type, and
/// through reflection only when none does.
/// </summary>
[TestFixture]
public class GeneratedContractTests
{
    private static JsonTypeInfo Contract(Type type)
        => GraphQLJsonContextRegistry.Options.GetTypeInfo(type);

    /// <summary>
    /// The contract's origin is the proof. A reflected contract deserializes identically, so
    /// only asking where it came from distinguishes the two.
    /// </summary>
    [TestCase(typeof(Person))]
    [TestCase(typeof(Gadget))]
    [TestCase(typeof(Country))]
    [TestCase(typeof(CountryContinent))]
    public void A_covered_type_resolves_to_the_generated_contract(Type type)
        => Assert.That(Contract(type).OriginatingResolver, Is.InstanceOf<QuerySerializerContext>(),
            $"'{type.Name}' was built by reflection, not by the registered context");

    /// <summary>A type no context covers still materializes — that is what the fallback is for.</summary>
    [Test]
    public void An_uncovered_type_falls_back_to_reflection()
        => Assert.That(Contract(typeof(Author)).OriginatingResolver,
            Is.Not.InstanceOf<QuerySerializerContext>());

    /// <summary>
    /// The modifier has to reach generated contracts too, or a projection over a type with a
    /// <c>required</c> member would fail where it used to work.
    /// </summary>
    [Test]
    public void Required_members_are_relaxed_on_a_generated_contract()
    {
        var contract = Contract(typeof(Person));

        Assert.That(contract.Properties.Where(p => p.IsRequired), Is.Empty);
    }

    /// <summary>Materializing through a generated contract produces the same rows as before.</summary>
    [Test]
    public void A_generated_contract_materializes_the_same_rows()
    {
        var executor = StubExecutor.Returning(
            """{"people":[{"name":"Ada","age":36,"emailAddress":"ada@example.test"}]}""");

        var people = executor.Queryable<Person>("people").Where(p => p.Age > 30).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(people[0].Name, Is.EqualTo("Ada"));
            Assert.That(people[0].Age, Is.EqualTo(36));
            Assert.That(people[0].Email, Is.EqualTo("ada@example.test"));
        });
    }

    /// <summary>A subset projection still works, which is what the relaxed contract buys.</summary>
    [Test]
    public void A_subset_projection_materializes_through_a_generated_contract()
    {
        var executor = StubExecutor.Returning("""{"people":[{"age":36},{"age":45}]}""");

        var ages = executor.Queryable<Person>("people")
            .Where(p => p.Age > 30)
            .Select(p => p.Age)
            .ToArray();

        Assert.That(ages, Is.EqualTo(new[] { 36, 45 }));
    }
}
