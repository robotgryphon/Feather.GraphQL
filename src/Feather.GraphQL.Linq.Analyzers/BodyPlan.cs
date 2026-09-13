using System.Collections.Generic;
using System.Text;

namespace Feather.GraphQL.Linq.Analyzers;

/// <summary>
/// A request body as alternating constants and values.
/// </summary>
/// <remarks>
/// <para>
/// Almost all of a compiled query's body is known when the query is compiled: the envelope, the
/// document, the property names, the punctuation. Only the values the caller passes are not. This
/// collects the two in order so that everything constant between two values ends up in one UTF-8
/// literal, written with one copy.
/// </para>
/// <para>
/// The separators are the caller's job here, which they were not when a <c>Utf8JsonWriter</c> was
/// doing the writing — it tracked what it had written and put the commas in. Writing bytes means
/// writing the commas too, so <see cref="Separated"/> exists to make forgetting one hard.
/// </para>
/// </remarks>
internal sealed class BodyPlan
{
    private readonly StringBuilder _constant = new();
    private readonly List<Step> _steps = [];

    /// <summary>One run of the body: JSON that was known, or a value that was not.</summary>
    internal readonly record struct Step(bool IsValue, string Text);

    /// <summary>Appends JSON that is already JSON.</summary>
    public void Const(string json) => _constant.Append(json);

    /// <summary>Appends a property name and its colon, quoted.</summary>
    /// <remarks>
    /// The name comes from a schema or a parameter, so it is an identifier — but it is escaped
    /// anyway, because the cost of doing so is nothing and the cost of being wrong is a body a
    /// server cannot parse.
    /// </remarks>
    public void Property(string name)
    {
        _constant.Append('"');
        Json(_constant, name);
        _constant.Append("\":");
    }

    /// <summary>Appends a string constant, quoted and escaped.</summary>
    public void Text(string value)
    {
        _constant.Append('"');
        Json(_constant, value);
        _constant.Append('"');
    }

    /// <summary>Appends a value written at run time from a C# expression.</summary>
    public void Value(string expression)
    {
        Flush();
        _steps.Add(new Step(true, expression));
    }

    /// <summary>
    /// Writes a comma before everything except the first of a run.
    /// </summary>
    /// <remarks>
    /// Takes the count rather than tracking its own, because the callers that need it are
    /// recursive and each level separates its own members.
    /// </remarks>
    public void Separated(int written)
    {
        if (written > 0)
            _constant.Append(',');
    }

    /// <summary>The body, as runs, with the trailing constant closed off.</summary>
    public IReadOnlyList<Step> Steps
    {
        get
        {
            Flush();

            return _steps;
        }
    }

    /// <summary>How long the constant parts are, which is what a body should be rented for.</summary>
    /// <remarks>
    /// A floor rather than a guess: the values still have to fit, and the body grows if they do
    /// not. It saves the first growth in the common case, which is the one that copies.
    /// </remarks>
    public int ConstantLength
    {
        get
        {
            int total = 0;

            foreach (var step in Steps)
            {
                if (!step.IsValue)
                    total += step.Text.Length;
            }

            return total;
        }
    }

    /// <summary>Writes the plan as the statements that build the body.</summary>
    public void Emit(StringBuilder builder, string indent, string body)
    {
        foreach (var (isValue, text) in Steps)
        {
            builder.Append(indent).Append(body);

            if (isValue)
                builder.Append(".Write(").Append(text).Append(");\n");
            else
                builder.Append(".WriteRaw(").Append(Utf8(text)).Append(");\n");
        }
    }

    private void Flush()
    {
        if (_constant.Length == 0)
            return;

        _steps.Add(new Step(false, _constant.ToString()));
        _constant.Clear();
    }

    /// <summary>
    /// One UTF-8 literal holding the given text.
    /// </summary>
    /// <remarks>
    /// The text is already JSON; this escapes it a second time, for C#. A <c>u8</c> literal is
    /// encoded from the source file, so anything outside ASCII is written as the escape for its
    /// code unit rather than relied upon to survive the file's encoding.
    /// </remarks>
    public static string Utf8(string text)
    {
        var builder = new StringBuilder("\"");

        foreach (char c in text)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (c < ' ' || c > '~')
                        builder.Append("\\u").Append(((int)c).ToString("x4"));
                    else
                        builder.Append(c);

                    break;
            }
        }

        return builder.Append("\"u8").ToString();
    }

    /// <summary>
    /// Escapes text so that it can sit inside a JSON string.
    /// </summary>
    /// <remarks>
    /// The three the specification requires — the quote, the backslash, and anything below a
    /// space — and nothing else. Text above ASCII is left as it is, because JSON is UTF-8 and a
    /// server reads it as such; escaping it would only make the body longer.
    /// </remarks>
    public static void Json(StringBuilder builder, string text)
    {
        foreach (char c in text)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (c < ' ')
                        builder.Append("\\u").Append(((int)c).ToString("x4"));
                    else
                        builder.Append(c);

                    break;
            }
        }
    }

    /// <summary>The JSON-escaped form of one string.</summary>
    public static string Json(string text)
    {
        var builder = new StringBuilder();

        Json(builder, text);

        return builder.ToString();
    }
}
