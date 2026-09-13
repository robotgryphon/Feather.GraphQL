using System.Buffers;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Feather.GraphQL.Serialization;

namespace Feather.GraphQL.Serialization.Benchmarks;

/// <summary>
/// What it costs a caller to build a request body with a variable in it.
/// </summary>
/// <remarks>
/// <para>
/// The filtered string row allocates about three times what the unfiltered one does, for a reply
/// of the same size. The unfiltered path sends a constant; this one builds an envelope per
/// request. These rows say how much of the difference is the building, and which way of building
/// it is responsible.
/// </para>
/// <para>
/// Sending and reading are deliberately absent. The reply is identical either way, so measuring
/// it again would only bury the part that differs.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class EnvelopeBuilding
{
    private const string Query =
        "query($continent: String!) { countries(filter: { continent: { eq: $continent } }) "
        + "{ name continent { name } } }";

    private const string Continent = "EU";

    /// <summary>What the benchmark's filtered row does today.</summary>
    /// <remarks>
    /// A serializer per call over a growable buffer. The obvious way to write it, and the thing
    /// under suspicion.
    /// </remarks>
    [Benchmark(Baseline = true, Description = "ArrayBufferWriter + Utf8JsonWriter")]
    public int Serializer()
    {
        var buffer = new ArrayBufferWriter<byte>(256);

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("query"u8, Query);
            writer.WritePropertyName("variables"u8);
            writer.WriteStartObject();
            writer.WriteString("continent", Continent);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return buffer.WrittenMemory.Length;
    }

    /// <summary>The same bytes, with the buffer sized for them up front.</summary>
    /// <remarks>
    /// Separates "a serializer is expensive" from "the buffer grew". If this is most of the
    /// saving, the cost was doubling a 256-byte buffer to fit a ~180-byte body plus whatever the
    /// writer asked for in one go.
    /// </remarks>
    [Benchmark(Description = "ArrayBufferWriter, pre-sized")]
    public int Sized()
    {
        var buffer = new ArrayBufferWriter<byte>(512);

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("query"u8, Query);
            writer.WritePropertyName("variables"u8);
            writer.WriteStartObject();
            writer.WriteString("continent", Continent);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return buffer.WrittenMemory.Length;
    }

    /// <summary>The same bytes through the pooled body the generated path uses.</summary>
    /// <remarks>
    /// The document is still escaped per call, as a hand-written caller's would be — what changes
    /// is that the buffer is rented rather than allocated, and no serializer is constructed.
    /// </remarks>
    [Benchmark(Description = "PooledBody")]
    public int Pooled()
    {
        using var body = PooledBody.Rent(256);

        body.WriteRaw("{\"query\":"u8);
        body.Write(Query);
        body.WriteRaw(",\"variables\":{\"continent\":"u8);
        body.Write(Continent);
        body.WriteRaw("}}"u8);

        return body.Written.Length;
    }

    /// <summary>
    /// The floor: the document escaped once, the value written per call.
    /// </summary>
    /// <remarks>
    /// What a compiled query does, written by hand. Not a fair comparison for the benchmark's
    /// baseline row — a caller sending their own document has not had a compiler escape it for
    /// them — but it says how much of the cost is the escaping rather than the buffer.
    /// </remarks>
    [Benchmark(Description = "PooledBody, document pre-escaped")]
    public int Prepared()
    {
        using var body = PooledBody.Rent(256);

        body.WriteRaw(_prefix);
        body.Write(Continent);
        body.WriteRaw("}}"u8);

        return body.Written.Length;
    }

    /// <summary>The constant half of the body, escaped once.</summary>
    private static readonly byte[] _prefix = Prefix();

    private static byte[] Prefix()
    {
        using var body = PooledBody.Rent(256);

        body.WriteRaw("{\"query\":"u8);
        body.Write(Query);
        body.WriteRaw(",\"variables\":{\"continent\":"u8);

        return body.Written.ToArray();
    }
}
