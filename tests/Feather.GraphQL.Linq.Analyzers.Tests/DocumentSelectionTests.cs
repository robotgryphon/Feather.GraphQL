namespace Feather.GraphQL.Linq.Analyzers.Tests;

/// <summary>
/// The fields a written document asks for, read back out of it.
/// </summary>
/// <remarks>
/// What this has to get right is not parsing GraphQL — it reads a small subset and declines the
/// rest — but knowing which subset it is in. A document it misreads produces a reader looking for
/// fields the server never sends, and a reply full of nulls rather than an error.
/// </remarks>
[TestFixture]
public class DocumentSelectionTests
{
    /// <summary>The selection under the root field, flattened for comparison.</summary>
    private static string? Read(string document)
    {
        var selection = DocumentSelectionReader.Read(document);

        if (selection is null)
            return null;

        var builder = new System.Text.StringBuilder();
        SelectionSetWriter.Print(builder, selection);

        return builder.ToString();
    }

    [Test]
    public void A_plain_query_gives_its_root_fields_selection()
        => Assert.That(Read("query { people { name age } }"), Is.EqualTo("name age"));

    [Test]
    public void An_anonymous_operation_reads_the_same()
        => Assert.That(Read("{ people { name age } }"), Is.EqualTo("name age"));

    /// <summary>A named operation with variables: neither changes the reply's shape.</summary>
    [Test]
    public void A_named_operation_with_variables_reads_the_same()
        => Assert.That(
            Read("query People($min: Int!) { people(where: { age: { gt: $min } }) { name age } }"),
            Is.EqualTo("name age"));

    [Test]
    public void A_nested_selection_is_kept()
        => Assert.That(
            Read("query { people { name home { city country } } }"),
            Is.EqualTo("name home { city country }"));

    /// <summary>A brace inside an argument's string does not end the arguments.</summary>
    [Test]
    public void A_brace_inside_a_string_argument_is_not_structure()
        => Assert.That(
            Read("""query { people(where: { name: { eq: "}{" } }) { name } }"""),
            Is.EqualTo("name"));

    [Test]
    public void Whitespace_and_commas_are_not_fields()
        => Assert.That(
            Read("query {\n  people {\n    name,\n    age\n  }\n}"),
            Is.EqualTo("name age"));

    /// <summary>A paged field's wrapper is part of the selection, as it is on the wire.</summary>
    [Test]
    public void A_connection_keeps_its_wrapper()
        => Assert.That(
            Read("query { people { nodes { name } totalCount } }"),
            Is.EqualTo("nodes { name } totalCount"));

    // ---- what it declines ---------------------------------------------------------------------

    /// <summary>
    /// An alias renames the field in the reply, so the name in the document is not the key.
    /// </summary>
    /// <remarks>
    /// The most important decline of the set: read as though the alias were the field, the reader
    /// would look for a name the server never sent and find nothing.
    /// </remarks>
    [Test]
    public void An_alias_declines()
        => Assert.That(Read("query { people { who: name age } }"), Is.Null);

    [TestCase("query { people { ...Fields } }", TestName = "a fragment spread")]
    [TestCase("query { people { ... on Person { name } } }", TestName = "an inline fragment")]
    [TestCase("query { people { name @include(if: $yes) } }", TestName = "a directive")]
    [TestCase("query { people { name } places { city } }", TestName = "two root fields")]
    [TestCase("query { people { name }", TestName = "an unclosed selection")]
    [TestCase("query", TestName = "no selection at all")]
    public void What_it_cannot_be_certain_of_declines(string document)
        => Assert.That(Read(document), Is.Null);
}
