namespace Feather.GraphQL.Linq.Analyzers.Tests;

/// <summary>
/// FGQL012: a chain that asks for every record. A warning, not a refusal — the query runs, and
/// what these pin down is when it is worth mentioning.
/// </summary>
[TestFixture]
public class UnboundedQueryAnalyzerTests
{
    [Test]
    public void A_bare_terminal_is_FGQL012()
        => Assert.That(
            AnalyzerHarness.Diagnose("client.CreateQueryable<Country>(\"countries\").ToArray();"),
            Is.EqualTo(new[] { "FGQL012" }));

    [Test]
    public void A_bare_async_terminal_is_FGQL012()
        => Assert.That(
            AnalyzerHarness.Diagnose("client.CreateQueryable<Country>(\"countries\").ToListAsync();"),
            Is.EqualTo(new[] { "FGQL012" }));

    [Test]
    public void A_filter_clears_it()
        => Assert.That(
            AnalyzerHarness.Diagnose("client.CreateQueryable<Country>(\"countries\").Where(c => c.Name == \"x\").ToArray();"),
            Is.Empty);

    [Test]
    public void A_page_clears_it()
        => Assert.That(
            AnalyzerHarness.Diagnose("client.CreateQueryable<Country>(\"countries\").Take(10).ToArray();"),
            Is.Empty);

    [Test]
    public void A_projection_clears_it()
        => Assert.That(
            AnalyzerHarness.Diagnose("client.CreateQueryable<Country>(\"countries\").Select(c => new { c.Name }).ToArray();"),
            Is.Empty);

    /// <summary>
    /// A result operator bounds the request by construction: the translator asks the server for
    /// a page of one rather than fetching everything and keeping the first row.
    /// </summary>
    [Test]
    public void A_result_operator_clears_it()
        => Assert.That(
            AnalyzerHarness.Diagnose("client.CreateQueryable<Country>(\"countries\").First();"),
            Is.Empty);

    /// <summary>A count asks for a number, so "this fetches every record" does not apply.</summary>
    [Test]
    public void A_count_clears_it()
        => Assert.That(
            AnalyzerHarness.Diagnose("client.CreateQueryable<Country>(\"countries\").Count();"),
            Is.Empty);

    /// <summary>
    /// Translating is not executing, but the document it prints describes the same unbounded
    /// request, so it is reported the same way.
    /// </summary>
    [Test]
    public void Translating_an_unbounded_chain_is_still_FGQL012()
        => Assert.That(
            AnalyzerHarness.Diagnose("client.CreateQueryable<Country>(\"countries\").ToGraphQLQuery();"),
            Is.EqualTo(new[] { "FGQL012" }));

    /// <summary>
    /// A queryable that is not this library's has entirely different rules, and warning about
    /// one would be a false positive on unrelated code.
    /// </summary>
    [Test]
    public void A_foreign_queryable_is_not_reported()
        => Assert.That(AnalyzerHarness.Diagnose("loose.ToArray();"), Is.Empty);

    /// <summary>
    /// A chain the reader cannot follow to a terminal is left alone, as everywhere else: the
    /// runtime now runs it either way, so a guess here would only ever be noise.
    /// </summary>
    [Test]
    public void An_unterminated_chain_is_not_reported()
        => Assert.That(
            AnalyzerHarness.Diagnose("client.CreateQueryable<Country>(\"countries\");"),
            Is.Empty);
}
