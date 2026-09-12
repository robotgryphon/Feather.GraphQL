using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;

namespace Feather.GraphQL.Serialization.Benchmarks;

/// <summary>
/// How a compiled projection should walk the rows it shapes.
/// </summary>
/// <remarks>
/// <para>
/// The generator emits an indexed <c>for</c> over the rows that were read, writing each shaped
/// row into an array sized once. This asks whether that is the right loop — against the same work
/// done through spans, through raw references, and across threads.
/// </para>
/// <para>
/// Read every number here against <see cref="ReplyDeserialization"/>, which is the only thing
/// that makes them mean anything: reading the rows costs far more than walking them afterwards,
/// and a loop that wins a microsecond has won a fraction of the read that produced its input.
/// </para>
/// <para>
/// Two rows are deliberately absent. Anything using the <c>unsafe</c> keyword cannot be emitted
/// at all — generated code lands in the <em>consumer's</em> compilation, and a generator cannot
/// turn on <c>AllowUnsafeBlocks</c> for them — and <c>Span&lt;T&gt;</c> cannot cross the
/// <c>await</c> the shaping sits after without restructuring the method around it.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class ShapingStrategies
{
    private Country[] _source = null!;

    /// <inheritdoc cref="Payloads.Sizes"/>
    [Params(1, 25, 100, 500)]
    public int Rows { get; set; }

    [GlobalSetup]
    public void Setup() => _source = Payloads.Rows(Rows);

    /// <summary>What the generator emits today.</summary>
    [Benchmark(Baseline = true, Description = "for + index")]
    public CountrySummary[] Indexed()
    {
        var source = _source;
        var rows = new CountrySummary[source.Length];

        for (int i = 0; i < source.Length; i++)
        {
            var c = source[i];

            rows[i] = new CountrySummary { Title = c.Name, Continent = c.Continent.Name };
        }

        return rows;
    }

    /// <summary>The same loop over spans, which the JIT can drop the bounds checks on.</summary>
    [Benchmark(Description = "for + span")]
    public CountrySummary[] Spans()
    {
        var source = _source.AsSpan();
        var rows = new CountrySummary[source.Length];
        var target = rows.AsSpan();

        for (int i = 0; i < source.Length; i++)
        {
            var c = source[i];

            target[i] = new CountrySummary { Title = c.Name, Continent = c.Continent.Name };
        }

        return rows;
    }

    /// <summary>
    /// Raw references, which is as close to the metal as generated code can get without the
    /// <c>unsafe</c> keyword the consumer's project may not allow.
    /// </summary>
    [Benchmark(Description = "ref + Unsafe.Add")]
    public CountrySummary[] References()
    {
        var source = _source;
        var rows = new CountrySummary[source.Length];

        ref var read = ref MemoryMarshal.GetArrayDataReference(source);
        ref var write = ref MemoryMarshal.GetArrayDataReference(rows);

        for (nint i = 0; i < source.Length; i++)
        {
            var c = Unsafe.Add(ref read, i);

            Unsafe.Add(ref write, i) = new CountrySummary { Title = c.Name, Continent = c.Continent.Name };
        }

        return rows;
    }

    /// <summary>Across threads, which is the question this class exists to answer honestly.</summary>
    [Benchmark(Description = "Parallel.For")]
    public CountrySummary[] Parallel()
    {
        var source = _source;
        var rows = new CountrySummary[source.Length];

        System.Threading.Tasks.Parallel.For(0, source.Length, i =>
        {
            var c = source[i];

            rows[i] = new CountrySummary { Title = c.Name, Continent = c.Continent.Name };
        });

        return rows;
    }

    /// <summary>What the runtime path does, for scale: a delegate per row through LINQ.</summary>
    [Benchmark(Description = "LINQ Select")]
    public CountrySummary[] Linq()
        => _source
            .Select(c => new CountrySummary { Title = c.Name, Continent = c.Continent.Name })
            .ToArray();

    /// <summary>
    /// A projection that allocates nothing, where the loop is all there is.
    /// </summary>
    /// <remarks>
    /// The object-creating rows above allocate once per row, and no loop shape can undo that. This
    /// one isolates the walking itself, which is where a better loop would show up if anywhere.
    /// </remarks>
    [Benchmark(Description = "for + index, no allocation")]
    public string[] IndexedScalar()
    {
        var source = _source;
        var rows = new string[source.Length];

        for (int i = 0; i < source.Length; i++)
            rows[i] = source[i].Name;

        return rows;
    }

    /// <inheritdoc cref="IndexedScalar"/>
    [Benchmark(Description = "ref + Unsafe.Add, no allocation")]
    public string[] ReferencesScalar()
    {
        var source = _source;
        var rows = new string[source.Length];

        ref var read = ref MemoryMarshal.GetArrayDataReference(source);
        ref var write = ref MemoryMarshal.GetArrayDataReference(rows);

        for (nint i = 0; i < source.Length; i++)
            Unsafe.Add(ref write, i) = Unsafe.Add(ref read, i).Name;

        return rows;
    }
}
