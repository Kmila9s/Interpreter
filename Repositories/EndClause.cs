using Parser.Models;
using Parser.Repositories;
using static Parser.Models.Constants.TYPE;

namespace Interpreter.Repositories;

/// <summary>
/// The single reader of a closing clause <c>end [postponed] &lt;keyword&gt; [?] [label] ;</c>
/// (VHDL LRM §10/§11: <c>end if</c>, <c>end case</c>, <c>end loop</c>, <c>end process</c>,
/// <c>end generate</c>, <c>end block</c>). Strict like GHDL: the keyword and the <c>;</c>
/// are required, and a closing label must repeat the statement's own label (an
/// unlabeled statement cannot have one).
/// </summary>
public static class EndClause
{
    /// <summary>
    /// Consumes <c>end &lt;keyword&gt; [?] [label] ;</c> at the current position.
    /// </summary>
    /// <param name="stream">Stream positioned on <c>end</c>.</param>
    /// <param name="keyword">Construct keyword expected after <c>end</c> (<c>if</c>, <c>loop</c>, …).</param>
    /// <param name="label">The statement's label, or null when it has none.</param>
    /// <param name="allowPostponed">Accept <c>end postponed process</c>.</param>
    /// <exception cref="ParserException">The clause is malformed or the label does not match.</exception>
    public static void Consume(TokenStream stream, string keyword, string? label = null, bool allowPostponed = false)
    {
        if (!Is(stream.Current, "end"))
            throw new ParserException($"'end {keyword};' expected", stream.Current ?? stream.Previous);
        stream.MoveNext();
        if (allowPostponed && Is(stream.Current, "postponed")) stream.MoveNext();
        if (!Is(stream.Current, keyword))
            throw new ParserException($"'{keyword}' expected after 'end'", stream.Current ?? stream.Previous);
        stream.MoveNext();
        // VHDL-2008 matching case closes with `end case?;`.
        if (stream.Current?.Value == "?") stream.MoveNext();
        if (stream.Current is { Type: IDENTIFIERS } closing)
        {
            if (label is null)
                throw new ParserException($"'end {keyword}' of an unlabeled statement cannot have a label", closing);
            if (!closing.Value.Equals(label, StringComparison.OrdinalIgnoreCase))
                throw new ParserException($"misspelling, \"{label}\" expected", closing);
            stream.MoveNext();
        }
        if (stream.Current?.Value != ";")
            throw new ParserException($"';' expected after 'end {keyword}'", stream.Current ?? stream.Previous);
        stream.MoveNext();
    }

    /// <summary>True when <paramref name="token"/> is the (case-insensitive) word <paramref name="word"/>.</summary>
    internal static bool Is(Token? token, string word) =>
        token is not null && token.Value.Equals(word, StringComparison.OrdinalIgnoreCase);
}
