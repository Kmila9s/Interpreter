using Interpreter.Services;
using Parser.Models;
using static Parser.Models.Constants;

namespace Interpreter.Models;

/// <summary>
/// 2026-07-20 — User-defined function call whose evaluation MUST happen at
/// runtime, on every re-fire of the enclosing <see cref="Operation"/>,
/// rather than being baked into a frozen <see cref="Variable"/> at parse
/// time.
/// </summary>
/// <remarks>
/// <para>
/// Sibling of <see cref="DeferredFunction"/>. The latter defers IEEE
/// stdlib primitives (<c>rising_edge</c>, <c>to_unsigned</c>, …); this
/// one defers <b>user-declared</b> functions registered in
/// <see cref="PackageRegistry"/> with a non-empty <c>BodyRaw</c>.
/// </para>
/// <para>
/// Before this class existed, <c>ParseUserDefinedFunctionCall</c> in
/// <c>Operation.cs</c> would evaluate each argument expression to its
/// parse-time value and invoke <c>FunctionExecutor.ExecuteFunction</c>
/// eagerly, baking the returned value into a <see cref="Variable"/>
/// pushed onto the operation's <c>Items</c> stack. On subsequent
/// re-fires of that <c>Operation</c> (typical inside a clocked process),
/// the frozen <see cref="Variable"/> would just re-push its stale value.
/// Symptom: an LFSR routed through a user <c>function</c> would
/// transition once (init → first step) then freeze forever; the same
/// LFSR written inline cycled continuously because the inline path used
/// live-signal <see cref="Variable"/> reads.
/// </para>
/// <para>
/// Fix: at parse time, wrap the call as a <see cref="DeferredUserFunction"/>
/// carrying the argument <b>expressions</b> (not their values) plus a
/// reference to the caller's live <c>Variables</c> scope. On each
/// <see cref="Evaluate"/> call the wrapper re-parses+re-executes each
/// argument expression against the live scope, then invokes
/// <see cref="FunctionExecutor.ExecuteFunction"/> with the freshly
/// resolved values. The result is a brand-new <see cref="Variable"/>
/// per fire — no stale caching.
/// </para>
/// </remarks>
public class DeferredUserFunction
{
    /// <summary>Registered VHDL function name (case-normalised at
    /// registration; consumers match by ordinal case-insensitive).</summary>
    public string FunctionName { get; }

    /// <summary>Raw argument expression strings, in caller order.
    /// Re-parsed into a fresh <see cref="Operation"/> on every
    /// <see cref="Evaluate"/> so signal reads bind to current values.</summary>
    public IReadOnlyList<string> ArgExpressions { get; }

    /// <summary>Return type declared by the function signature. Copied
    /// into the produced <see cref="Variable"/> so downstream operators
    /// see the correct <c>DATATYPES</c>.</summary>
    public DATATYPES ReturnType { get; }

    /// <summary>Declared return-type size (bit-width) for vector return
    /// types. Zero when the function returns a scalar or the size is
    /// unspecified.</summary>
    public int ReturnTypeSize { get; }

    /// <summary>Live scope the argument expressions resolve against.
    /// This is the enclosing <see cref="Operation.Variables"/> hashset,
    /// whose <c>Variable.Value</c> fields are mutated in-place by the
    /// <c>DeltaCycleEngine</c> between re-fires.</summary>
    public HashSet<Variable> Variables { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="DeferredUserFunction"/> class.
    /// </summary>
    /// <param name="functionName">The name of the user-declared VHDL function.</param>
    /// <param name="argExpressions">The raw argument expressions, re-parsed against the live scope on each evaluation.</param>
    /// <param name="returnType">The function's declared return data type.</param>
    /// <param name="returnTypeSize">The return-type bit width for vector returns; zero for scalars.</param>
    /// <param name="variables">The live scope the argument expressions resolve against.</param>
    public DeferredUserFunction(
        string functionName,
        IReadOnlyList<string> argExpressions,
        DATATYPES returnType,
        int returnTypeSize,
        HashSet<Variable> variables)
    {
        FunctionName = functionName;
        ArgExpressions = argExpressions;
        ReturnType = returnType;
        ReturnTypeSize = returnTypeSize;
        Variables = variables;
    }

    /// <summary>
    /// Re-parse each argument expression, execute it against the live
    /// scope, hand the resolved values to
    /// <see cref="FunctionExecutor.ExecuteFunction"/>, and return the
    /// fresh result as a <see cref="Variable"/>. Runs on every
    /// <c>Operation.Execute</c> — the whole point of the deferral.
    /// </summary>
    public Variable Evaluate()
    {
        var argValues = new object[ArgExpressions.Count];
        for (int i = 0; i < ArgExpressions.Count; i++)
        {
            var argOp = new Operation(ArgExpressions[i], Variables);
            var argResult = argOp.Execute();
            argValues[i] = argResult is Variable av
                ? (av.Value ?? "0")
                : (argResult ?? "0");
        }

        var executor = new FunctionExecutor();
        var rv = executor.ExecuteFunction(FunctionName, argValues, Variables);

        var resultType = ReturnType == DATATYPES.UNKNOWN
            ? DATATYPES.STD_LOGIC_VECTOR
            : ReturnType;
        var resultVar = new Variable(
            $"UserFunction-{FunctionName}",
            rv ?? "0",
            resultType);
        if (ReturnTypeSize > 0) resultVar.Size = ReturnTypeSize;
        resultVar.PortType = PortType.OUT;
        resultVar.TypeMark = (Services.FunctionRegistry.Instance.Get(FunctionName)
            ?? Services.PackageRegistry.Instance.GetFunction(FunctionName))?.ReturnTypeMark;
        return resultVar;
    }
}
