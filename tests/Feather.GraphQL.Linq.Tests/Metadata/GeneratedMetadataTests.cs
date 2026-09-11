using System.Reflection;
using Feather.GraphQL.Linq.Metadata;
using Feather.GraphQL.Linq.Tests.Query;

namespace Feather.GraphQL.Linq.Tests.Metadata;

/// <summary>
/// The generated field tables must be indistinguishable from the reflection they replace, and
/// must be what the translator actually finds.
/// </summary>
/// <remarks>
/// Parity is the whole contract. A table that merely looks plausible would change query text in
/// ways no other test would notice — a missing inherited property silently drops a field from a
/// selection set, and the query still runs.
/// </remarks>
[TestFixture]
public class GeneratedMetadataTests
{
    /// <summary>
    /// The rule the reflection fallback applies, written out — so the comparison is against the
    /// rule rather than against a copy of the generator's own output.
    /// </summary>
    private static (string Clr, string Field, Type Type, bool Ignored)[] ByReflection(Type type)
    {
        var fields = new List<(string, string, Type, bool)>();

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0)
                continue;

            string name =
                property.GetCustomAttribute<System.Text.Json.Serialization.JsonPropertyNameAttribute>()
                    is { Name.Length: > 0 } json
                    ? json.Name
                    : property.GetCustomAttribute<System.Runtime.Serialization.DataMemberAttribute>()
                        is { Name.Length: > 0 } member
                        ? member.Name!
                        : char.IsUpper(property.Name[0])
                            ? char.ToLowerInvariant(property.Name[0]) + property.Name[1..]
                            : property.Name;

            bool ignored = property
                .GetCustomAttribute<System.Text.Json.Serialization.JsonIgnoreAttribute>() is not null;

            fields.Add((property.Name, name, property.PropertyType, ignored));
        }

        return [.. fields];
    }

    private static IGraphQLTypeMetadata Generated(Type type)
    {
        Assert.That(GraphQLTypeMetadataRegistry.TryGet(type, out var metadata), Is.True,
            $"no metadata was registered for '{type.Name}' — the generator did not see it");

        return metadata!;
    }

    [TestCase(typeof(Person))]
    [TestCase(typeof(Author))]
    [TestCase(typeof(Gadget))]
    [TestCase(typeof(Country))]
    [TestCase(typeof(CursorPerson))]
    public void A_generated_table_matches_what_reflection_would_build(Type type)
    {
        var generated = Generated(type).Fields
            .Select(f => (f.ClrName, f.FieldName, f.ClrType, f.IsIgnored))
            .OrderBy(f => f.ClrName, StringComparer.Ordinal)
            .ToArray();

        var reflected = ByReflection(type)
            .Select(f => (f.Clr, f.Field, f.Type, f.Ignored))
            .OrderBy(f => f.Clr, StringComparer.Ordinal)
            .ToArray();

        Assert.That(generated, Is.EqualTo(reflected));
    }

    /// <summary>
    /// The point of the exercise: the table the translator finds is the generated one, not a
    /// reflected one cached under the same key.
    /// </summary>
    [TestCase(typeof(Person))]
    [TestCase(typeof(Country))]
    [TestCase(typeof(Gadget))]
    public void The_registered_table_is_the_generated_one(Type type)
        => Assert.That(Generated(type).GetType().Assembly,
            Is.EqualTo(typeof(GeneratedMetadataTests).Assembly),
            "metadata came from the Linq assembly, so it was built by reflection");

    /// <summary>Types reached only through a member are generated too, not just query roots.</summary>
    [Test]
    public void A_nested_type_is_generated_from_its_parent()
        => Assert.That(Generated(typeof(CountryContinent)).Fields.Select(f => f.FieldName),
            Does.Contain("name"));

    /// <summary><c>[JsonIgnore]</c> members stay in the table, flagged — the translator reports them.</summary>
    [Test]
    public void An_ignored_member_is_present_and_flagged()
    {
        Assert.That(Generated(typeof(Person)).TryGetField("Secret", out var field), Is.True);
        Assert.That(field!.IsIgnored, Is.True);
    }

    [Test]
    public void A_renamed_member_keeps_its_JsonPropertyName()
    {
        Assert.That(Generated(typeof(Person)).TryGetField("Email", out var field), Is.True);
        Assert.That(field!.FieldName, Is.EqualTo("emailAddress"));
    }
}
