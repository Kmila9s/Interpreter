using System.Globalization;
using System.Text.RegularExpressions;
using Interpreter.Services;
using Parser.Models;

namespace Interpreter.Models;

/// <summary>
/// Evaluates a VHDL time expression to nanoseconds: physical literals
/// (<c>10 ns</c>, <c>10ns</c>), numbers, time constants (also ones initialised by
/// an expression, <c>constant T2 : time := (3*PERIOD/4)</c>), parentheses and
/// <c>+ - * /</c>. Used by <c>wait for</c>.
/// </summary>
public static partial class TimeValue
{
    /// <summary>Nanoseconds per femtosecond (time values are femtoseconds, <see cref="PhysicalTime"/>).</summary>
    private const double NsPerFs = 1e-6;

    [GeneratedRegex(@"\d[\d_]*(?:\.[\d_]+)?(?:[eE][+-]?\d+)?|[A-Za-z_]\w*|[()*/+-]")]
    private static partial Regex Lexeme();

    /// <summary>Evaluates <paramref name="text"/> against <paramref name="scope"/>.</summary>
    /// <returns>False when the expression is not a time (no unit) or cannot be evaluated.</returns>
    public static bool TryEvaluateNs(string text, HashSet<Variable>? scope, out double ns) =>
        TryEvaluate(text, scope, depth: 0, out ns, out bool isTime) & isTime;

    private static bool TryEvaluate(string text, HashSet<Variable>? scope, int depth, out double value, out bool isTime)
    {
        var tokens = Lexeme().Matches(text).Select(m => m.Value).ToList();
        int pos = 0;
        var parser = new Evaluator(tokens, scope, depth);
        bool ok = parser.Sum(ref pos, out value, out isTime) && pos == tokens.Count;
        return ok;
    }

    /// <summary>Recursive-descent evaluator over the lexemes; <c>isTime</c> tracks whether the value carries a unit.</summary>
    private sealed class Evaluator(List<string> tokens, HashSet<Variable>? scope, int depth)
    {
        public bool Sum(ref int pos, out double value, out bool isTime)
        {
            if (!Product(ref pos, out value, out isTime)) return false;
            while (pos < tokens.Count && tokens[pos] is "+" or "-")
            {
                bool minus = tokens[pos++] == "-";
                if (!Product(ref pos, out double rhs, out bool rhsTime)) return false;
                value = minus ? value - rhs : value + rhs;
                isTime |= rhsTime;
            }
            return true;
        }

        private bool Product(ref int pos, out double value, out bool isTime)
        {
            if (!Factor(ref pos, out value, out isTime)) return false;
            while (pos < tokens.Count && tokens[pos] is "*" or "/")
            {
                bool divide = tokens[pos++] == "/";
                if (!Factor(ref pos, out double rhs, out bool rhsTime) || (divide && rhs == 0)) return false;
                value = divide ? value / rhs : value * rhs;
                isTime = divide ? isTime && !rhsTime : isTime || rhsTime;
            }
            return true;
        }

        private bool Factor(ref int pos, out double value, out bool isTime)
        {
            value = 0; isTime = false;
            if (pos >= tokens.Count) return false;
            string t = tokens[pos++];
            if (t == "(")
            {
                if (!Sum(ref pos, out value, out isTime) || pos >= tokens.Count || tokens[pos] != ")") return false;
                pos++;
                return true;
            }
            if (t == "-") { bool ok = Factor(ref pos, out value, out isTime); value = -value; return ok; }
            if (char.IsDigit(t[0]))
            {
                value = double.Parse(t.Replace("_", ""), CultureInfo.InvariantCulture);
                if (pos < tokens.Count && PhysicalTime.UnitFs.TryGetValue(tokens[pos], out long unitFs))
                {
                    value *= unitFs * NsPerFs; isTime = true; pos++;
                }
                return true;
            }
            return Constant(t, out value, out isTime);
        }

        /// <summary>A constant visible here (object in scope or package constant), evaluated from its value text.</summary>
        private bool Constant(string name, out double value, out bool isTime)
        {
            value = 0; isTime = false;
            if (depth > 8) return false;
            if (name.Equals("now", StringComparison.OrdinalIgnoreCase))
            {
                value = SignalAttributeHandler.Instance.NowFs * NsPerFs; isTime = true; return true;
            }
            var obj = scope?.FirstOrDefault(v => v is not null && v.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? PackageRegistry.Instance.GetConstant(name);
            // A time object holds femtoseconds; anything else is re-read from its value text.
            if (obj is not null && PhysicalTime.IsTime(obj) && PhysicalTime.TryFs(obj.Value, out long fs))
            {
                value = fs * NsPerFs; isTime = true; return true;
            }
            object? raw = obj?.Value;
            return raw is not null && TryEvaluate(raw.ToString() ?? string.Empty, scope, depth + 1, out value, out isTime);
        }
    }
}
