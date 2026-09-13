using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Attributes;

namespace Feather.GraphQL.Serialization.Benchmarks;

/// <summary>
/// Whether a row should hold its values or hold where its values are.
/// </summary>
/// <remarks>
/// <para>
/// A generated row struct holds a <c>string</c> per selected field, which means the read allocates
/// one string per field per row. The alternative is for the row to hold offsets into the reply's
/// own bytes and for the strings to be made later — during the projection, if they are needed at
/// all.
/// </para>
/// <para>
/// The question that decides it is not how fast the loop gets. It is how many strings still have
/// to exist at the end. Two pairs are measured for that reason: one where the projection puts
/// every value into the result, which is what a projection normally does, and one where it only
/// compares them, which is where deferring can actually avoid the allocation rather than move it.
/// </para>
/// <para>
/// The rows are byte-for-byte the same size either way — two offsets and two lengths is four ints,
/// against two references — so nothing here is paid for by a fatter row.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class DeferredMaterialization
{
    private byte[] _narrow = null!;

    /// <inheritdoc cref="Payloads.Sizes"/>
    [Params(1, 25, 100)]
    public int Rows { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _narrow = Payloads.NarrowBody(Rows);

        if (Held() != Deferred() || HeldMatches() != DeferredMatches())
            throw new InvalidOperationException("the two representations disagree");
    }

    // ---- the projection keeps every value, which is the ordinary case ----

    /// <summary>Read the strings, then project them into the result.</summary>
    /// <remarks>What the generator emits today: one string per field per row, made during the read.</remarks>
    [Benchmark(Baseline = true, Description = "strings in row -> project")]
    public int Held()
    {
        var rows = ReadHeld(_narrow);
        var shaped = new CountrySummary[rows.Length];

        for (int i = 0; i < rows.Length; i++)
            shaped[i] = new CountrySummary { Title = rows[i].Name, Continent = rows[i].Continent };

        return shaped.Length;
    }

    /// <summary>Read where the strings are, then make them during the projection.</summary>
    /// <remarks>
    /// The same strings exist at the end, made at a different moment. If the projection needs
    /// every value — and the document only asked for values the projection named — then this
    /// cannot allocate less than the row above. It can only allocate the same, later.
    /// </remarks>
    [Benchmark(Description = "spans in row -> project")]
    public int Deferred()
    {
        var rows = ReadDeferred(_narrow);
        var shaped = new CountrySummary[rows.Length];

        for (int i = 0; i < rows.Length; i++)
        {
            shaped[i] = new CountrySummary
            {
                Title = Text(_narrow, rows[i].Name),
                Continent = Text(_narrow, rows[i].Continent)
            };
        }

        return shaped.Length;
    }

    // ---- the projection only compares, which is where deferring can win ----

    /// <summary>Read the strings, then compare them, having allocated every one.</summary>
    [Benchmark(Description = "strings in row -> compare")]
    public int HeldMatches()
    {
        var rows = ReadHeld(_narrow);
        int matched = 0;

        for (int i = 0; i < rows.Length; i++)
        {
            if (rows[i].Continent == "Europe")
                matched++;
        }

        return matched;
    }

    /// <summary>Compare the bytes, and never make the string at all.</summary>
    /// <remarks>
    /// The case the whole idea is for: a value that is read, used, and never handed to anyone can
    /// be used without existing as a <see cref="string"/>.
    /// </remarks>
    [Benchmark(Description = "spans in row -> compare")]
    public int DeferredMatches()
    {
        var rows = ReadDeferred(_narrow);
        int matched = 0;

        for (int i = 0; i < rows.Length; i++)
        {
            if (Matches(_narrow, rows[i].Continent, "Europe"u8))
                matched++;
        }

        return matched;
    }

    // ---- the two row shapes ----

    /// <summary>A row as the generator writes it now.</summary>
    private readonly record struct HeldRow(string Name, string Continent);

    /// <summary>
    /// Where a value is, rather than what it is.
    /// </summary>
    /// <remarks>
    /// A negative length means the bytes are escaped and are not yet text. Packing that into the
    /// sign keeps the slice eight bytes, so a row of two of them is exactly a row of two string
    /// references — otherwise the comparison would be paid for by a fatter row rather than won.
    /// </remarks>
    private readonly record struct Slice(int At, int Length)
    {
        public bool Escaped => Length < 0;

        public int Size => Length < 0 ? -Length : Length;
    }

    /// <summary>A row of offsets, the same width as a row of references.</summary>
    private readonly record struct DeferredRow(Slice Name, Slice Continent);

    /// <summary>
    /// Makes the text a slice stands for.
    /// </summary>
    /// <remarks>
    /// Unescaped bytes are already the text and transcode straight into a string. Escaped bytes
    /// are not text at all until something has interpreted the escapes, and the only thing here
    /// that knows how is the reader — so the value is read again, from its own bytes, quotes
    /// included because that is what makes them a JSON string.
    /// </remarks>
    private static string Text(byte[] json, Slice slice)
    {
        if (!slice.Escaped)
            return Encoding.UTF8.GetString(json.AsSpan(slice.At, slice.Size));

        var quoted = new Utf8JsonReader(json.AsSpan(slice.At - 1, slice.Size + 2));

        return quoted.Read() ? quoted.GetString()! : "";
    }

    /// <summary>Whether a slice holds the given text, without making a string when it need not.</summary>
    /// <remarks>
    /// An escaped value cannot be compared byte for byte against a plain literal — the bytes are
    /// <c>\u00e9</c> where the literal is <c>é</c> — so it has to be made before it can be
    /// matched, which is the allocation this was trying to avoid.
    /// </remarks>
    private static bool Matches(byte[] json, Slice slice, ReadOnlySpan<byte> text)
        => slice.Escaped
            ? Text(json, slice).AsSpan().SequenceEqual(Encoding.UTF8.GetString(text))
            : json.AsSpan(slice.At, slice.Size).SequenceEqual(text);

    private static HeldRow[] ReadHeld(byte[] json)
    {
        var reader = new Utf8JsonReader(json);

        if (!Locate(ref reader))
            return [];

        var rows = new List<HeldRow>();

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
                    name = reader.GetString()!;
                }
                else if (reader.ValueTextEquals("continent"u8))
                {
                    reader.Read();

                    while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                    {
                        if (reader.ValueTextEquals("name"u8))
                        {
                            reader.Read();
                            continent = reader.GetString()!;
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
                    reader.Read();
                    reader.Skip();
                }
            }

            rows.Add(new HeldRow(name, continent));
        }

        return rows.ToArray();
    }

    private static DeferredRow[] ReadDeferred(byte[] json)
    {
        var reader = new Utf8JsonReader(json);

        if (!Locate(ref reader))
            return [];

        var rows = new List<DeferredRow>();

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                continue;
            }

            Slice name = default;
            Slice continent = default;

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("name"u8))
                {
                    reader.Read();
                    name = Where(ref reader);
                }
                else if (reader.ValueTextEquals("continent"u8))
                {
                    reader.Read();

                    while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                    {
                        if (reader.ValueTextEquals("name"u8))
                        {
                            reader.Read();
                            continent = Where(ref reader);
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
                    reader.Read();
                    reader.Skip();
                }
            }

            rows.Add(new DeferredRow(name, continent));
        }

        return rows.ToArray();
    }

    /// <summary>
    /// Where the value the reader is standing on begins, and how long it is.
    /// </summary>
    /// <remarks>
    /// The token starts at its opening quote, so the value is one byte past it. Whether the bytes
    /// are escaped is recorded in the sign of the length, because they are not text until they
    /// have been unescaped and only the slice knows which kind it is.
    /// </remarks>
    private static Slice Where(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.String)
            return default;

        int length = reader.ValueSpan.Length;

        return new Slice((int)reader.TokenStartIndex + 1, reader.ValueIsEscaped ? -length : length);
    }

    /// <summary>Walks to the array the rows are in.</summary>
    private static bool Locate(ref Utf8JsonReader reader)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            return false;

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            bool isData = reader.ValueTextEquals("data"u8);

            reader.Read();

            if (!isData || reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                continue;
            }

            if (!reader.Read() || reader.TokenType != JsonTokenType.PropertyName)
                return false;

            reader.Read();

            return reader.TokenType == JsonTokenType.StartArray;
        }

        return false;
    }
}
