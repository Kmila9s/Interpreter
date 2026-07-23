using Parser.Models;

namespace Interpreter.Models;

/// <summary>
/// Runtime-variable-indexed access. Used when a vector / array signal is
/// indexed by a name or expression that resolves to a value at execution
/// time rather than at parse time — for example <c>diff(i)</c> inside a
/// for-loop (Bug #14) or <c>rom_init(to_integer(unsigned(addr)))</c>
/// against a constant array of vectors (Bench bug #19).
/// </summary>
/// <remarks>
/// Two modes:
/// <list type="bullet">
///   <item><b>Identifier mode</b>: <see cref="IndexSource"/> is a Variable
///         (typically an INTEGER iterator); Evaluate reads its current value
///         each delta.</item>
///   <item><b>Expression mode</b>: <see cref="IndexExpression"/> is the raw
///         token-joined text of the index expression (e.g.
///         <c>"to_integer ( unsigned ( addr ) )"</c>); Evaluate builds a
///         fresh <see cref="Services.Operation"/> against
///         <see cref="ExpressionScope"/> each delta and reads the result.
///         Slower (re-parses each evaluation) but needed because the
///         expression may reference live signals.</item>
/// </list>
/// Kept as a distinct type from <see cref="DeferredIndex"/> so the existing
/// literal-int path (which the entire combinational corpus depends on) is
/// not touched.
/// </remarks>
public class DynamicIndex
{
    public Variable Source { get; }
    public Variable? IndexSource { get; }

    /// <summary>
    /// Bench bug #19: raw text of the index expression for re-evaluation at
    /// runtime. Set when the index is a non-trivial expression (function
    /// call, arithmetic on signals) that the parser couldn't reduce to a
    /// single integer Variable. Null when the legacy IndexSource path is in
    /// use.
    /// </summary>
    public string? IndexExpression { get; }

    /// <summary>
    /// Scope of variables the index expression can reference. Captured at
    /// parse time and passed to each freshly-constructed Operation when
    /// <see cref="IndexExpression"/> is non-null. Null when the legacy
    /// IndexSource path is in use.
    /// </summary>
    public HashSet<Variable>? ExpressionScope { get; }

    public DynamicIndex(Variable source, Variable indexSource)
    {
        Source = source;
        IndexSource = indexSource;
        IndexExpression = null;
        ExpressionScope = null;
    }

    public DynamicIndex(Variable source, string indexExpression, HashSet<Variable> scope)
    {
        Source = source;
        IndexSource = null;
        IndexExpression = indexExpression;
        ExpressionScope = scope;
    }

    // Bench bug #19 perf follow-up (2026-06-23, P3 hang): rebuilding a
    // fresh Operation on every Evaluate() killed multi-thousand-cycle sims
    // (P3 went from 50 µs/min on P2A to 1.5 s per 100 ns). Cache the parsed
    // Operation and let it re-execute against the live scope each call —
    // Operation.Execute already re-reads its operands' .Value at runtime,
    // so the cached parse is safe.
    private Services.Operation? _cachedOperation;

    public Variable Evaluate()
    {
        // Defensive null-guards: an expression like `mem(to_integer(unsigned(addr)))`
        // can land here with Source = null when the parser captured a
        // function-call expression as an index source rather than a real
        // signal. Without this guard the entire SimulationRunner aborts via
        // a swallowed NullReferenceException, leaving the user with a silent
        // empty VCD. Return 'U' instead so the design simulates and the user
        // sees the unsupported-index location in the waveform.
        string vec = Source?.Value?.ToString() ?? "";
        int dynIdx = -1;

        if (IndexExpression is not null && ExpressionScope is not null)
        {
            // Expression mode: parse the index expression ONCE and cache;
            // Execute re-reads operand .Value each call, so re-parsing per
            // delta was pure overhead. The cache is per-DynamicIndex
            // instance so different `mem(<expr1>)` and `mem(<expr2>)`
            // sites don't share.
            try
            {
                if (_cachedOperation is null)
                {
                    _cachedOperation = new Services.Operation(IndexExpression, ExpressionScope);
                }
                var result = _cachedOperation.Execute();
                if (result is Variable v) dynIdx = TryToInt(v.Value);
                else dynIdx = TryToInt(result);
            }
            catch
            {
                dynIdx = -1;
            }
        }
        else
        {
            object? rawIdx = IndexSource?.Value;
            if (rawIdx is int ri) dynIdx = ri;
            else if (rawIdx is long li) dynIdx = (int)li;
            else if (!int.TryParse(rawIdx?.ToString(), out dynIdx)) dynIdx = -1;
        }

        var srcName = Source?.Name ?? "unknown";
        if (vec.Length == 0 || dynIdx < 0)
            return new Variable($"{srcName}_dyn", "U", Constants.DATATYPES.STD_LOGIC);

        // Bench bug #19: when the source is typed by a user array of vectors
        // (ElementSize > 1), the index selects an element slot — not a bit.
        // Storage convention matches ParseAggregateOrExpression's positional
        // path: element 0 is at the start of the bitstring, each slot is
        // ElementSize bits wide.
        int elementSize = Source?.ElementSize ?? 0;
        if (elementSize > 1)
        {
            int start = dynIdx * elementSize;
            if (start < 0 || start + elementSize > vec.Length)
                return new Variable($"{srcName}_dyn_{dynIdx}",
                    new string('U', elementSize),
                    Constants.DATATYPES.STD_LOGIC_VECTOR);
            string slot = vec.Substring(start, elementSize);
            return new Variable($"{srcName}_dyn_{dynIdx}", slot, Constants.DATATYPES.STD_LOGIC_VECTOR);
        }

        int pos = vec.Length - 1 - dynIdx;
        string bit = (pos >= 0 && pos < vec.Length) ? vec[pos].ToString() : "U";
        return new Variable($"{srcName}_dyn_{dynIdx}", bit, Constants.DATATYPES.STD_LOGIC);
    }

    private static int TryToInt(object? value)
    {
        if (value == null) return -1;
        if (value is int i) return i;
        if (value is long l) return (int)l;
        string s = value.ToString() ?? string.Empty;
        // Binary string (multi-char, only 0/1) — interpret base-2.
        if (s.Length > 1 && s.All(c => c == '0' || c == '1'))
        {
            try { return System.Convert.ToInt32(s, 2); } catch { /* fall through */ }
        }
        if (int.TryParse(s, out int parsed)) return parsed;
        return -1;
    }
}
