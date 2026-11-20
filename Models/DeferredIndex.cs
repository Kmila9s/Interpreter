using Parser.Models;

namespace Interpreter.Models;

/// <summary>
/// Indexed-access placeholder evaluated at expression-execution time, not
/// at parse time. Previously, `signal(N)` on the RHS of an assignment was
/// resolved at synthesis (the bit was read from <c>signal.Value</c> at
/// parse and frozen into the `Items` array). That meant the indexed value
/// never refreshed on later signal changes, so expressions like
/// `output(0) &lt;= (not input(2)) and (not input(1)) and (not input(0))`
/// re-evaluated with stale bit values every delta cycle.
///
/// `DeferredIndex` keeps a live reference to the source <see cref="Variable"/>
/// and an integer bit index (or downto-range bounds); its <see cref="Evaluate"/>
/// reads the current <see cref="Variable.Value"/> each time it fires.
///
/// Bench Phase F bug #5 sub-cause D fix (2026-06-05).
/// </summary>
public class DeferredIndex
{
    /// <summary>The vector signal whose value is indexed at evaluation time.</summary>
    public Variable Source { get; }

    /// <summary>Single-bit index, or the high bound of a downto/to slice.</summary>
    public int High { get; }

    /// <summary>Low bound of a slice, or equal to <see cref="High"/> for a single-bit read.</summary>
    public int Low { get; }

    /// <summary>True for `(H downto L)`; otherwise single-bit when High == Low.</summary>
    public bool IsSlice { get; }

    /// <summary>Creates a deferred index/slice over <paramref name="source"/>.</summary>
    /// <param name="source">The vector signal to index at evaluation time.</param>
    /// <param name="high">The single-bit index, or the high bound of the slice.</param>
    /// <param name="low">The low bound of the slice, or equal to <paramref name="high"/> for a single bit.</param>
    /// <param name="isSlice">True when this represents a slice; false for a single-bit read.</param>
    public DeferredIndex(Variable source, int high, int low, bool isSlice)
    {
        Source = source;
        High = high;
        Low = low;
        IsSlice = isSlice;
    }

    /// <summary>
    /// Reads the current value of <see cref="Source"/> and extracts the
    /// indexed bit (or slice). Returns a fresh <see cref="Variable"/>
    /// wrapping the result so it slots into the existing expression-stack
    /// evaluator unchanged.
    /// </summary>
    public Variable Evaluate()
    {
        string vec = Source.Value?.ToString() ?? "";
        bool element = Source.ElementSize > 1;
        int width = IsSlice ? VectorIndexing.RangeWidth(Source, High, Low) : (element ? Source.ElementSize : 1);
        var span = vec.Length == 0 ? null
            : IsSlice ? VectorIndexing.Range(Source, High, Low, vec.Length)
            : VectorIndexing.Element(Source, High, vec.Length);
        string value = span is { } s ? vec.Substring(s.Start, s.Width) : new string('U', width);
        string name = VectorIndexing.PartName(Source.Name, High, IsSlice ? Low : null);
        return new Variable(name, value, Source.Type == Constants.DATATYPES.STRING
            ? (IsSlice ? Constants.DATATYPES.STRING : Constants.DATATYPES.CHARACTER)
            : IsSlice || element ? Constants.DATATYPES.STD_LOGIC_VECTOR : Constants.DATATYPES.STD_LOGIC);
    }
}
