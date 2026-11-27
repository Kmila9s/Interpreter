using System.Text.RegularExpressions;
using Parser.Models;

namespace Interpreter.Services;

/// <summary>
/// The implicit sensitivity of a concurrent statement, a concurrent assertion or
/// procedure call, <c>wait until</c> without <c>on</c>, and <c>process (all)</c>
/// (LRM §10.2, §11): every signal <em>named in the statement's text</em> —
/// waveform values and delays, conditions, selectors, index expressions and
/// function arguments. The body of a called subprogram is never looked into,
/// so a function reading an outer signal does not make the statement
/// sensitive to it (GHDL, G62).
/// </summary>
public static partial class ImplicitSensitivity
{
    /// <summary>
    /// The implicit sensitivity of a concurrent statement (LRM §11.6): the signals
    /// named in its text, never those read inside a function it calls (G62). Null
    /// when its value can change without an event on them (implicit signals
    /// <c>'stable</c>/<c>'quiet</c>/<c>'delayed</c>/<c>'transaction</c>, <c>now</c>):
    /// it then runs every delta cycle.
    /// </summary>
    public static HashSet<Variable>? OfStatement(Interfaces.IConcurrentStatement statement, HashSet<Variable> scope)
    {
        var texts = statement.ReadTexts.ToList();
        if (texts.Any(t => TimeDependent().IsMatch(t))) return null;
        return [.. SignalsIn(texts, scope)];
    }

    [GeneratedRegex(@"'\s*(stable|quiet|delayed|transaction)\b|\bnow\b", RegexOptions.IgnoreCase)]
    private static partial Regex TimeDependent();

    /// <summary>
    /// Every signal of <paramref name="scope"/> named in <paramref name="texts"/>.
    /// String and character literals and field selectors are not names;
    /// constants are not signals.
    /// </summary>
    public static IEnumerable<Variable> SignalsIn(IEnumerable<string> texts, HashSet<Variable> scope)
    {
        foreach (var text in texts)
        {
            string code = CharLiteral().Replace(VhdlText.MaskStringLiterals(text), " ");
            foreach (Match m in Name().Matches(code))
            {
                if (m.Index > 0 && code[m.Index - 1] == '.') continue;   // a record field, not a name
                if (scope.TryGetValue(new Variable { Name = m.Value }, out var signal) && !signal.IsConstant)
                    yield return signal;
            }
        }
    }

    /// <summary>
    /// The text a statement part reads: <paramref name="tokens"/> without the
    /// target names of its signal and variable assignments (an object only
    /// written by a <c>process (all)</c> is not in its sensitivity list).
    /// </summary>
    public static string ReadText(IReadOnlyList<Token> tokens)
    {
        var parts = new List<string>(tokens.Count);
        for (int i = 0; i < tokens.Count; i++)
        {
            if (IsTargetName(tokens, i)) continue;
            parts.Add(tokens[i].Value);
        }
        return string.Join(" ", parts);
    }

    /// <summary>True when token <paramref name="i"/> starts an assignment target (<c>name [(…)] &lt;=|:=</c> at a statement start).</summary>
    private static bool IsTargetName(IReadOnlyList<Token> tokens, int i)
    {
        if (tokens[i].Type != Constants.TYPE.IDENTIFIERS) return false;
        string before = i > 0 ? tokens[i - 1].Value.ToLowerInvariant() : ";";
        if (before is not (";" or "then" or "else" or "begin" or "loop" or "=>" or "is" or ":" or "generate")) return false;
        int j = i + 1;
        if (j < tokens.Count && tokens[j].Value == "(")
        {
            int depth = 0;
            for (; j < tokens.Count; j++)
            {
                if (tokens[j].Value == "(") depth++;
                else if (tokens[j].Value == ")" && --depth == 0) { j++; break; }
            }
        }
        return j < tokens.Count && tokens[j].Value is "<=" or ":=";
    }

    [GeneratedRegex(@"[A-Za-z]\w*")]
    private static partial Regex Name();

    /// <summary>A character literal (<c>'Z'</c>), not an attribute tick (<c>clk'event</c>).</summary>
    [GeneratedRegex(@"(?<![\w)])'.'")]
    private static partial Regex CharLiteral();
}
