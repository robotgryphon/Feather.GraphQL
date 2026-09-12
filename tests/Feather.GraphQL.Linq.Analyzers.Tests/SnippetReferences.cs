using System.Collections.Immutable;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;

namespace Feather.GraphQL.Linq.Analyzers.Tests;

/// <summary>
/// What a snippet compilation is built against.
/// </summary>
/// <remarks>
/// Everything beside the test assembly, plus the framework. Broad on purpose: a snippet has to
/// bind against the same library the runtime half of every comparison runs against, and naming
/// assemblies one at a time is how that quietly stops being true.
/// </remarks>
internal static class SnippetReferences
{
    /// <summary>Every assembly next to the tests, and every one in the shared framework.</summary>
    public static ImmutableArray<MetadataReference> All()
    {
        string core = Path.GetDirectoryName(typeof(object).Assembly.Location)!;

        return
        [
            .. Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll")
                .Concat(Directory.EnumerateFiles(core, "*.dll"))
                .Where(Loadable)
                .GroupBy(Path.GetFileName)
                .Select(group => (MetadataReference)MetadataReference.CreateFromFile(group.First()))
        ];
    }

    /// <summary>Native and resource libraries sit in the same folder and are not assemblies.</summary>
    private static bool Loadable(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new PEReader(stream);

            return reader.HasMetadata;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }
}
