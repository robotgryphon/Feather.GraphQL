namespace Feather.GraphQL.Linq.Analyzers;

/// <summary>
/// Reads the fields a written document asks for, so its reply can be modelled like a chain's.
/// </summary>
/// <remarks>
/// <para>
/// A <c>[GraphQLQuery]</c> method carries its document as a literal, which means the shape of its
/// reply is as knowable as a chain's — it is just written in GraphQL rather than in LINQ. This is
/// the half that turns one into the other: the selection set, as the same tree
/// <see cref="SelectionSetWriter"/> builds from an expression, so everything downstream of it
/// cannot tell which surface a query came from.
/// </para>
/// <para>
/// Deliberately a small parser for a small job. It reads names, nesting and the arguments it has
/// to step over, and declines everything else — an alias, a fragment, a directive, an inline
/// spread. Declining is not a failure: the method keeps the reading it already had, which is a
/// contract and the serializer, and only loses the saving.
/// </para>
/// <para>
/// Declining an alias matters more than it looks. An alias renames a field in the reply, so the
/// key on the wire is no longer the field the type knows about — a reader built as though it were
/// would look for a name the server never sent and find nothing, which is the silent-wrong-answer
/// failure this whole surface is written to avoid.
/// </para>
/// </remarks>
internal static class DocumentSelectionReader
{
    /// <summary>
    /// The root field's selection set, or null when the document is outside the subset.
    /// </summary>
    /// <remarks>
    /// The root field is the operation's single one, as every reader in this library takes it: a
    /// document with two of them has no one field to read into, and is declined here rather than
    /// read wrongly later.
    /// </remarks>
    public static SelectionSetWriter.Node? Read(string document)
    {
        int at = 0;

        // The operation header — `query`, a name, a variable list — says nothing about the reply.
        if (!Skip(document, ref at, out char opened) || opened != '{')
            return null;

        var root = new SelectionSetWriter.Node();

        // The operation's own selection set holds the root field, whose selection set is the one
        // the rows are read through.
        var fields = new SelectionSetWriter.Node();

        if (!Fields(document, ref at, fields))
            return null;

        if (fields.Order.Count != 1)
            return null;

        var selection = fields[fields.Order[0]];

        return selection.Order.Count > 0 ? selection : root.Order.Count > 0 ? root : null;
    }

    /// <summary>Walks to the operation's opening brace, over whatever preceded it.</summary>
    private static bool Skip(string document, ref int at, out char opened)
    {
        opened = default;

        for (; at < document.Length; at++)
        {
            switch (document[at])
            {
                case '{':
                    opened = '{';
                    at++;
                    return true;

                // A variable list, which the reply's shape does not depend on.
                case '(':
                    if (!Balanced(document, ref at, '(', ')'))
                        return false;

                    continue;

                case '#':
                    while (at < document.Length && document[at] != '\n')
                        at++;

                    continue;

                default:
                    continue;
            }
        }

        return false;
    }

    /// <summary>Reads the fields of one selection set, up to its closing brace.</summary>
    private static bool Fields(string document, ref int at, SelectionSetWriter.Node target)
    {
        while (at < document.Length)
        {
            char c = document[at];

            if (char.IsWhiteSpace(c) || c == ',')
            {
                at++;
                continue;
            }

            if (c == '}')
            {
                at++;
                return true;
            }

            // A fragment spread or an inline fragment: what the reply carries then depends on a
            // type condition, which is the server's to resolve and not knowable here.
            if (c == '.' || c == '#' || c == '@')
                return false;

            if (!char.IsLetter(c) && c != '_')
                return false;

            int start = at;

            while (at < document.Length && (char.IsLetterOrDigit(document[at]) || document[at] == '_'))
                at++;

            string name = document.Substring(start, at - start);
            var child = target.Child(name);

            if (!Rest(document, ref at, child))
                return false;
        }

        return false;
    }

    /// <summary>
    /// Reads what follows a field's name: its arguments, its own selection set, or neither.
    /// </summary>
    private static bool Rest(string document, ref int at, SelectionSetWriter.Node child)
    {
        while (at < document.Length)
        {
            char c = document[at];

            if (char.IsWhiteSpace(c))
            {
                at++;
                continue;
            }

            switch (c)
            {
                // An alias renames the field in the reply, so the name just read was not the key.
                case ':':
                    return false;

                // Arguments change what the server answers with, never what the answer is shaped
                // like, so they are stepped over whole.
                case '(':
                    if (!Balanced(document, ref at, '(', ')'))
                        return false;

                    continue;

                case '@':
                    return false;

                case '{':
                    at++;

                    return Fields(document, ref at, child);

                default:
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Steps over a bracketed run, counting depth so a nested one does not end it early.
    /// </summary>
    /// <remarks>
    /// A string inside it is stepped over as a unit: a filter value may contain a brace or a
    /// bracket of its own, and counting those would leave the reader somewhere the document never
    /// meant it to be.
    /// </remarks>
    private static bool Balanced(string document, ref int at, char open, char close)
    {
        int depth = 0;

        for (; at < document.Length; at++)
        {
            char c = document[at];

            if (c == '"')
            {
                if (!String(document, ref at))
                    return false;

                continue;
            }

            if (c == open)
                depth++;
            else if (c == close && --depth == 0)
            {
                at++;
                return true;
            }
        }

        return false;
    }

    /// <summary>Steps over a quoted string, escapes included.</summary>
    private static bool String(string document, ref int at)
    {
        for (at++; at < document.Length; at++)
        {
            if (document[at] == '\\')
            {
                at++;
                continue;
            }

            if (document[at] == '"')
                return true;
        }

        return false;
    }
}
