using System.Buffers;
using System.Text;
using System.Text.Json;
using Feather.GraphQL.Serialization;

namespace Feather.GraphQL.Http.Tests;

/// <summary>
/// The body builder a compiled query writes its request into.
/// </summary>
/// <remarks>
/// The escaping is the part worth testing hardest. It replaces <c>Utf8JsonWriter</c> on the path
/// that puts caller values on the wire, so getting it wrong is a malformed request or an injected
/// one — not a slow one. Every case here is therefore checked against what
/// <c>Utf8JsonWriter</c> itself produces rather than against text written out by hand: the
/// question is never "is this the escaping I expected" but "is this the escaping the framework
/// would have done".
/// </remarks>
[TestFixture]
public class PooledBodyTests
{
    /// <summary>Strings whose escaping is not the identity.</summary>
    /// <remarks>
    /// Quotes and backslashes because they end or escape the literal; control characters because
    /// they are illegal raw; non-ASCII because the default encoder escapes it, which is what makes
    /// most real-world text take the slow path; and the awkward ones — a lone surrogate, an empty
    /// string — because they are where an encoder is most likely to disagree with itself.
    /// </remarks>
    private static readonly string[] _values =
    [
        "",
        "plain",
        "with \"quotes\"",
        "back\\slash",
        "new\nline",
        "tab\there",
        "bell\a here",
        "carriage\rreturn",
        "null\0byte",
        "escape\u001bsequence",
        "delete\u007fchar",
        "Côte d'Ivoire",
        "Curaçao",
        "日本語",
        "emoji 🎉 here",
        "\ud83c", // a high surrogate with nothing after it
        "</script>",
        "a&b",
        "'apostrophe'",
        new string('x', 5000),
        new string('é', 2000)
    ];

    /// <summary>A written string is the string <c>Utf8JsonWriter</c> would have written.</summary>
    [Test]
    public void Strings_are_escaped_as_the_framework_escapes_them([ValueSource(nameof(_values))] string value)
    {
        using var body = PooledBody.Rent(8);

        body.Write(value);

        Assert.That(Utf8(body), Is.EqualTo(Reference(writer => writer.WriteStringValue(value))));
    }

    /// <summary>And reads back as the same string it went in as.</summary>
    /// <remarks>
    /// Escaping correctly and escaping reversibly are different claims. This is the second one,
    /// and it is the one a server actually depends on.
    /// </remarks>
    [Test]
    public void Strings_round_trip_through_a_reader([ValueSource(nameof(_values))] string value)
    {
        using var body = PooledBody.Rent(8);

        body.Write(value);

        // Compared against the framework's own round-trip rather than against the input, because
        // the input is not always what comes back: a lone surrogate is not encodable, and both
        // encoders replace it. Agreeing on that is the claim; preserving it is not.
        byte[] reference = Encoding.UTF8.GetBytes(Reference(writer => writer.WriteStringValue(value)));

        Assert.That(ReadBack(body.Written.Span), Is.EqualTo(ReadBack(reference)));
    }

    /// <summary>The string one JSON string token holds.</summary>
    private static string? ReadBack(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json);

