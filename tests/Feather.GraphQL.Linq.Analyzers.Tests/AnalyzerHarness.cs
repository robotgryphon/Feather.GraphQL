using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Feather.GraphQL.Linq.Analyzers.Tests;

/// <summary>
/// Compiles a snippet against the real library and runs the analyzer over it.
/// </summary>
/// <remarks>
/// Hand-rolled rather than pulled from Microsoft.CodeAnalysis.Testing: the whole harness is one
/// compilation and one call, and the diagnostics it must not report are as interesting as the
/// ones it must, so the assertions read better written out.
/// </remarks>
internal static class AnalyzerHarness
{
    private static readonly ImmutableArray<MetadataReference> _references = BuildReferences();

    /// <summary>The library IDs the analyzer raised, in source order.</summary>
    public static string[] Diagnose(string snippet)
    {
        string source = $"{Preamble}\n        return {snippet}\n    }}\n}}\n";

        var compilation = CSharpCompilation.Create(
            "Snippet",
            [CSharpSyntaxTree.ParseText(source)],
            _references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        // A snippet that does not compile would make any analyzer result meaningless.
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToArray();

        Assert.That(errors, Is.Empty, $"snippet did not compile: {string.Join("; ", errors.Select(e => e.ToString()))}");

        var analyzed = compilation
            .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new ProjectionAnalyzer()))
            .GetAnalyzerDiagnosticsAsync()
            .GetAwaiter()
            .GetResult();

        return [.. analyzed.OrderBy(d => d.Location.SourceSpan.Start).Select(d => d.Id)];
    }

    /// <summary>
    /// The model the snippets project over: a type whose continent leads back to it, one that
    /// nests without cycling, and one that is scalars all the way down.
    /// </summary>
    private const string Preamble = """
        using System.Collections.Generic;
        using System.Linq;
        using Feather.GraphQL.Linq;

        [GenerateQueryable("countries")]
        public class Country
        {
            public string Name { get; set; } = "";
            public Continent Continent { get; set; } = new();
            public Dimensions? Size { get; set; }
            public Parts? Parts { get; set; }
            public Parts? More { get; set; }
            public string[] Tags { get; set; } = [];
        }

        public class Continent
        {
            public string Code { get; set; } = "";
            public string Name { get; set; } = "";
            public IReadOnlyCollection<Country> Countries { get; set; } = [];
        }

        public class Dimensions
        {
            public string Minimum { get; set; } = "";
            public string Maximum { get; set; } = "";
        }

        public class Parts
        {
            public Part[] Primary { get; set; } = [];
        }

        public class Part
        {
            public string Name { get; set; } = "";
        }

        public class NotQueryable
        {
            public Parts? Parts { get; set; }
        }

        public static class Snippet
        {
            public static object? Run(IQueryable<Country> countries, IQueryable<NotQueryable> others)
            {
        """;

    private static ImmutableArray<MetadataReference> BuildReferences()
    {
        var references = ImmutableArray.CreateBuilder<MetadataReference>();

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location))
                references.Add(MetadataReference.CreateFromFile(assembly.Location));
        }

        // Loaded on demand, so it may not be in the AppDomain yet.
        references.Add(MetadataReference.CreateFromFile(
            typeof(GenerateQueryableAttribute).Assembly.Location));

        string core = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        foreach (string name in new[] { "System.Runtime.dll", "netstandard.dll", "System.Linq.Expressions.dll" })
        {
            string path = Path.Combine(core, name);
            if (File.Exists(path))
                references.Add(MetadataReference.CreateFromFile(path));
        }

        return references.Distinct().ToImmutableArray();
    }
}
