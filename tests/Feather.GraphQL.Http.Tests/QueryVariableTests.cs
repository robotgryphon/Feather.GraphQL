using System.Net;
using System.Text;
using System.Text.Json;
using Feather.GraphQL.Http;

namespace Feather.GraphQL.Http.Tests;

/// <summary>
/// Sending a document with the values its variables take.
/// </summary>
/// <remarks>
/// The assertions are on the bytes that went over, read back as JSON. What a caller hands in is a
/// dictionary of <see cref="object"/>, so the only thing standing between their value and the
/// wire is a type test — and a type test that falls through to the wrong case is a request a
/// server rejects rather than a compiler.
/// </remarks>
[TestFixture]
public class QueryVariableTests
{
    private const string Query = "query($v: String!) { countries(code: $v) { name } }";

    /// <summary>No variables sends no <c>variables</c> member.</summary>
    [Test]
    public async Task An_empty_map_sends_no_variables()
    {
        var sent = await Send(Query, new Dictionary<string, object?>());

        Assert.That(sent, Is.EqualTo("""{"query":"query($v: String!) { countries(code: $v) { name } }"}"""));
    }

    /// <summary>The document goes over unchanged, and the value lands beside it.</summary>
    [Test]
    public async Task A_value_lands_in_the_variables_member()
    {
        var sent = await Send(Query, new Dictionary<string, object?> { ["code"] = "EU" });

        using var parsed = JsonDocument.Parse(sent);

        Assert.Multiple(() =>
        {
            Assert.That(parsed.RootElement.GetProperty("query").GetString(), Is.EqualTo(Query));
            Assert.That(
                parsed.RootElement.GetProperty("variables").GetProperty("code").GetString(),
                Is.EqualTo("EU"));
        });
    }

    /// <summary>Several values need the separator a writer would have put in.</summary>
    [Test]
    public async Task Several_values_are_separated()
    {
        var sent = await Send(Query, new Dictionary<string, object?>
        {
            ["name"] = "Ada",
            ["age"] = 36,
            ["active"] = true
        });

        using var parsed = JsonDocument.Parse(sent);
        var variables = parsed.RootElement.GetProperty("variables");

        Assert.Multiple(() =>
        {
            Assert.That(variables.GetProperty("name").GetString(), Is.EqualTo("Ada"));
            Assert.That(variables.GetProperty("age").GetInt32(), Is.EqualTo(36));
            Assert.That(variables.GetProperty("active").GetBoolean(), Is.True);
        });
    }

    /// <summary>Every type the writer names survives the round trip.</summary>
    [Test]
    public async Task Every_supported_type_reads_back()
    {
        var guid = Guid.Parse("f3d1a0b2-4c5e-6789-abcd-ef0123456789");
        var moment = new DateTime(2026, 9, 13, 4, 5, 6, DateTimeKind.Utc);

        var sent = await Send(Query, new Dictionary<string, object?>
        {
            ["text"] = "value",
            ["i"] = 42,
            ["l"] = 9_000_000_000L,
            ["s"] = (short)7,
            ["d"] = 1.5d,
            ["m"] = 12.34m,
            ["g"] = guid,
            ["dt"] = moment,
            ["date"] = new DateOnly(2026, 9, 13),
            ["time"] = new TimeOnly(4, 5, 6),
            ["nothing"] = null
        });

        using var parsed = JsonDocument.Parse(sent);
        var v = parsed.RootElement.GetProperty("variables");

        Assert.Multiple(() =>
        {
            Assert.That(v.GetProperty("text").GetString(), Is.EqualTo("value"));
            Assert.That(v.GetProperty("i").GetInt32(), Is.EqualTo(42));
            Assert.That(v.GetProperty("l").GetInt64(), Is.EqualTo(9_000_000_000L));
            Assert.That(v.GetProperty("s").GetInt32(), Is.EqualTo(7));
            Assert.That(v.GetProperty("d").GetDouble(), Is.EqualTo(1.5d));
            Assert.That(v.GetProperty("m").GetDecimal(), Is.EqualTo(12.34m));
            Assert.That(v.GetProperty("g").GetString(), Is.EqualTo(guid.ToString("D")));
            Assert.That(v.GetProperty("dt").GetString(), Is.EqualTo(moment.ToString("O")));
            Assert.That(v.GetProperty("date").GetString(), Is.EqualTo("2026-09-13"));
            Assert.That(v.GetProperty("time").GetString(), Is.EqualTo("04:05:06.0000000"));
            Assert.That(v.GetProperty("nothing").ValueKind, Is.EqualTo(JsonValueKind.Null));
        });
    }

