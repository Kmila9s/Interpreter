using Parser.Models;
using static Parser.Models.Constants;

namespace Interpreter.Models;

/// <summary>
/// Represents a function call that should be evaluated at execution time, not parse time.
/// This is essential for functions like rising_edge and falling_edge that depend on
/// the current simulation state.
/// </summary>
/// <remarks>
/// <para><strong>v0.17.0:</strong></para>
/// <para>
/// Functions like rising_edge(clk) were previously evaluated during parsing, which meant
/// they always returned false because the clock hadn't changed yet. This class defers
/// evaluation until Execute() time when the signal state is correct.
/// </para>
/// </remarks>
public class DeferredFunction
{
    /// <summary>
    /// Gets the name of the function to call.
    /// </summary>
    public string FunctionName { get; }

    /// <summary>
    /// Gets the arguments to pass to the function.
    /// </summary>
    public object[] Arguments { get; }

    /// <summary>
    /// Gets the expected result data type.
    /// </summary>
    public DATATYPES ResultType { get; }

    /// <summary>
    /// Gets the variables available for argument resolution.
    /// </summary>
    public HashSet<Variable> Variables { get; }

    /// <summary>
    /// Creates a new deferred function call.
    /// </summary>
    /// <param name="functionName">The name of the function.</param>
    /// <param name="arguments">The arguments (can be Variable objects or strings).</param>
    /// <param name="resultType">The expected result type.</param>
    /// <param name="variables">Available variables for argument resolution.</param>
    public DeferredFunction(string functionName, object[] arguments, DATATYPES resultType, HashSet<Variable> variables)
    {
        FunctionName = functionName;
        Arguments = arguments;
        ResultType = resultType;
        Variables = variables;
    }

    /// <summary>
    /// Evaluates the function and returns the result as a Variable.
    /// </summary>
    /// <returns>A Variable containing the function result.</returns>
    public Variable Evaluate()
    {
        // Resolve arguments to runtime values:
        //   • Variable     → use as-is
        //   • DeferredFunction (nested IEEE call, e.g. unsigned(addr) inside
        //                       to_integer(unsigned(addr))) → recursively
        //                       evaluate to its Variable result, hand the
        //                       resulting Variable to StandardFunctions
        //   • string       → look up by name in the surrounding scope
        //   • everything else (int literals, etc.) → pass through
        var resolvedArgs = Arguments.Select(arg =>
        {
            if (arg is Variable v)
                return (object)v;
            if (arg is DeferredFunction nested)
                return (object)nested.Evaluate();
            if (arg is DeferredExpression subExpr)
                return (object)subExpr.Evaluate();
            if (arg is string s)
            {
                var found = Variables.FirstOrDefault(v =>
                    v.Name.Equals(s, StringComparison.OrdinalIgnoreCase));
                return (object)(found ?? (object)s);
            }
            return arg;
        }).ToArray();

        return Services.StandardFunctions.Result(FunctionName,
            Services.StandardFunctions.EvaluateFunction(FunctionName, resolvedArgs, Variables), resolvedArgs);
    }

    /// <summary>
    /// Checks if a function should be deferred (evaluated at execution time).
    /// Returning <c>true</c> means the parser builds a
    /// <see cref="DeferredFunction"/> instead of inline-evaluating the call,
    /// which keeps the structured call alive for the synth pass to lower
    /// through <c>IEEEPrimitives.TryLower</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// History: v0.17 deferred only edge-detection functions because their
    /// values depend on simulation state. v3.1 (#28) extends the list to
    /// every IEEE function the synthesizer has a primitive emitter for —
    /// otherwise <c>to_integer</c> / <c>resize</c> / <c>shift_*</c> /
    /// <c>rotate_*</c> still get inline-evaluated and the synth never sees
    /// a call to lower.
    /// </para>
    /// <para>
    /// Type-name overloads (<c>unsigned(stdvec)</c>, <c>signed(stdvec)</c>,
    /// <c>std_logic_vector(unsigned)</c>) are <b>not</b> deferred today.
    /// Their argument shapes are too entangled with VHDL's type-coercion
    /// rules; deferring them risks breaking the inline-evaluator's runtime
    /// semantics that the 54-fixture sweep relies on. They keep going
    /// through <c>StandardFunctions.EvaluateFunction</c> for now; #28's
    /// follow-up captures them as a separate task.
    /// </para>
    /// </remarks>
    /// <param name="functionName">The function name to check.</param>
    /// <returns>True if the function should be deferred.</returns>
    public static bool ShouldDefer(string functionName)
    {
        return functionName.ToLower() switch
        {
            "rising_edge"   => true,
            "falling_edge"  => true,
            "to_integer"    => true,
            "conv_integer"  => true,
            "to_unsigned"   => true,
            "to_signed"     => true,
            "resize"        => true,
            "shift_left"    => true,
            "shift_right"   => true,
            "rotate_left"   => true,
            "rotate_right"  => true,
            // v3.2 (#29): with the parser-side nested-call recursion in
            // place, deferring the type-name overloads is finally safe —
            // the inline evaluator now receives proper Variable args and
            // returns correct values, while the synth gets to see the
            // call as a DeferredFunction it can lower to a BUF cell with
            // the right Cast attribute.
            "unsigned"             => true,
            "signed"               => true,
            "std_logic_vector"     => true,
            "conv_std_logic_vector"=> true,
            var other => Services.StandardFunctions.LiveFunctions.Contains(other)
        };
    }
}
