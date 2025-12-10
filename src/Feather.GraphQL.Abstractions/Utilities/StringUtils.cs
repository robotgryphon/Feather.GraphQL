using System.Text.RegularExpressions;

namespace Feather.GraphQL.Utilities;

/// <summary>
/// Copied from https://github.com/jquense/StringUtils
/// </summary>
public static class StringUtils
{
    private static readonly Regex _reWords = new Regex(@"[A-Z\xc0-\xd6\xd8-\xde]?[a-z\xdf-\xf6\xf8-\xff]+(?:['’](?:d|ll|m|re|s|t|ve))?(?=[\xac\xb1\xd7\xf7\x00-\x2f\x3a-\x40\x5b-\x60\x7b-\xbf\u2000-\u206f \t\x0b\f\xa0\ufeff\n\r\u2028\u2029\u1680\u180e\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200a\u202f\u205f\u3000]|[A-Z\xc0-\xd6\xd8-\xde]|$)|(?:[A-Z\xc0-\xd6\xd8-\xde]|[^\ud800-\udfff\xac\xb1\xd7\xf7\x00-\x2f\x3a-\x40\x5b-\x60\x7b-\xbf\u2000-\u206f \t\x0b\f\xa0\ufeff\n\r\u2028\u2029\u1680\u180e\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200a\u202f\u205f\u3000\d+\u2700-\u27bfa-z\xdf-\xf6\xf8-\xffA-Z\xc0-\xd6\xd8-\xde])+(?:['’](?:D|LL|M|RE|S|T|VE))?(?=[\xac\xb1\xd7\xf7\x00-\x2f\x3a-\x40\x5b-\x60\x7b-\xbf\u2000-\u206f \t\x0b\f\xa0\ufeff\n\r\u2028\u2029\u1680\u180e\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200a\u202f\u205f\u3000]|[A-Z\xc0-\xd6\xd8-\xde](?:[a-z\xdf-\xf6\xf8-\xff]|[^\ud800-\udfff\xac\xb1\xd7\xf7\x00-\x2f\x3a-\x40\x5b-\x60\x7b-\xbf\u2000-\u206f \t\x0b\f\xa0\ufeff\n\r\u2028\u2029\u1680\u180e\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200a\u202f\u205f\u3000\d+\u2700-\u27bfa-z\xdf-\xf6\xf8-\xffA-Z\xc0-\xd6\xd8-\xde])|$)|[A-Z\xc0-\xd6\xd8-\xde]?(?:[a-z\xdf-\xf6\xf8-\xff]|[^\ud800-\udfff\xac\xb1\xd7\xf7\x00-\x2f\x3a-\x40\x5b-\x60\x7b-\xbf\u2000-\u206f \t\x0b\f\xa0\ufeff\n\r\u2028\u2029\u1680\u180e\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200a\u202f\u205f\u3000\d+\u2700-\u27bfa-z\xdf-\xf6\xf8-\xffA-Z\xc0-\xd6\xd8-\xde])+(?:['’](?:d|ll|m|re|s|t|ve))?|[A-Z\xc0-\xd6\xd8-\xde]+(?:['’](?:D|LL|M|RE|S|T|VE))?|\d+|(?:[\u2700-\u27bf]|(?:\ud83c[\udde6-\uddff]){2}|[\ud800-\udbff][\udc00-\udfff])[\ufe0e\ufe0f]?(?:[\u0300-\u036f\ufe20-\ufe23\u20d0-\u20f0]|\ud83c[\udffb-\udfff])?(?:\u200d(?:[^\ud800-\udfff]|(?:\ud83c[\udde6-\uddff]){2}|[\ud800-\udbff][\udc00-\udfff])[\ufe0e\ufe0f]?(?:[\u0300-\u036f\ufe20-\ufe23\u20d0-\u20f0]|\ud83c[\udffb-\udfff])?)*");

    /// <summary>
    /// Split a cased string into a series of "words" excluding the seperator.
    /// </summary>
    /// <param name="str"></param>
    /// <returns></returns>
    private static IEnumerable<string> ToWords(string str)
    {
        foreach (Match match in _reWords.Matches(str))
        {
            yield return match.Value;
        }
    }

    /// <summary>
    /// Convert a string to CONSTANT_CASE
    /// </summary>
    /// <example>
    ///     <code>StringUtils.ToConstantCase("fOo BaR") // "FOO_BAR"</code>
    ///     <code>StringUtils.ToConstantCase("FooBar")  // "FOO_BAR"</code>
    ///     <code>StringUtils.ToConstantCase("Foo Bar") // "FOO_BAR"</code>
    /// </example>
    /// <param name="str"></param>
    /// <returns></returns>
    public static string ToConstantCase(string str)
        => ChangeCase(str, "_", w => w.ToUpperInvariant());

    private static string ChangeCase(string str, string sep, Func<string, string> composer)
        => ChangeCase(str, sep, (w, i) => composer(w));

    /// <summary>
    /// Convert a string to a new case
    /// </summary>
    /// <example>
    /// Convert a string to inverse camelCase: CAMELcASE
    ///     <code>
    ///         StringUtils.ChangeCase("my string", "", (word, index) => {
    ///             word = word.ToUpperInvariant();
    ///             if (index > 0)
    ///                 word = StringUtils.toLowerFirst(word);
    ///             return word
    ///         });
    ///         // "MYsTRING"
    ///     </code>
    /// </example>
    /// <param name="str">an input string </param>
    /// <param name="sep">a seperator string used between "words" in the string</param>
    /// <param name="composer">a function that converts individual words to a new case</param>
    /// <returns></returns>
    private static string ChangeCase(string str, string sep, Func<string, int, string> composer)
    {
        int index = 0;
        return ToWords(str)
                .Aggregate("", (current, word) => current + ((index == 0 ? "" : sep) + composer(word, index++)));
    }
}
