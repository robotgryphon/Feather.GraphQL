using System.Collections.Immutable;
using Feather.GraphQL.Linq.Query;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Feather.GraphQL.Linq.Analyzers.Tests;

/// <summary>A composite, as both a symbol and a CLR type.</summary>
public class Model
{
    public string Name { get; set; } = "";
}

/// <summary>A user-defined value type — not a scalar, despite being a struct.</summary>
public struct Shape
{
    public string Name { get; set; }
}

/// <summary>
/// The compile-time and runtime answers to "is this a scalar?" must be the same answer.
/// </summary>
/// <remarks>
/// <para>
/// Both sides now call one rule, so the rule itself cannot drift. What can still drift is the
/// <em>mapping</em> — each side describes its own type model to the rule, and a mapping that
/// describes a type wrongly produces a wrong answer from a correct rule.
/// </para>
/// <para>
/// This is not hypothetical. The first version of the symbol mapping passed Roslyn's
/// <c>ToDisplayString()</c>, which renders <c>string</c> rather than <c>System.String</c>, so
/// every string field stopped being a scalar and whole selection sets emptied out. Nothing but a
/// comparison against the runtime answer catches that class of mistake.
/// </para>
/// </remarks>
[TestFixture]
public class TypeFactsAgreementTests
{
    /// <summary>
    /// Each case is a type written two ways: as C# the compiler will resolve into a symbol, and
    /// as the CLR type the translator would see for the same field.
    /// </summary>
    private static readonly (string Source, Type Runtime)[] _types =
    [
        ("string", typeof(string)),
        ("int", typeof(int)),
        ("int?", typeof(int?)),
        ("bool", typeof(bool)),
        ("char", typeof(char)),
        ("byte", typeof(byte)),
        ("long", typeof(long)),
        ("double", typeof(double)),
        ("float", typeof(float)),
        ("decimal", typeof(decimal)),
        ("decimal?", typeof(decimal?)),
        ("System.Guid", typeof(Guid)),
        ("System.DateTime", typeof(DateTime)),
        ("System.DateTimeOffset", typeof(DateTimeOffset)),
        ("System.DateOnly", typeof(DateOnly)),
        ("System.TimeOnly", typeof(TimeOnly)),
        ("System.TimeSpan", typeof(TimeSpan)),
        ("System.Uri", typeof(Uri)),
        ("System.DayOfWeek", typeof(DayOfWeek)),
        ("System.DayOfWeek?", typeof(DayOfWeek?)),
        ("System.Version", typeof(Version)),
        ("System.Text.StringBuilder", typeof(System.Text.StringBuilder)),
        ("string[]", typeof(string[])),
        ("int[]", typeof(int[])),
        ("System.Collections.Generic.List<string>", typeof(List<string>)),
        ("System.Collections.Generic.IReadOnlyCollection<int>", typeof(IReadOnlyCollection<int>)),
        ("Model", typeof(Model)),
        ("Model[]", typeof(Model[])),
        ("Shape", typeof(Shape)),
        ("Shape?", typeof(Shape?))
    ];

    [Test]
    public void Both_mappings_reach_the_same_verdict()
    {
        var symbols = Resolve();

        Assert.Multiple(() =>
        {
            for (int i = 0; i < _types.Length; i++)
            {
                (string source, var runtime) = _types[i];

                Assert.That(
                    GraphQLTypeFacts.IsScalar(symbols[i]),
                    Is.EqualTo(SelectionSetBuilder.IsScalar(runtime)),
                    $"the compiler and the runtime disagree about '{source}'");
            }
        });
    }

    /// <summary>
    /// The same comparison for the leaf rule, which is the scalar rule applied through a
    /// collection — so a mapping that gets element types wrong shows up here and not above.
    /// </summary>
    [Test]
    public void Both_mappings_reach_the_same_verdict_about_leaves()
    {
        var symbols = Resolve();

        Assert.Multiple(() =>
        {
            for (int i = 0; i < _types.Length; i++)
            {
                (string source, var runtime) = _types[i];

                Assert.That(
                    GraphQLTypeFacts.IsLeaf(symbols[i]),
                    Is.EqualTo(SelectionSetBuilder.IsLeaf(runtime)),
                    $"the compiler and the runtime disagree about a field of '{source}'");
            }
        });
    }

    /// <summary>
    /// A guard on the comparison itself: if the corpus were all scalars or all composites, two
    /// mappings that always answered the same thing would pass while agreeing about nothing.
    /// </summary>
    [Test]
    public void The_corpus_covers_both_answers()
    {
        var symbols = Resolve();
        bool[] verdicts = [.. symbols.Select(GraphQLTypeFacts.IsScalar)];

        Assert.Multiple(() =>
        {
            Assert.That(verdicts, Does.Contain(true), "no case in the corpus is a scalar");
            Assert.That(verdicts, Does.Contain(false), "no case in the corpus is a composite");
        });
    }

    /// <summary>
    /// Compiles one field per case and hands back the resolved symbols, in the corpus's order.
    /// </summary>
    private static ImmutableArray<ITypeSymbol> Resolve()
    {
        string fields = string.Join(
            "\n",
            _types.Select((t, i) => $"    public {t.Source} F{i} = default!;"));

        string source = $$"""
            public class Model { public string Name = ""; }
            public struct Shape { public string Name; }
            public class Fields
            {
            {{fields}}
            }
            """;

        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "TypeFacts",
            [tree],
            References(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToArray();

        Assert.That(errors, Is.Empty,
            $"the corpus did not compile: {string.Join("; ", errors.Select(e => e.ToString()))}");

        var fieldsType = compilation.GetTypeByMetadataName("Fields")!;

        // By ordinal rather than by name: the corpus's order is what pairs a symbol with its
        // runtime counterpart.
        return [.. _types
            .Select((_, i) => ((IFieldSymbol)fieldsType.GetMembers($"F{i}")[0]).Type)];
    }

    private static IEnumerable<MetadataReference> References()
    {
        string core = Path.GetDirectoryName(typeof(object).Assembly.Location)!;

        return AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .Concat(new[] { "System.Runtime.dll", "netstandard.dll", "System.Collections.dll" }
                .Select(n => Path.Combine(core, n))
                .Where(File.Exists)
                .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)))
            .Distinct();
    }
}
