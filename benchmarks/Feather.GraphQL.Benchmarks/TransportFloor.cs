using BenchmarkDotNet.Attributes;

namespace Feather.GraphQL.Benchmarks;

/// <summary>
/// What one request costs before any GraphQL client is involved.
/// </summary>
/// <remarks>
/// <para>
/// The number every other benchmark has to be read against. <c>HttpClient</c> has a real
/// per-request cost — building the message, running the handler pipeline, allocating the
/// response and its content — and with a canned transport that cost is most of a small query's
/// total. Without this row, a client that added nothing measurable would still look expensive.
/// </para>
/// <para>
/// <c>Parse</c> is the other floor: deserializing the reply is work no client can avoid, so the
/// difference between it and a client's total is the client's own overhead.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class TransportFloor
{
    private CannedTransport _transport = null!;
    private HttpClient _client = null!;
    private byte[] _body = null!;

    /// <summary>How many rows the canned reply carries.</summary>
    [Params(1, 100, 1000)]
    public int Rows { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _body = Payloads.Body(Rows);
        _transport = new CannedTransport(_body);
        _client = _transport.Client();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _client.Dispose();
        _transport.Dispose();
    }

    /// <summary>A POST and a full read of the reply, with nothing interpreting it.</summary>
    [Benchmark(Baseline = true, Description = "HTTP round trip only")]
    public async Task<int> RoundTrip()
    {
        using var content = new ByteArrayContent(_body);
        using var response = await _client.PostAsync((Uri?)null, content);

        return (await response.Content.ReadAsByteArrayAsync()).Length;
    }

    /// <summary>Deserializing the reply, with no transport at all.</summary>
    [Benchmark(Description = "Deserialize only")]
    public int Parse()
    {
        var document = System.Text.Json.JsonDocument.Parse(_body);

        return document.RootElement.GetProperty("data").GetProperty("countries").GetArrayLength();
    }
}
