using System.Reflection;
using System.Text.Json;

namespace Feather.GraphQL.Linq.Materialization;

/// <summary>
/// The runtime half of <see cref="ProjectionBinder"/>: what a compiled projection calls to pull
/// values out of a response element.
/// </summary>
/// <remarks>
/// A missing field reads as <c>default</c> rather than throwing. A server is free to return
/// <c>null</c> for anything nullable, and partial results arrive alongside errors — which the
/// terminal has already surfaced by the time these run.
/// </remarks>
internal static class JsonReader
{
    public static readonly MethodInfo ValueMethod = Method(nameof(ReadValue));
    public static readonly MethodInfo ListMethod = Method(nameof(ReadList));
    public static readonly MethodInfo ObjectMethod = Method(nameof(ReadObject));

    /// <summary>Reads a single field at <paramref name="path"/>, scalar or object alike.</summary>
    public static TValue? ReadValue<TValue>(JsonElement element, string[] path)
        => TryDescend(element, path, out var target) ? target.Deserialize<TValue>(GraphQLJsonOptions.Instance) : default;

    /// <summary>Reads a collection field, projecting each item through <paramref name="select"/>.</summary>
    public static IEnumerable<TItem> ReadList<TItem>(
        JsonElement element,
        string[] path,
        Func<JsonElement, TItem> select)
    {
        if (!TryDescend(element, path, out var target) || target.ValueKind != JsonValueKind.Array)
            return [];

        var items = new TItem[target.GetArrayLength()];
        int index = 0;

        foreach (var item in target.EnumerateArray())
            items[index++] = select(item);

        return items;
    }

    /// <summary>Reads the element itself — the <c>Select(p =&gt; p)</c> case.</summary>
    public static TObject? ReadObject<TObject>(JsonElement element)
        => element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? default
            : element.Deserialize<TObject>(GraphQLJsonOptions.Instance);

    private static bool TryDescend(JsonElement element, string[] path, out JsonElement result)
    {
        var current = element;

        foreach (string segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out var child))
            {
                result = default;
                return false;
            }

            current = child;
        }

        result = current;
        return current.ValueKind != JsonValueKind.Null;
    }

    private static MethodInfo Method(string name)
        => typeof(JsonReader).GetMethod(name, BindingFlags.Public | BindingFlags.Static)!;
}
