using System.Globalization;
using System.Text.RegularExpressions;
using Interpreter.Services;
using Parser.Models;

namespace Interpreter.Models;

/// <summary>
/// Evaluates a VHDL time expression to femtoseconds (the resolution of VHDL time
/// and of the engine, G103): physical literals
/// (<c>10 ns</c>, <c>10ns</c>), numbers, time constants (also ones initialised by
/// an expression, <c>constant T2 : time := (3*PERIOD/4)</c>), parentheses and
/// <c>+ - * /</c>. Used by <c>wait for</c> and <c>after</c>.
/// </summary>
public static partial class TimeValue
{
    [GeneratedRegex(@"\d[\d_]*(?:\.[\d_]+)?(?:[eE][+-]?\d+)?|[A-Za-z_]\w*|[()*/+-]")]
    private static partial Regex Lexeme();

    /// <summary>Evaluates <paramref name="text"/> against <paramref name="scope"/>.</summary>
    /// <returns>False when the expression is not a time (no unit) or cannot be evaluated.</returns>
    /// <param name="text">The time expression.</param>
    /// <param name="scope">Objects visible to it (null: literals and package constants only).</param>
    /// <param name="fs">The value in femtoseconds, rounded to the nearest one (as GHDL converts a real time).</param>
    public static bool TryEvaluateFs(string text, HashSet<Variable>? scope, out long fs)
    {
        bool ok = TryEvaluate(text, scope, depth: 0, out double value, out bool isTime) & isTime;
        fs = ok ? (long)Math.Round(value, MidpointRounding.AwayFromZero) : 0;
        return ok;
    }

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
                    value *= unitFs; isTime = true; pos++;
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
                value = SignalAttributeHandler.Instance.NowFs; isTime = true; return true;
            }
            var obj = (scope is not null && scope.TryGetValue(new Variable { Name = name }, out var found) ? found : null)
                ?? PackageRegistry.Instance.GetConstant(name);
            // A time object holds femtoseconds; anything else is re-read from its value text.
            if (obj is not null && PhysicalTime.IsTime(obj) && PhysicalTime.TryFs(obj.Value, out long fs))
            {
                value = fs; isTime = true; return true;
            }
            object? raw = obj?.Value;
            return raw is not null && TryEvaluate(raw.ToString() ?? string.Empty, scope, depth + 1, out value, out isTime);
        }
    }
}
