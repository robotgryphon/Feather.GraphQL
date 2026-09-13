using System.Buffers;
using System.Text.Json;
using BenchmarkDotNet.Attributes;

namespace Feather.GraphQL.Serialization.Benchmarks;

/// <summary>
/// How a generated reader should collect the rows it reads.
/// </summary>
/// <remarks>
/// <para>
/// A reader knows the rows are coming but not how many, so it accumulates them and hands back an
/// array. What it accumulates into is the question here: the rows arrive one at a time, the count
/// is only known once the array ends, and every strategy for that trades allocation against
/// passes over the bytes.
/// </para>
/// <para>
/// Reading is identical across the rows — the same walk, the same <see cref="Row.Read"/> per
/// element. Only the accumulation differs, so the difference between the numbers is the
/// accumulation and nothing else.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class RowAccumulation
{
    private byte[] _narrow = null!;

    /// <summary>
    /// Sizes, including none at all.
    /// </summary>
    /// <remarks>
    /// Zero is not a curiosity: an empty result and a failed one both arrive as no rows, and a
    /// reader that allocates to hold them has allocated for nothing.
    /// </remarks>
    [Params(0, 1, 25, 100)]
    public int Rows { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _narrow = Payloads.NarrowBody(Rows);

        // Every row here reads the same payload and must find the same rows in it. A strategy that
        // is fast because it reads fewer of them is not faster, and a benchmark that never checks
        // is where that would hide.
        if (Listed() != Rows || Pooled() != Rows || Lazy() != Rows
            || Counted() != Rows || Scanned() != Rows || Sliced() != Rows || Parallelised() != Rows)
        {
            throw new InvalidOperationException($"strategies disagree at {Rows} rows");
        }

        Awkward();
    }

    /// <summary>
    /// The case a scan can get wrong that a tokenizer cannot.
    /// </summary>
    /// <remarks>
    /// Braces and quotes inside string values are characters, not structure. Bogus does not
    /// generate country names containing them, so a payload that does is written by hand here —
    /// otherwise the scan would be checked only against data that cannot catch its one real risk.
    /// </remarks>
    private static void Awkward()
    {
        byte[] json = """
            {"data":{"countries":[
                {"name":"{not a brace}","continent":{"name":"Europe"}},
                {"name":"quote \" and backslash \\","continent":{"name":"Asia"}},
                {"name":"]}","continent":{"name":"]["}}
            ]}}
            """u8.ToArray();

        var reader = new Utf8JsonReader(json);

        if (!Locate(ref reader))
            throw new InvalidOperationException("the awkward payload did not locate");

        int scanned = Scan(json, (int)reader.BytesConsumed, default, default);

        if (scanned != 3)
            throw new InvalidOperationException($"the scan found {scanned} rows in a payload of 3");

        int from = (int)reader.BytesConsumed;
        int[] starts = new int[3];
        int[] lengths = new int[3];

        Scan(json, from, starts, lengths);

        // Each slice has to be the row and the whole row, which is only true if the boundaries the
        // scan recorded were the real ones.
        for (int i = 0; i < 3; i++)
        {
            if (ReadOne(json.AsSpan(starts[i], lengths[i])).Continent.Length == 0)
                throw new InvalidOperationException($"row {i} sliced wrong");
        }
    }

    /// <summary>A list, grown by doubling, copied once into an array at the end.</summary>
    /// <remarks>
    /// What the generator emits today. The list is allocated before the walk begins, so it exists
    /// even for a reply that carries nothing, and reaching 25 rows allocates backing arrays of 4,
    /// 8, 16 and 32 on the way — each one copying what the last one held — before
    /// <c>ToArray</c> copies a fifth time into the array that is finally returned.
    /// </remarks>
    [Benchmark(Baseline = true, Description = "List + ToArray")]
    public int Listed()
    {
        var reader = new Utf8JsonReader(_narrow);

        if (!Locate(ref reader))
            return 0;

        var rows = new List<Row>();

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.StartObject)
                rows.Add(Row.Read(ref reader));
            else
                reader.Skip();
        }

        return rows.ToArray().Length;
    }

    /// <summary>A rented buffer, grown by doubling, copied once into an exact array.</summary>
    /// <remarks>
    /// The same doubling, but the intermediates come from the pool and go back to it, so the only
    /// thing the GC sees is the array that is returned. The rows are structs holding strings, so
    /// the buffer is returned cleared — otherwise the last reply's strings stay reachable through
    /// the pool until something else rents the same array.
    /// </remarks>
    [Benchmark(Description = "pooled + exact copy")]
    public int Pooled()
    {
        var reader = new Utf8JsonReader(_narrow);

        if (!Locate(ref reader))
            return 0;

        var buffer = ArrayPool<Row>.Shared.Rent(16);
        int at = 0;

        try
        {
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType != JsonTokenType.StartObject)
                {
                    reader.Skip();
                    continue;
                }

                if (at == buffer.Length)
                {
                    var grown = ArrayPool<Row>.Shared.Rent(at * 2);

                    buffer.AsSpan(0, at).CopyTo(grown);
                    ArrayPool<Row>.Shared.Return(buffer, clearArray: true);

                    buffer = grown;
                }

                buffer[at++] = Row.Read(ref reader);
            }

            if (at == 0)
                return 0;

            var rows = new Row[at];

            buffer.AsSpan(0, at).CopyTo(rows);

            return rows.Length;
        }
        finally
        {
            ArrayPool<Row>.Shared.Return(buffer, clearArray: true);
        }
    }

    /// <summary>The same, but nothing is rented until there is a row to put in it.</summary>
    /// <remarks>
    /// A reply that carries no rows should touch neither the pool nor the heap, and an empty
    /// buffer to begin with gets that for free: the capacity check the growth already needs is
    /// <c>0 == 0</c> on the first row, so the rent happens there rather than before the walk, and
    /// never at all when the walk finds nothing.
    /// </remarks>
    [Benchmark(Description = "pooled, rented on first row")]
    public int Lazy()
    {
        var reader = new Utf8JsonReader(_narrow);

        if (!Locate(ref reader))
            return 0;

        var buffer = Array.Empty<Row>();
        int at = 0;

        try
        {
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType != JsonTokenType.StartObject)
                {
                    reader.Skip();
                    continue;
                }

                if (at == buffer.Length)
                {
                    var grown = ArrayPool<Row>.Shared.Rent(at == 0 ? 16 : at * 2);

                    if (at > 0)
                    {
                        buffer.AsSpan(0, at).CopyTo(grown);
                        ArrayPool<Row>.Shared.Return(buffer, clearArray: true);
                    }

                    buffer = grown;
                }

                buffer[at++] = Row.Read(ref reader);
            }

            if (at == 0)
                return 0;

            var rows = new Row[at];

            buffer.AsSpan(0, at).CopyTo(rows);

            return rows.Length;
        }
        finally
        {
            // Array.Empty is not the pool's to take back, and it is what a reply with no rows left
            // the buffer as.
            if (buffer.Length > 0)
                ArrayPool<Row>.Shared.Return(buffer, clearArray: true);
        }
    }

    /// <summary>Counted first from a copy of the reader, then filled into an exact array.</summary>
    /// <remarks>
    /// A <c>Utf8JsonReader</c> is a struct over bytes it does not own, so copying one costs a
    /// struct copy and gives a second cursor over the same payload. Counting with it means the
    /// array is allocated once, at exactly the right size, with nothing to grow and nothing to
    /// copy — paid for by tokenizing the rows twice.
    /// </remarks>
    [Benchmark(Description = "pre-count + exact fill")]
    public int Counted()
    {
        var reader = new Utf8JsonReader(_narrow);

        if (!Locate(ref reader))
            return 0;

        // Only the objects are counted, so a row the server could not resolve — which arrives as a
        // null and is not read — does not leave a gap the fill would have to trim away after.
        var counting = reader;
        int count = 0;

        while (counting.Read() && counting.TokenType != JsonTokenType.EndArray)
        {
            if (counting.TokenType == JsonTokenType.StartObject)
                count++;

            counting.Skip();
        }

        if (count == 0)
        {
            reader.Skip();

            return 0;
        }

        var rows = new Row[count];
        int at = 0;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.StartObject)
                rows[at++] = Row.Read(ref reader);
            else
                reader.Skip();
        }

        return rows.Length;
    }

    /// <summary>Rows found by scanning for their braces, then filled into an exact array.</summary>
    /// <remarks>
    /// <para>
    /// The same idea as the pre-count above, with a far cheaper counter. Finding where a row ends
    /// does not need the value of anything inside it — only its braces, and only the ones that are
    /// not inside a string — so the scan classifies no tokens, parses no numbers and compares no
    /// property names. It looks for four bytes and vectorizes the search for them, skipping whole
    /// runs of payload that tokenizing would have had to walk one token at a time.
    /// </para>
    /// <para>
    /// What it buys over the pooled rows is the growth: the count is known before a row is read,
    /// so the array is allocated once at the right size and filled in place, with no buffer to
    /// rent, no doubling, and no copy at the end.
    /// </para>
    /// </remarks>
    [Benchmark(Description = "brace scan + exact fill")]
    public int Scanned()
    {
        var reader = new Utf8JsonReader(_narrow);

        if (!Locate(ref reader))
            return 0;

        int count = Scan(_narrow, (int)reader.BytesConsumed, default, default);

        if (count == 0)
            return 0;

        var rows = new Row[count];
        int at = 0;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.StartObject)
                rows[at++] = Row.Read(ref reader);
            else
                reader.Skip();
        }

        return rows.Length;
    }

    /// <summary>The same scan, but keeping where each row began and how long it was.</summary>
    /// <remarks>
    /// The scan already passes over every row boundary, so recording them costs little more than
    /// counting them — and a row's bytes are self-contained JSON, so each one can be read on its
    /// own from its own slice rather than by carrying one cursor through all of them.
    /// </remarks>
    [Benchmark(Description = "brace scan + sliced reads")]
    public int Sliced()
    {
        var reader = new Utf8JsonReader(_narrow);

        if (!Locate(ref reader))
            return 0;

        int from = (int)reader.BytesConsumed;
        int count = Scan(_narrow, from, default, default);

        if (count == 0)
            return 0;

        int[] starts = ArrayPool<int>.Shared.Rent(count);
        int[] lengths = ArrayPool<int>.Shared.Rent(count);

        try
        {
            Scan(_narrow, from, starts.AsSpan(0, count), lengths.AsSpan(0, count));

            var rows = new Row[count];

            for (int i = 0; i < count; i++)
                rows[i] = ReadOne(_narrow.AsSpan(starts[i], lengths[i]));

            return rows.Length;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(starts);
            ArrayPool<int>.Shared.Return(lengths);
        }
    }

    /// <summary>The sliced reads, run across the thread pool instead of one after another.</summary>
    /// <remarks>
    /// Each slice is independent, so nothing here needs synchronising — every iteration writes its
    /// own index of its own array. Whether independence is worth dispatching for is the question:
    /// the work per row is a few hundred nanoseconds, and a parallel loop costs what it costs
    /// before the first row is read.
    /// </remarks>
    [Benchmark(Description = "brace scan + parallel reads")]
    public int Parallelised()
    {
        var reader = new Utf8JsonReader(_narrow);

        if (!Locate(ref reader))
            return 0;

        int from = (int)reader.BytesConsumed;
        int count = Scan(_narrow, from, default, default);

        if (count == 0)
            return 0;

        int[] starts = ArrayPool<int>.Shared.Rent(count);
        int[] lengths = ArrayPool<int>.Shared.Rent(count);

        try
        {
            Scan(_narrow, from, starts.AsSpan(0, count), lengths.AsSpan(0, count));

            var rows = new Row[count];
            byte[] json = _narrow;

            Parallel.For(0, count, i => rows[i] = ReadOne(json.AsSpan(starts[i], lengths[i])));

            return rows.Length;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(starts);
            ArrayPool<int>.Shared.Return(lengths);
        }
    }

    /// <summary>The bytes that mean something to a scan that is only looking for row boundaries.</summary>
    private static readonly SearchValues<byte> _structural =
        SearchValues.Create("\"{}]"u8);

    /// <summary>The bytes that end a string, or escape whatever follows them.</summary>
    private static readonly SearchValues<byte> _inString =
        SearchValues.Create("\"\\"u8);

    /// <summary>
    /// Finds the top-level objects of the array beginning at <paramref name="from"/>.
    /// </summary>
    /// <remarks>
    /// Returns how many there are, and where each one is when there is somewhere to record it.
    /// Strings are stepped over rather than examined, because a brace inside one is a character
    /// and not a boundary — which is the whole of what makes this cheaper than tokenizing, and
    /// also the whole of what it could get wrong if it did not.
    /// </remarks>
    private static int Scan(ReadOnlySpan<byte> json, int from, Span<int> starts, Span<int> lengths)
    {
        int at = from;
        int depth = 0;
        int count = 0;
        int start = 0;

        while (at < json.Length)
        {
            int next = json.Slice(at).IndexOfAny(_structural);

            if (next < 0)
                break;

            at += next;

            switch (json[at])
            {
                case (byte)'"':
                    at = EndOfString(json, at + 1);
                    break;

                case (byte)'{':
                    if (depth == 0)
                        start = at;

                    depth++;
                    at++;
                    break;

                case (byte)'}':
                    depth--;
                    at++;

                    if (depth != 0)
                        break;

                    if (!starts.IsEmpty && count < starts.Length)
                    {
                        starts[count] = start;
                        lengths[count] = at - start;
                    }

                    count++;
                    break;

                // The array's own close, which only ends it when no row is still open.
                default:
                    if (depth == 0)
                        return count;

                    at++;
                    break;
            }
        }

        return count;
    }

    /// <summary>Steps over a string literal, escapes included, to the byte after its close.</summary>
    private static int EndOfString(ReadOnlySpan<byte> json, int at)
    {
        while (at < json.Length)
        {
            int next = json.Slice(at).IndexOfAny(_inString);

            if (next < 0)
                return json.Length;

            at += next;

            // A backslash spends the byte after it, whatever that byte is.
            if (json[at] == (byte)'\\')
            {
                at += 2;
                continue;
            }

            return at + 1;
        }

        return at;
    }

    /// <summary>Reads one row from bytes that hold that row and nothing else.</summary>
    private static Row ReadOne(ReadOnlySpan<byte> slice)
    {
        var reader = new Utf8JsonReader(slice);

        return reader.Read() && reader.TokenType == JsonTokenType.StartObject
            ? Row.Read(ref reader)
            : default;
    }

    /// <summary>
    /// Walks to the array the rows are in, as a generated reply reader does.
    /// </summary>
    /// <remarks>
    /// The envelope in miniature: <c>data</c>, its single field, and the array that field holds.
    /// Shared by the rows so the walk is not part of what separates them.
    /// </remarks>
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

    /// <summary>One row, as a generated reader models it: a field per field, and nothing else.</summary>
    private readonly struct Row
    {
        public readonly string Name;
        public readonly string Continent;

        private Row(string name, string continent)
        {
            Name = name;
            Continent = continent;
        }

        public static Row Read(ref Utf8JsonReader reader)
        {
            string name = "";
            string continent = "";

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("name"u8))
                {
                    reader.Read();
                    name = reader.TokenType == JsonTokenType.Null ? "" : reader.GetString()!;
                }
                else if (reader.ValueTextEquals("continent"u8))
                {
                    reader.Read();

                    if (reader.TokenType != JsonTokenType.StartObject)
                    {
                        reader.Skip();
                        continue;
                    }

                    while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                    {
                        if (reader.ValueTextEquals("name"u8))
                        {
                            reader.Read();
                            continent = reader.TokenType == JsonTokenType.Null ? "" : reader.GetString()!;
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

            return new Row(name, continent);
        }
    }
}
