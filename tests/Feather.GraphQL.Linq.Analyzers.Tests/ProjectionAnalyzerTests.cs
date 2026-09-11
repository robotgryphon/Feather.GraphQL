namespace Feather.GraphQL.Linq.Analyzers.Tests;

/// <summary>
/// The compile-time half of the projection rules: what the translator would refuse, refused
/// earlier. Each snippet is the body of a <c>return</c> inside a method over
/// <c>IQueryable&lt;Country&gt;</c>.
/// </summary>
[TestFixture]
public class ProjectionAnalyzerTests
{
    /// <summary>
    /// Country.Continent holds Continent.Countries, which is Country again — and that is fine,
    /// because expanding a member takes its scalars and leaves its nested fields alone.
    /// </summary>
    [Test]
    public void A_member_leading_back_into_the_projection_is_fine()
        => Assert.That(
            AnalyzerHarness.Diagnose("client.CreateQueryable<Country>(\"countries\").Select(c => new { c.Name, c.Continent });"),
            Is.Empty);

    /// <summary>
    /// The one case expansion cannot answer: a type whose every field is nested contributes an
    /// empty selection set, and GraphQL has no such thing.
    /// </summary>
    [Test]
    public void A_member_with_no_scalars_is_FGQL014()
        => Assert.That(
            AnalyzerHarness.Diagnose("client.CreateQueryable<Country>(\"countries\").Select(c => new { c.Parts });"),
            Is.EqualTo(new[] { "FGQL014" }));

    [Test]
    public void An_object_member_of_scalars_is_fine()
        => Assert.That(
            AnalyzerHarness.Diagnose("client.CreateQueryable<Country>(\"countries\").Select(c => new { c.Name, c.Size });"),
            Is.Empty);

    [Test]
    public void A_scalar_list_is_fine()
        => Assert.That(
            AnalyzerHarness.Diagnose("client.CreateQueryable<Country>(\"countries\").Select(c => new { c.Tags });"),
            Is.Empty);

    /// <summary>Saying which fields is exactly the fix the diagnostic asks for.</summary>
    [Test]
    public void Naming_the_nested_fields_clears_it()
        => Assert.That(
            AnalyzerHarness.Diagnose("client.CreateQueryable<Country>(\"countries\").Select(c => new { c.Name, Continent = c.Continent.Name });"),
            Is.Empty);

    [Test]
    public void A_nested_Select_clears_it()
        => Assert.That(
            AnalyzerHarness.Diagnose(
                "client.CreateQueryable<Country>(\"countries\")"
                + ".Select(c => new { Parts = c.Parts!.Primary.Select(p => new { p.Name }).ToArray() });"),
            Is.Empty);

    /// <summary>A chain that only materializes expands its element type, as the translator does.</summary>
    [Test]
    public void A_chain_without_a_projection_expands_its_elements()
        => Assert.That(
            AnalyzerHarness.Diagnose("client.CreateQueryable<Country>(\"countries\").Select(c => new { Parts = c.Parts!.Primary.ToArray() });"),
            Is.Empty);

    [Test]
    public void An_object_initializer_is_walked_too()
        => Assert.That(
            AnalyzerHarness.Diagnose("client.CreateQueryable<Country>(\"countries\").Select(c => new Continent { Name = c.Continent.Name });"),
            Is.Empty);

    [Test]
    public void A_constructor_argument_is_walked_too()
        => Assert.That(
            AnalyzerHarness.Diagnose("client.CreateQueryable<Country>(\"countries\").Select(c => new System.Tuple<Parts>(c.Parts!));"),
            Is.EqualTo(new[] { "FGQL014" }));

    /// <summary>
    /// The rules govern this library's queryables. Somebody else's Select over an unattributed
    /// type is none of the analyzer's business.
    /// </summary>
    [Test]
    public void A_projection_over_another_providers_queryable_is_ignored()
        => Assert.That(
            AnalyzerHarness.Diagnose("others.Select(o => new { o.Parts });"),
            Is.Empty);

    /// <summary>
    /// Nothing on the type marks it as this library's any more, so the analyzer looks at where
    /// the chain starts. A queryable arriving some other way is left to the runtime.
    /// </summary>
    [Test]
    public void A_chain_whose_origin_is_not_visible_is_left_alone()
        => Assert.That(
            AnalyzerHarness.Diagnose("loose.Select(c => new { c.Parts });"),
            Is.Empty);

    [Test]
    public void The_translate_only_entry_point_is_analyzed_too()
        => Assert.That(
            AnalyzerHarness.Diagnose(
                "GraphQLQueryable.For<Country>(\"countries\").Select(c => new { c.Parts });"),
            Is.EqualTo(new[] { "FGQL014" }));

    [Test]
    public void Each_offending_member_is_reported_once()
        => Assert.That(
            AnalyzerHarness.Diagnose("client.CreateQueryable<Country>(\"countries\").Select(c => new { c.Continent, c.Parts, c.More });"),
            Is.EqualTo(new[] { "FGQL014", "FGQL014" }));
}
