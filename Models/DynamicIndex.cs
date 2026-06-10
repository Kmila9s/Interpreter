using Parser.Models;

namespace Interpreter.Models;

/// <summary>
/// Runtime-variable-indexed access (Bug #14). Used when a vector signal is
/// indexed by a name that resolves to an iterator/counter rather than a
/// literal integer, e.g. <c>diff(i)</c> inside <c>for i in 0 to 15 loop</c>.
/// At synthesis time we capture references to both the source vector and
/// the index variable; <see cref="Evaluate"/> reads the index variable's
/// current value at expression-evaluation time, parses it to an int, and
/// returns the indexed bit (or 'U' on out-of-range).
/// </summary>
/// <remarks>
/// Kept as a distinct type from <see cref="DeferredIndex"/> so the existing
/// literal-int path (which the entire combinational corpus depends on) is
/// not touched. Three prior attempts to extend DeferredIndex itself
/// regressed unrelated cases via a path that hasn't been narrowed down.
/// </remarks>
public class DynamicIndex
{
    public Variable Source { get; }
    public Variable IndexSource { get; }

    public DynamicIndex(Variable source, Variable indexSource)
    {
        Source = source;
        IndexSource = indexSource;
    }

    public Variable Evaluate()
    {
        string vec = Source.Value?.ToString() ?? "";
        int dynIdx;
        object? rawIdx = IndexSource.Value;
        if (rawIdx is int ri) dynIdx = ri;
        else if (rawIdx is long li) dynIdx = (int)li;
        else if (!int.TryParse(rawIdx?.ToString(), out dynIdx)) dynIdx = -1;

        if (vec.Length == 0 || dynIdx < 0)
            return new Variable($"{Source.Name}_dyn", "U", Constants.DATATYPES.STD_LOGIC);

        int pos = vec.Length - 1 - dynIdx;
        string bit = (pos >= 0 && pos < vec.Length) ? vec[pos].ToString() : "U";
        return new Variable($"{Source.Name}_dyn_{dynIdx}", bit, Constants.DATATYPES.STD_LOGIC);
    }
}
