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
    public Variable Source { get; }

    /// <summary>Single-bit index, or the high bound of a downto/to slice.</summary>
    public int High { get; }

    /// <summary>Low bound of a slice, or equal to <see cref="High"/> for a single-bit read.</summary>
    public int Low { get; }

    /// <summary>True for `(H downto L)`; otherwise single-bit when High == Low.</summary>
    public bool IsSlice { get; }

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
        if (vec.Length == 0)
        {
            return new Variable(
                $"{Source.Name}_idx",
                IsSlice ? new string('U', High - Low + 1) : "U",
                IsSlice ? Constants.DATATYPES.STD_LOGIC_VECTOR : Constants.DATATYPES.STD_LOGIC);
        }

        // Bench bug #19: arrays of vectors (Source.ElementSize > 1) index
        // by ELEMENT, not by bit. `rom_init(3)` returns element slot 3, an
        // ElementSize-wide slice. Slices `(H downto L)` over such arrays
        // aren't supported here — they're an unusual VHDL construct on an
        // array — so we honour single-index reads only and fall through to
        // the legacy bit path for explicit ranges.
        if (!IsSlice && Source.ElementSize > 1)
        {
            int elementSize = Source.ElementSize;
            int start = High * elementSize;
            if (start < 0 || start + elementSize > vec.Length)
            {
                return new Variable($"{Source.Name}_idx_{High}",
                    new string('U', elementSize),
                    Constants.DATATYPES.STD_LOGIC_VECTOR);
            }
            string slot = vec.Substring(start, elementSize);
            return new Variable($"{Source.Name}_idx_{High}", slot, Constants.DATATYPES.STD_LOGIC_VECTOR);
        }

        if (IsSlice)
        {
            int width = High - Low + 1;
            int startPos = vec.Length - 1 - High; // MSB-first storage
            string slice = (startPos >= 0 && startPos + width <= vec.Length)
                ? vec.Substring(startPos, width)
                : new string('U', width);
            return new Variable($"{Source.Name}_idx_{High}_{Low}", slice, Constants.DATATYPES.STD_LOGIC_VECTOR);
        }
        else
        {
            int pos = vec.Length - 1 - High;
            string bit = (pos >= 0 && pos < vec.Length) ? vec[pos].ToString() : "U";
            return new Variable($"{Source.Name}_idx_{High}", bit, Constants.DATATYPES.STD_LOGIC);
        }
    }
}
