using Feather.GraphQL.Linq.Filtering;
using Feather.GraphQL.Linq.Query;

namespace Feather.GraphQL.Linq.Tests.Query;

/// <summary>
/// §4.4: a filter input need not be shaped like the type it filters, so a predicate can be
/// written against the input instead.
/// </summary>
[TestFixture]
public class FilterShapeTests
{
    private static string ThrowsWith(TestDelegate action)
        => Assert.Throws<GraphQLTranslationException>(action)!.DiagnosticId;

    private static string Variables(GraphQLQueryPlan plan)
        => System.Text.Json.JsonSerializer.Serialize(plan.Variables);

    /// <summary>
    /// The whole point: the queried type nests, the filter input does not, and the predicate
    /// follows the input.
    /// </summary>
    [Test]
    public void A_filter_shape_lowers_to_its_own_field_path()
    {
        var plan = Schema.Countries
            .Where((CountryFilter f) => f.Continent == "Europe")
            .Select(c => new { c.Name })
            .ToQueryPlan();

        Assert.That(Variables(plan), Is.EqualTo("""{"v0":{"continent":{"eq":"Europe"}}}"""));
    }

    [Test]
    public void The_element_type_lowers_through_its_own_nesting()
    {
        var plan = Schema.Countries
            .Where(c => c.Continent.Name == "Europe")
            .Select(c => new { c.Name })
            .ToQueryPlan();

        Assert.That(Variables(plan), Is.EqualTo("""{"v0":{"continent":{"name":{"eq":"Europe"}}}}"""));
    }

    /// <summary>The argument's type name belongs to the root field, not to the predicate's type.</summary>
    [Test]
    public void The_filter_input_type_name_comes_from_the_queried_type()
        => Assert.That(Schema.Countries
                .Where((CountryFilter f) => f.Continent == "Europe")
                .Select(c => new { c.Name })
                .ToQueryPlan().Query,
            Does.StartWith("query($v0: CountryFilterInput)"));

    [Test]
    public void The_selection_set_still_comes_from_the_queried_type()
        => Assert.That(Schema.Countries
                .Where((CountryFilter f) => f.Continent == "Europe")
                .Select(c => new { c.Name })
                .ToQueryPlan().Query,
            Is.EqualTo("query($v0: CountryFilterInput) { countries(where: $v0) { name } }"));

    [Test]
    public void Field_names_honour_JsonPropertyName_on_the_filter_shape()
        => Assert.That(Variables(Schema.Countries
                .Where((CountryFilter f) => f.CurrencyCode == "EUR")
                .Select(c => new { c.Name })
                .ToQueryPlan()),
            Is.EqualTo("""{"v0":{"currency":{"eq":"EUR"}}}"""));

    [Test]
    public void Several_filter_shape_predicates_merge()
        => Assert.That(Variables(Schema.Countries
                .Where((CountryFilter f) => f.Continent == "Europe")
                .Where((CountryFilter f) => f.CurrencyCode == "EUR")
                .Select(c => new { c.Name })
                .ToQueryPlan()),
            Is.EqualTo("""{"v0":{"continent":{"eq":"Europe"},"currency":{"eq":"EUR"}}}"""));

    [Test]
    public void It_composes_with_the_argument_names()
        => Assert.That(Schema.Countries
                .WithGraphQLArguments(new() { Filter = "filter" })
                .Where((CountryFilter f) => f.Continent == "Europe")
                .Select(c => new { c.Name })
                .ToQueryPlan().Query,
            Is.EqualTo("query($v0: CountryFilterInput) { countries(filter: $v0) { name } }"));

    /// <summary>
    /// The two shapes lower to different field paths, so merging them would produce a filter
    /// matching neither.
    /// </summary>
    [Test]
    public void Mixing_the_two_shapes_is_FGQL020()
        => Assert.That(ThrowsWith(() => Schema.Countries
                .Where(c => c.Name == "France")
                .Where((CountryFilter f) => f.Continent == "Europe")
                .ToQueryPlan()),
            Is.EqualTo("FGQL020"));

    /// <summary>
    /// The short form: one call says both what to filter on and what the server calls the
    /// argument, so a simple query never has to meet WithGraphQLArguments.
    /// </summary>
    [Test]
    public void The_filter_argument_can_be_named_inline()
        => Assert.That(Schema.Countries
                .Where("filter", (CountryFilter f) => f.Continent == "Europe")
                .Select(c => new { c.Name })
                .ToQueryPlan().Query,
            Is.EqualTo("query($v0: CountryFilterInput) { countries(filter: $v0) { name } }"));

    /// <summary>
    /// Both type arguments given explicitly is the other spelling of the same call, and lets the
    /// lambda parameter stay bare.
    /// </summary>
    [Test]
    public void Both_type_arguments_may_be_given_explicitly()
        => Assert.That(Schema.Countries
                .Where<Country, CountryFilter>("filter", f => f.Continent == "Europe")
                .Select(c => new { c.Name })
                .ToQueryPlan().Query,
            Is.EqualTo("query($v0: CountryFilterInput) { countries(filter: $v0) { name } }"));

    [Test]
    public void An_inline_name_works_over_the_queried_type_too()
        => Assert.That(Schema.People
                .Where("filter", p => p.Age > 30)
                .ToQueryPlan().Query,
            Is.EqualTo("query($v0: PersonFilterInput) { people(filter: $v0) { name age emailAddress } }"));

    [Test]
    public void An_inline_name_lowers_the_predicate_unchanged()
        => Assert.That(Variables(Schema.Countries
                .Where("filter", (CountryFilter f) => f.Continent == "Europe")
                .Select(c => new { c.Name })
                .ToQueryPlan()),
            Is.EqualTo("""{"v0":{"continent":{"eq":"Europe"}}}"""));

    /// <summary>Both spellings set the same slot, so the later call in the chain wins.</summary>
    [Test]
    public void The_later_of_the_two_spellings_wins()
    {
        string inlineLast = Schema.People
            .WithGraphQLArguments(new() { Filter = "criteria" })
            .Where("filter", p => p.Age > 30)
            .ToQueryPlan().Query;

        string argumentsLast = Schema.People
            .Where("filter", p => p.Age > 30)
            .WithGraphQLArguments(new() { Filter = "criteria" })
            .ToQueryPlan().Query;

        Assert.Multiple(() =>
        {
            Assert.That(inlineLast, Does.Contain("people(filter: $v0)"));
            Assert.That(argumentsLast, Does.Contain("people(criteria: $v0)"));
        });
    }

    /// <summary>Naming the argument leaves the other arguments alone.</summary>
    [Test]
    public void An_inline_name_does_not_touch_the_other_arguments()
        => Assert.That(Schema.People
                .Where("filter", p => p.Age > 30)
                .OrderBy(p => p.Name)
                .Take(5)
                .ToQueryPlan().Query,
            Does.Contain("people(filter: $v0, order: $v1, take: $v2)"));

    [Test]
    public void An_empty_inline_name_is_rejected()
        => Assert.Throws<ArgumentException>(
            () => Schema.People.Where(" ", p => p.Age > 30));

    [Test]
    public void It_works_over_a_foreign_queryable()
        => Assert.That(Variables(Array.Empty<Country>().AsQueryable()
                .Where((CountryFilter f) => f.Continent == "Europe")
                .Select(c => new { c.Name })
                .ToQueryPlan(new GraphQLQueryOptions { RootField = "countries" })),
            Is.EqualTo("""{"v0":{"continent":{"eq":"Europe"}}}"""));
}
