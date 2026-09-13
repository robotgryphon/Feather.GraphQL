using System.Text.Json;
using BenchmarkDotNet.Attributes;

namespace Feather.GraphQL.Serialization.Benchmarks;

/// <summary>
/// How a generated reader should turn a JSON string into a <see cref="string"/>.
/// </summary>
/// <remarks>
/// <para>
/// <c>Utf8JsonReader.CopyString</c> writes an unescaped value into a buffer the caller owns,
/// which the documentation rightly describes as reading a string without allocating one. The
/// question these rows answer is whether that helps <em>here</em>, where the value is on its way
/// into a <c>string</c> member: a string is an allocation, so copying into a buffer first and
/// constructing the string afterwards ends with the same object plus the work of getting there.
/// </para>
/// <para>
/// The third row is where the idea has somewhere to go. A reply repeats values — every row's
/// continent is one of seven — and a buffer can be compared against strings already made without
/// allocating anything to do the comparison. That trades an allocation per row for a lookup per
/// row, which is a real trade rather than a free win, and worth a number either way.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class StringReading
{
    private byte[] _narrow = null!;

    /// <inheritdoc cref="Payloads.Sizes"/>
    [Params(25, 100)]
    public int Rows { get; set; }

    [GlobalSetup]
    public void Setup() => _narrow = Payloads.NarrowBody(Rows);

    /// <summary>What the generator emits today.</summary>
    [Benchmark(Baseline = true, Description = "GetString")]
    public int GetString() => Read(_narrow, Strategy.GetString);

    /// <summary>Copied into a buffer, then made into the string the member needs.</summary>
    [Benchmark(Description = "CopyString + new string")]
    public int CopyString() => Read(_narrow, Strategy.CopyString);

    /// <summary>
    /// Copied into a buffer, then matched against the strings this reply already made.
    /// </summary>
    /// <remarks>
    /// The lookup is by span, so a value already seen costs no allocation at all — which is the
    /// only shape in which <c>CopyString</c> can save one on the way into a <c>string</c> member.
    /// </remarks>
    [Benchmark(Description = "CopyString + reuse")]
    public int Deduplicated() => Read(_narrow, Strategy.Deduplicate);

    private enum Strategy { GetString, CopyString, Deduplicate }

    /// <summary>
    /// Reads the reply, building the same rows each way.
    /// </summary>
    /// <remarks>
    /// One reader for the three, with the strategy passed in: what differs between the rows should
    /// be how a string is produced and nothing else, and two readers that drifted apart would be
    /// measuring their drift.
    /// </remarks>
    private static int Read(ReadOnlySpan<byte> json, Strategy strategy)
    {
        var reader = new Utf8JsonReader(json);
        var rows = new List<CountrySummary>();

        Dictionary<string, string>? seen = strategy == Strategy.Deduplicate
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : null;

        var lookup = seen?.GetAlternateLookup<ReadOnlySpan<char>>();

        // data → its single field → the rows.
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            return 0;

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            reader.Read();

            if (reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                continue;
            }

            if (!reader.Read() || reader.TokenType != JsonTokenType.PropertyName)
                continue;

            reader.Read();

            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType != JsonTokenType.StartObject)
                {
                    reader.Skip();
                    continue;
                }

                string name = "";
                string continent = "";

                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    if (reader.ValueTextEquals("name"u8))
                    {
                        reader.Read();
                        name = Value(ref reader, strategy, lookup);
                    }
                    else if (reader.ValueTextEquals("continent"u8))
                    {
                        reader.Read();

                        if (reader.TokenType == JsonTokenType.StartObject)
                        {
                            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                            {
                                if (reader.ValueTextEquals("name"u8))
                                {
                                    reader.Read();
                                    continent = Value(ref reader, strategy, lookup);
                                }
                                else
                                {
                                    reader.Read();
                                    reader.Skip();
                                }
                            }
                        }
                        else
                        {
                            reader.Skip();
                        }
                    }
                    else
                    {
                        reader.Read();
                        reader.Skip();
                    }
                }

                rows.Add(new CountrySummary { Title = name, Continent = continent });
            }
        }

        return rows.Count;
    }

    /// <summary>One string, made whichever way the row under test calls for.</summary>
    private static string Value(
        ref Utf8JsonReader reader,
        Strategy strategy,
        Dictionary<string, string>.AlternateLookup<ReadOnlySpan<char>>? lookup)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return "";

        if (strategy == Strategy.GetString)
            return reader.GetString() ?? "";

        // Long enough for the values this payload carries; a real reader would fall back to the
        // getter for anything longer rather than risk the copy throwing.
        Span<char> buffer = stackalloc char[128];

        if (!reader.HasValueSequence && reader.ValueSpan.Length > buffer.Length)
            return reader.GetString() ?? "";

        int written = reader.CopyString(buffer);
        var value = buffer[..written];

        if (strategy == Strategy.CopyString || lookup is not { } cache)
            return new string(value);

        if (cache.TryGetValue(value, out string? existing))
            return existing;

        string made = new(value);
        cache[value] = made;

        return made;
    }
}
