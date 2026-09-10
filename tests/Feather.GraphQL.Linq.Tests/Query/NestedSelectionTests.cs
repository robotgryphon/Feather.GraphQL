using Feather.GraphQL.Linq.Query;

namespace Feather.GraphQL.Linq.Tests.Query;

/// <summary>
/// What a projection may say about nested fields: LINQ chains over a collection member, and
/// object members named without a projection of their own.
/// </summary>
[TestFixture]
public class NestedSelectionTests
{
    private static string ThrowsWith(TestDelegate action)
        => Assert.Throws<GraphQLTranslationException>(action)!.DiagnosticId;

    /// <summary>
    /// The materializing call is C# needing an array, not GraphQL needing anything — so it asks
    /// for exactly what the Select alone would.
    /// </summary>
    [Test]
    public void A_materializing_call_after_a_nested_Select_is_transparent()
        => Assert.That(GraphQLQueryable.For<Gadget>()
                .Where(g => g.Name == "x")
                .Select(g => new
                {
                    g.Name,
                    Primary = g.Parts!.Primary.Select(part => new { part.Name, part.Weight }).ToArray()
                })
                .ToQueryPlan().Query,
            Is.EqualTo("query($v0: GadgetFilterInput) "
                + "{ gadgets(where: $v0) { name parts { primary { name weight } } } }"));

    [Test]
    public void ToList_works_the_same_way()
        => Assert.That(GraphQLQueryable.For<Gadget>()
                .Where(g => g.Name == "x")
                .Select(g => new
                {
                    Secondary = g.Parts!.Secondary.Select(part => new { part.Name }).ToList()
                })
                .ToQueryPlan().Query,
            Does.Contain("parts { secondary { name } }"));

    [Test]
    public void Two_nested_chains_merge_under_one_parent()
        => Assert.That(GraphQLQueryable.For<Gadget>()
                .Where(g => g.Name == "x")
                .Select(g => new
                {
                    Primary = g.Parts!.Primary.Select(part => new { part.Name }).ToArray(),
                    Secondary = g.Parts!.Secondary.Select(part => new { part.Weight }).ToArray()
                })
                .ToQueryPlan().Query,
            Does.Contain("parts { primary { name } secondary { weight } }"));

    /// <summary>An object field must carry a selection set, so naming it selects its scalars.</summary>
    [Test]
    public void An_object_member_named_bare_expands_to_its_scalars()
        => Assert.That(GraphQLQueryable.For<Gadget>()
                .Where(g => g.Name == "x")
                .Select(g => new { g.Name, g.Size })
                .ToQueryPlan().Query,
            Does.Contain("{ name size { minimum maximum } }"));

    /// <summary>A collection with no projection expands the same way.</summary>
    [Test]
    public void A_collection_member_named_bare_expands_to_its_scalars()
        => Assert.That(GraphQLQueryable.For<Gadget>()
                .Where(g => g.Name == "x")
                .Select(g => new { Primary = g.Parts!.Primary.ToArray() })
                .ToQueryPlan().Query,
            Does.Contain("parts { primary { name weight } }"));

    /// <summary>A list of scalars is a leaf; a selection set on it would be invalid.</summary>
    [Test]
    public void A_scalar_list_stays_a_leaf()
        => Assert.That(GraphQLQueryable.For<Gadget>()
                .Where(g => g.Name == "x")
                .Select(g => new { g.Tags })
                .ToQueryPlan().Query,
            Does.Contain("{ tags }").And.Not.Contains("tags {"));

    /// <summary>An explicit projection wins; expansion only fills an empty selection.</summary>
    [Test]
    public void An_explicit_projection_is_not_expanded()
        => Assert.That(GraphQLQueryable.For<Gadget>()
                .Where(g => g.Name == "x")
                .Select(g => new { g.Size!.Minimum, g.Size })
                .ToQueryPlan().Query,
            Does.Contain("size { minimum }").And.Not.Contains("maximum"));

    /// <summary>
    /// Expansion takes the member's own scalars and skips its nested fields, so a graph that
    /// points back at itself has no way to recurse.
    /// </summary>
    [Test]
    public void Expansion_skips_nested_fields()
        => Assert.That(GraphQLQueryable.For<Gadget>()
                .Where(g => g.Name == "x")
                .Select(g => new { g.Name, g.Nested })
                .ToQueryPlan().Query,
            Is.EqualTo("query($v0: GadgetFilterInput) "
                + "{ gadgets(where: $v0) { name nested { label } } }"));

    /// <summary>
    /// A type with no scalars at all contributes an empty selection set, which GraphQL does not
    /// allow — the one case expansion still cannot answer.
    /// </summary>
    [Test]
    public void A_member_with_no_scalars_is_FGQL014()
        => Assert.That(ThrowsWith(() => GraphQLQueryable.For<Gadget>()
                .Where(g => g.Name == "x")
                .Select(g => new { g.Parts })
                .ToQueryPlan()),
            Is.EqualTo("FGQL014"));

    /// <summary>Computation the builder cannot see the fields through is still refused.</summary>
    [Test]
    public void Unreadable_computation_is_still_FGQL013()
        => Assert.That(ThrowsWith(() => GraphQLQueryable.For<Gadget>()
                .Where(g => g.Name == "x")
                .Select(g => new { Shouted = g.Name.ToUpperInvariant() })
                .ToQueryPlan()),
            Is.EqualTo("FGQL013"));
}
