using System.Text.RegularExpressions;

namespace Interpreter.Models;

/// <summary>
/// Folds a locally static VHDL integer/boolean expression — the range of a
/// <c>for … generate</c> and the condition of an <c>if … generate</c> after
/// generic substitution: integer literals, <c>true</c>/<c>false</c>, names
/// resolved through a caller-supplied lookup (generics, constants), parentheses,
/// <c>** * / mod rem + -</c>, comparisons, <c>not and or xor</c>.
/// Booleans fold to 1/0.
/// </summary>
public static partial class StaticExpression
{
    [GeneratedRegex(@"\d[\d_]*|[A-Za-z_]\w*|\*\*|/=|<=|>=|[()*/+\-=<>]")]
    private static partial Regex Lexeme();

    /// <summary>
    /// The lookup most callers pass: the value text of the first constant (or
    /// generic) of that name in <paramref name="scopes"/>, in order, then of a
    /// package constant. Signals and variables are never static.
    /// </summary>
    public static Func<string, string?> ConstantsIn(params IEnumerable<Parser.Models.Variable>[] scopes) => name =>
        scopes.SelectMany(s => s).FirstOrDefault(v => v is { IsConstant: true } && v.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value?.ToString()
        ?? Services.PackageRegistry.Instance.GetConstant(name)?.Value?.ToString();

    /// <summary>
    /// Evaluates <paramref name="text"/>; names are resolved by
    /// <paramref name="lookup"/> (returning the name's value text, evaluated
    /// recursively up to depth 8).
    /// </summary>
    /// <returns>False when a name is unknown or the text is not such an expression.</returns>
    public static bool TryEvaluate(string text, Func<string, string?> lookup, out long value) =>
        TryEvaluate(text, lookup, 0, out value);

    private static bool TryEvaluate(string text, Func<string, string?> lookup, int depth, out long value)
    {
        var tokens = Lexeme().Matches(text).Select(m => m.Value).ToList();
        value = 0;
        if (tokens.Count == 0 || string.Concat(tokens).Length != Regex.Replace(text, @"\s+", "").Length) return false;
        int pos = 0;
        return new Evaluator(tokens, lookup, depth).Logical(ref pos, out value) && pos == tokens.Count;
    }

    private sealed class Evaluator(List<string> t, Func<string, string?> lookup, int depth)
    {
        private bool IsWord(int pos, string word) => pos < t.Count && t[pos].Equals(word, StringComparison.OrdinalIgnoreCase);

        public bool Logical(ref int pos, out long v)
        {
            if (!Relation(ref pos, out v)) return false;
            while (IsWord(pos, "and") || IsWord(pos, "or") || IsWord(pos, "xor"))
            {
                string op = t[pos++].ToLowerInvariant();
                if (!Relation(ref pos, out long r)) return false;
                v = op switch { "and" => (v != 0 && r != 0) ? 1 : 0, "or" => (v != 0 || r != 0) ? 1 : 0, _ => ((v != 0) ^ (r != 0)) ? 1 : 0 };
            }
            return true;
        }

        private bool Relation(ref int pos, out long v)
        {
            if (!Sum(ref pos, out v)) return false;
            if (pos < t.Count && t[pos] is "=" or "/=" or "<" or "<=" or ">" or ">=")
            {
                string op = t[pos++];
                if (!Sum(ref pos, out long r)) return false;
                v = op switch { "=" => v == r, "/=" => v != r, "<" => v < r, "<=" => v <= r, ">" => v > r, _ => v >= r } ? 1 : 0;
            }
            return true;
        }

        private bool Sum(ref int pos, out long v)
        {
            v = 0;
            bool negate = false;
            if (pos < t.Count && t[pos] is "+" or "-") negate = t[pos++] == "-";
            if (!Term(ref pos, out v)) return false;
            if (negate) v = -v;
            while (pos < t.Count && t[pos] is "+" or "-")
            {
                bool minus = t[pos++] == "-";
                if (!Term(ref pos, out long r)) return false;
                v = minus ? v - r : v + r;
            }
            return true;
        }

        private bool Term(ref int pos, out long v)
        {
            if (!Factor(ref pos, out v)) return false;
            while (pos < t.Count && (t[pos] is "*" or "/" || IsWord(pos, "mod") || IsWord(pos, "rem")))
            {
                string op = t[pos++].ToLowerInvariant();
                if (!Factor(ref pos, out long r) || (op is "/" or "mod" or "rem" && r == 0)) return false;
                v = IntegerArithmetic.Apply(op, v, r);
            }
            return true;
        }

        private bool Factor(ref int pos, out long v)
        {
            if (!Primary(ref pos, out v)) return false;
            if (pos < t.Count && t[pos] == "**")
            {
                pos++;
                if (!Primary(ref pos, out long e) || e < 0) return false;
                v = (long)Math.Pow(v, e);
            }
            return true;
        }

        private bool Primary(ref int pos, out long v)
        {
            v = 0;
            if (pos >= t.Count) return false;
            string tok = t[pos++];
            if (tok == "(")
            {
                if (!Logical(ref pos, out v) || pos >= t.Count || t[pos] != ")") return false;
                pos++;
                return true;
            }
            if (tok.Equals("not", StringComparison.OrdinalIgnoreCase))
            {
                if (!Primary(ref pos, out v)) return false;
                v = v == 0 ? 1 : 0;
                return true;
            }
            if (char.IsDigit(tok[0])) return long.TryParse(tok.Replace("_", ""), out v);
            if (tok.Equals("true", StringComparison.OrdinalIgnoreCase)) { v = 1; return true; }
            if (tok.Equals("false", StringComparison.OrdinalIgnoreCase)) return true;
            return depth < 8 && lookup(tok) is { } text && TryEvaluate(text, lookup, depth + 1, out v);
        }
    }
}