    /// <summary>An input object is a nested map, which is most of what a filter is.</summary>
    [Test]
    public async Task A_nested_map_is_an_input_object()
    {
        var sent = await Send(Query, new Dictionary<string, object?>
        {
            ["filter"] = new Dictionary<string, object?>
            {
                ["continent"] = new Dictionary<string, object?> { ["eq"] = "EU" }
            }
        });

        using var parsed = JsonDocument.Parse(sent);

        Assert.That(
            parsed.RootElement.GetProperty("variables").GetProperty("filter")
                .GetProperty("continent").GetProperty("eq").GetString(),
            Is.EqualTo("EU"));
    }

    /// <summary>A sequence is a list, whatever kind of sequence it is.</summary>
    [Test]
    public async Task A_sequence_is_a_list()
    {
        var sent = await Send(Query, new Dictionary<string, object?>
        {
            ["codes"] = new[] { "EU", "AS" },
            ["sizes"] = new List<object?> { 1, 2, 3 }
        });

        using var parsed = JsonDocument.Parse(sent);
        var v = parsed.RootElement.GetProperty("variables");

        Assert.Multiple(() =>
        {
            Assert.That(v.GetProperty("codes").EnumerateArray().Select(x => x.GetString()),
                Is.EqualTo(new[] { "EU", "AS" }));

            Assert.That(v.GetProperty("sizes").GetArrayLength(), Is.EqualTo(3));
        });
    }

    /// <summary>
    /// A string is a sequence, and must not be written as one.
    /// </summary>
    /// <remarks>
    /// The case the type switch exists to get right. Ordered wrongly it would send
    /// <c>["E","U"]</c>, which a server rejects and no compiler would have caught.
    /// </remarks>
    [Test]
    public async Task A_string_is_not_written_as_a_sequence()
    {
        var sent = await Send(Query, new Dictionary<string, object?> { ["code"] = "EU" });

        using var parsed = JsonDocument.Parse(sent);

        Assert.That(
            parsed.RootElement.GetProperty("variables").GetProperty("code").ValueKind,
            Is.EqualTo(JsonValueKind.String));
    }

    /// <summary>An enum travels as its name.</summary>
    [Test]
    public async Task An_enum_is_written_as_its_name()
    {
        var sent = await Send(Query, new Dictionary<string, object?> { ["kind"] = Shade.Deep });

        using var parsed = JsonDocument.Parse(sent);

        Assert.That(
            parsed.RootElement.GetProperty("variables").GetProperty("kind").GetString(),
            Is.EqualTo("Deep"));
    }

    /// <summary>A value that would otherwise end the JSON string it sits in.</summary>
    [Test]
    public async Task Values_are_escaped()
    {
        var sent = await Send(Query, new Dictionary<string, object?> { ["code"] = "EU\" } injected" });

        using var parsed = JsonDocument.Parse(sent);

        Assert.That(
            parsed.RootElement.GetProperty("variables").GetProperty("code").GetString(),
            Is.EqualTo("EU\" } injected"));
    }

    /// <summary>A name that needs escaping is escaped too.</summary>
    [Test]
    public async Task Names_are_escaped()
    {
        var sent = await Send(Query, new Dictionary<string, object?> { ["od\"d"] = 1 });

        using var parsed = JsonDocument.Parse(sent);

        Assert.That(parsed.RootElement.GetProperty("variables").GetProperty("od\"d").GetInt32(), Is.EqualTo(1));
    }

    /// <summary>A type with no rule is refused rather than guessed at.</summary>
    [Test]
    public void An_unsupported_type_is_refused()
    {
        var handler = new CapturingHandler("""{"data":{}}""");
        using var client = handler.Client();

        Assert.That(
            async () => await client.SendGraphQLQueryAsync(
                Query, new Dictionary<string, object?> { ["thing"] = new object() }),
            Throws.TypeOf<NotSupportedException>());
    }

    private enum Shade { Pale, Deep }

    /// <summary>Sends, and gives back the body that went over.</summary>
    private static async Task<string> Send(string query, IReadOnlyDictionary<string, object?> variables)
    {
        var handler = new CapturingHandler("""{"data":{}}""");
        using var client = handler.Client();

        using var response = await client.SendGraphQLQueryAsync(query, variables);

        return handler.SentBody ?? "";
    }

    /// <summary>Answers from a canned body, and records what it was sent.</summary>
    private sealed class CapturingHandler(string json) : HttpMessageHandler
    {
        public string? SentBody { get; private set; }

        public HttpClient Client() => new(this) { BaseAddress = new Uri("https://example.test/") };

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            SentBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }
}
