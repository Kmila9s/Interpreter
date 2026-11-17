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
///   <item><b>Identifier mode</b>: <see cref="IndexValue.Source"/> is a Variable
///         (typically an INTEGER iterator); Evaluate reads its current value
///         each delta.</item>
///   <item><b>Expression mode</b>: <see cref="IndexValue.Expression"/> is the raw
///         token-joined text of the index expression (e.g.
///         <c>"to_integer ( unsigned ( addr ) )"</c>), parsed once and
///         re-evaluated against the live signals each delta.</item>
/// </list>
/// Kept as a distinct type from <see cref="DeferredIndex"/> so the existing
/// literal-int path (which the entire combinational corpus depends on) is
/// not touched.
/// </remarks>
public class DynamicIndex
{
    /// <summary>The vector/array signal being indexed.</summary>
    public Variable Source { get; }
    /// <summary>
    /// The index, evaluated at run time: a variable (identifier mode) or an
    /// expression over live signals (expression mode, parsed once).
    /// </summary>
    public IndexValue Index { get; }

    /// <summary>Creates an identifier-mode dynamic index over <paramref name="source"/> using <paramref name="indexSource"/>.</summary>
    /// <param name="source">The vector/array signal being indexed.</param>
    /// <param name="indexSource">The variable whose current value is the index.</param>
    public DynamicIndex(Variable source, Variable indexSource)
    {
        Source = source;
        Index = IndexValue.Of(indexSource);
    }

    /// <summary>Creates an expression-mode dynamic index over <paramref name="source"/> evaluated against <paramref name="scope"/>.</summary>
    /// <param name="source">The vector/array signal being indexed.</param>
    /// <param name="indexExpression">The raw token-joined text of the index expression.</param>
    /// <param name="scope">The variables the index expression may reference.</param>
    public DynamicIndex(Variable source, string indexExpression, HashSet<Variable> scope)
    {
        Source = source;
        Index = IndexValue.Of(indexExpression, scope);
    }

    /// <summary>Resolves the index at runtime and returns the selected element as a Variable.</summary>
    /// <returns>The indexed element, or an unresolved ('U') value when the index cannot be resolved.</returns>
    public Variable Evaluate()
    {
        // Source can be null when the parser captured a function call as the
        // indexed name; return 'U' instead of aborting the whole run.
        string vec = Source?.Value?.ToString() ?? "";
        int index = Index.Evaluate();
        var srcName = Source?.Name ?? "unknown";
        bool element = (Source?.ElementSize ?? 0) > 1;
        int width = element ? Source!.ElementSize : 1;
        var span = Source is null || vec.Length == 0 || index < 0 ? null : VectorIndexing.Element(Source, index, vec.Length);
        string value = span is { } s ? vec.Substring(s.Start, s.Width) : new string('U', width);
        return new Variable(index < 0 ? $"{srcName}_dyn" : $"{srcName}_dyn_{index}", value,
            element ? Constants.DATATYPES.STD_LOGIC_VECTOR : Constants.DATATYPES.STD_LOGIC);
    }
}
