using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Feather.GraphQL.Linq.Analyzers.Tests;

/// <summary>
/// What the compiler is willing to call a value in the schema.
/// </summary>
/// <remarks>
/// <para>
/// Only ever asked when a filter's structure goes into the document, because that is the only
/// place a value has to declare its own type. A value written into a variables payload is coerced
/// by the server against whatever input type it lands in and never names itself, which is why
/// nothing needed this until the filter moved.
/// </para>
/// <para>
/// The interesting half of this file is what answers null. A wrong name here is not a build
/// failure but a query the server refuses at run time, so the rule is that a mapping which is a
/// judgement call is not made — the filter goes back into a variable instead, which costs the
/// analyser its view and costs correctness nothing.
/// </para>
/// </remarks>
[TestFixture]
public class ScalarNameTests
{
    [TestCase("System.String", "String")]
    [TestCase("System.Boolean", "Boolean")]
    [TestCase("System.Byte", "Byte")]
    [TestCase("System.Int16", "Short")]
    [TestCase("System.Int32", "Int")]
    [TestCase("System.Int64", "Long")]
    [TestCase("System.Single", "Float")]
    [TestCase("System.Double", "Float")]
    [TestCase("System.Decimal", "Decimal")]
    [TestCase("System.Guid", "UUID")]
    [TestCase("System.DateTime", "DateTime")]
    [TestCase("System.DateTimeOffset", "DateTime")]
    [TestCase("System.DateOnly", "Date")]
    public void A_type_the_schema_names_one_way_is_named(string clr, string scalar)
        => Assert.That(GraphQLTypeFacts.ScalarName(Type(clr)), Is.EqualTo(scalar));

    /// <summary>
    /// The types a schema could reasonably call more than one thing, which are left unnamed.
    /// </summary>
    /// <remarks>
    /// A <c>Uri</c> is a <c>URL</c> to one server and a <c>String</c> to the next; a
    /// <c>TimeSpan</c> is an ISO-8601 string or a count of seconds; <c>char</c> is a one-character
    /// string or a number; and the unsigned integers are whatever width the schema chose to widen
    /// them to. None of them is guessed.
    /// </remarks>
    [TestCase("System.Uri")]
    [TestCase("System.TimeSpan")]
    [TestCase("System.TimeOnly")]
    [TestCase("System.Char")]
    [TestCase("System.UInt32")]
    [TestCase("System.UInt64")]
    public void A_type_the_schema_could_name_several_ways_is_not_guessed(string clr)
        => Assert.That(GraphQLTypeFacts.ScalarName(Type(clr)), Is.Null);

    /// <summary>
    /// A nullable value type is named for what it wraps.
    /// </summary>
    /// <remarks>
    /// The variable is declared nullable either way — a filter's <c>eq</c> takes a nullable
    /// scalar, and a non-null variable would be accepted where one is wanted but not the reverse
    /// — so the wrapper changes nothing about the name.
    /// </remarks>
    [Test]
    public void A_nullable_value_is_named_for_what_it_wraps()
    {
        var compilation = Compilation();

        var nullable = compilation.GetTypeByMetadataName("System.Nullable`1")!
            .Construct(compilation.GetTypeByMetadataName("System.Int32")!);

        Assert.That(GraphQLTypeFacts.ScalarName(nullable), Is.EqualTo("Int"));
    }

    /// <summary>What a document can carry unquoted, which is narrower than a JSON key.</summary>
    [TestCase("name", true)]
    [TestCase("_name", true)]
    [TestCase("name2", true)]
    [TestCase("", false)]
    [TestCase("2name", false)]
    [TestCase("first-name", false)]
    [TestCase("first name", false)]
    [TestCase("naïve", false)]
    public void A_field_name_is_only_written_out_when_the_grammar_allows_it(string name, bool allowed)
        => Assert.That(GraphQLTypeFacts.IsGraphQLName(name), Is.EqualTo(allowed));

    private static ITypeSymbol Type(string metadataName)
        => Compilation().GetTypeByMetadataName(metadataName)
            ?? throw new InvalidOperationException($"'{metadataName}' is not in the reference set");

    private static CSharpCompilation Compilation()
        => CSharpCompilation.Create("Scalars", references: SnippetReferences.All());
}