        return reader.Read() ? reader.GetString() : null;
    }

    /// <summary>A null string is a JSON null, not an empty one.</summary>
    [Test]
    public void Null_is_written_as_null()
    {
        using var body = PooledBody.Rent(8);

        body.Write((string?)null);

        Assert.That(Utf8(body), Is.EqualTo("null"));
    }

    /// <summary>Numbers and the rest format as the framework formats them.</summary>
    [Test]
    public void Values_are_written_as_the_framework_writes_them()
    {
        var guid = Guid.Parse("f3d1a0b2-4c5e-6789-abcd-ef0123456789");
        var moment = new DateTime(2026, 9, 12, 21, 28, 44, DateTimeKind.Utc);
        var offset = new DateTimeOffset(moment, TimeSpan.Zero);

        Assert.Multiple(() =>
        {
            Assert.That(Written(b => b.Write(0)), Is.EqualTo(Reference(w => w.WriteNumberValue(0))));
            Assert.That(Written(b => b.Write(int.MinValue)), Is.EqualTo(Reference(w => w.WriteNumberValue(int.MinValue))));
            Assert.That(Written(b => b.Write(long.MaxValue)), Is.EqualTo(Reference(w => w.WriteNumberValue(long.MaxValue))));
            Assert.That(Written(b => b.Write(1.5d)), Is.EqualTo(Reference(w => w.WriteNumberValue(1.5d))));
            Assert.That(Written(b => b.Write(12.34m)), Is.EqualTo(Reference(w => w.WriteNumberValue(12.34m))));
            Assert.That(Written(b => b.Write(true)), Is.EqualTo("true"));
            Assert.That(Written(b => b.Write(false)), Is.EqualTo("false"));
            Assert.That(Written(b => b.WriteNull()), Is.EqualTo("null"));

            // The quoted ones are text a server parses back, so the format matters more than the
            // framework's own choice of it: round-trip is the claim.
            Assert.That(Written(b => b.Write(guid)), Is.EqualTo($"\"{guid:D}\""));
            Assert.That(Written(b => b.Write(moment)), Is.EqualTo($"\"{moment:O}\""));
            Assert.That(Written(b => b.Write(offset)), Is.EqualTo($"\"{offset:O}\""));
            Assert.That(Written(b => b.Write(new DateOnly(2026, 9, 12))), Is.EqualTo("\"2026-09-12\""));
            Assert.That(Written(b => b.Write(new TimeOnly(21, 28, 44))), Is.EqualTo("\"21:28:44.0000000\""));
        });
    }

    /// <summary>Doubles that a careless formatter would round away.</summary>
    /// <remarks>
    /// A number written with fewer digits than it holds is a number that comes back different.
    /// The framework round-trips them, so this has to as well.
    /// </remarks>
    [Test]
    public void Doubles_keep_every_digit_they_need(
        [Values(0.1d, 1e-300, 1.7976931348623157e308, 3.141592653589793d, -0.0d)] double value)
    {
        Assert.That(Written(b => b.Write(value)), Is.EqualTo(Reference(w => w.WriteNumberValue(value))));
    }

    /// <summary>Raw bytes go through untouched, which is what constants depend on.</summary>
    [Test]
    public void Raw_json_is_copied_verbatim()
    {
        using var body = PooledBody.Rent(8);

        body.WriteRaw("{\"query\":\"{ a }\",\"variables\":{"u8);
        body.Write("v");
        body.WriteRaw("}}"u8);

        Assert.That(Utf8(body), Is.EqualTo("{\"query\":\"{ a }\",\"variables\":{\"v\"}}"));
    }

    /// <summary>A body grows past the capacity it was rented with, keeping what it held.</summary>
    /// <remarks>
    /// Deliberately rented far too small: growth has to copy, and a copy that loses or reorders
    /// what came before would only show up once it happened more than once.
    /// </remarks>
    [Test]
    public void Growing_preserves_everything_written_before_it()
    {
        using var body = PooledBody.Rent(1);
        var expected = new StringBuilder();

        for (int i = 0; i < 500; i++)
        {
            body.Write(i);
            body.WriteRaw(","u8);
            body.Write($"value-{i}-é");
            body.WriteRaw(","u8);

            expected.Append(i).Append(',').Append(Reference(w => w.WriteStringValue($"value-{i}-é"))).Append(',');
        }

        Assert.That(Utf8(body), Is.EqualTo(expected.ToString()));
    }

    /// <summary>A whole envelope, read back by the reader that will read it on the server.</summary>
    [Test]
    public void A_built_body_parses_as_json()
    {
        using var body = PooledBody.Rent(64);

        body.WriteRaw("{\"query\":\"query($code:String!){ countries(filter:{eq:$code}) { name } }\",\"variables\":{\"code\":"u8);
        body.Write("EU\" injected");
        body.WriteRaw("}}"u8);

        using var parsed = JsonDocument.Parse(body.Written);

        Assert.Multiple(() =>
        {
            Assert.That(parsed.RootElement.GetProperty("query").GetString(), Does.StartWith("query($code:String!)"));
            Assert.That(
                parsed.RootElement.GetProperty("variables").GetProperty("code").GetString(),
                Is.EqualTo("EU\" injected"));
        });
    }

    /// <summary>Disposing twice returns the buffer once.</summary>
    /// <remarks>
    /// Returning one array to the pool twice hands the same memory to two callers, which is a
    /// corruption that surfaces somewhere else entirely. The second call has to do nothing.
    /// </remarks>
    [Test]
    public void Disposing_twice_is_harmless()
    {
        var body = PooledBody.Rent(16);

        body.Write("something");
        body.Dispose();

        Assert.DoesNotThrow(() => body.Dispose());
    }

    /// <summary>Writing to a returned body is a bug, and says so rather than corrupting the pool.</summary>
    [Test]
    public void Writing_after_disposal_throws()
    {
        var body = PooledBody.Rent(16);

        body.Dispose();

        Assert.Throws<ObjectDisposedException>(() => body.Write("late"));
    }

    /// <summary>A body has to be rented with room for something.</summary>
    [Test]
    public void Renting_nothing_is_rejected()
        => Assert.Throws<ArgumentOutOfRangeException>(() => PooledBody.Rent(0));

    /// <summary>The bytes a body holds, as text.</summary>
    private static string Utf8(PooledBody body) => Encoding.UTF8.GetString(body.Written.Span);

    /// <summary>What one write produces, for a body rented too small to hold it.</summary>
    private static string Written(Action<PooledBody> write)
    {
        using var body = PooledBody.Rent(1);

        write(body);

        return Utf8(body);
    }

    /// <summary>What <c>Utf8JsonWriter</c> produces for the same value, with its default options.</summary>
    private static string Reference(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            write(writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
