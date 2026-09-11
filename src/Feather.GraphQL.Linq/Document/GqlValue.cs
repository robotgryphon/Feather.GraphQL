using System.Text.Json;

namespace Feather.GraphQL.Linq.Document;

/// <summary>
/// A value on its way to a server: what a variable binds, and what an inlined argument prints.
/// </summary>
/// <remarks>
/// <para>
/// This used to be a <c>JsonNode</c> tree, and the cost was out of
/// all proportion to the job. Lowering <c>c.Continent.Code == code</c> — one comparison —
/// allocated 1,712 bytes, because three nested <c>JsonObject</c>s each carry a dictionary built
/// for random access to a map that is written once, in order, and never read.
/// </para>
/// <para>
/// These nodes are ordered lists of a few entries, which is what a filter actually is. They also
/// have no parent: a <c>JsonNode</c> belongs to one tree, so the merge step had to deep-clone
/// every value it moved, and nothing here has to.
/// </para>
/// </remarks>
internal abstract class GqlValue
{
    /// <summary>Writes this value as JSON, for a variables payload.</summary>
    public abstract void WriteTo(Utf8JsonWriter writer);
}

/// <summary>A single value, kept as the CLR object until something asks for it.</summary>
/// <remarks>
/// Converted at the point of writing rather than at the point of lowering. The lowering never
/// looks inside a value — it moves them between shapes and compares only paths — so converting
/// early would be work done for a reader that does not exist.
/// </remarks>
internal sealed class GqlScalar(object? value) : GqlValue
{
    /// <summary>The value, as the predicate supplied it.</summary>
    public object? Value { get; } = value;

    public override void WriteTo(Utf8JsonWriter writer) => GqlValueWriter.Write(writer, Value);
}

/// <summary>An ordered list of values: a set operation's operands, or a junction's branches.</summary>
internal sealed class GqlList : GqlValue
{
    public List<GqlValue?> Items { get; } = [];

    public override void WriteTo(Utf8JsonWriter writer)
    {
        writer.WriteStartArray();

        foreach (var item in Items)
        {
            if (item is null)
                writer.WriteNullValue();
            else
                item.WriteTo(writer);
        }

        writer.WriteEndArray();
    }
}

/// <summary>
/// An ordered map of a few entries: a field, an operation, or a merged object.
/// </summary>
/// <remarks>
/// A list rather than a dictionary. A filter object holds one or two entries in almost every
/// case, and the algorithm that builds it reads by key only while merging — where a scan of two
/// entries beats hashing one.
/// </remarks>
internal sealed class GqlObject : GqlValue
{
    private readonly List<(string Key, GqlValue? Value)> _fields = [];

    public GqlObject() { }

    public GqlObject(string key, GqlValue? value) => _fields.Add((key, value));

    public int Count => _fields.Count;

    public (string Key, GqlValue? Value) this[int index] => _fields[index];

    public IReadOnlyList<(string Key, GqlValue? Value)> Fields => _fields;

    /// <summary>Sets a member, replacing it when it is already there.</summary>
    public void Set(string key, GqlValue? value)
    {
        for (int i = 0; i < _fields.Count; i++)
        {
            if (!string.Equals(_fields[i].Key, key, StringComparison.Ordinal))
                continue;

            _fields[i] = (key, value);
            return;
        }

        _fields.Add((key, value));
    }

    public bool TryGet(string key, out GqlValue? value)
    {
        foreach (var (name, existing) in _fields)
        {
            if (!string.Equals(name, key, StringComparison.Ordinal))
                continue;

            value = existing;
            return true;
        }

        value = null;
        return false;
    }

    public override void WriteTo(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();

        foreach (var (key, value) in _fields)
        {
            writer.WritePropertyName(key);

            if (value is null)
                writer.WriteNullValue();
            else
                value.WriteTo(writer);
        }

        writer.WriteEndObject();
    }
}
