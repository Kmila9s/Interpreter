using Parser.Models;
using Interpreter.Repositories;

namespace Interpreter.Models;

/// <summary>
/// Phase 3 (Session 26, 2026-07-22): reified execution state for a
/// procedure body running inside a wait-driven process. Pushed on
/// <c>Process.Frames</c> when the engine's process-aware runner enters
/// a procedure call; popped when the procedure returns. A <c>wait</c>
/// anywhere inside the procedure body (top level, an if/case branch,
/// a for/while/simple loop, or a nested procedure call) captures the
/// resume position here and keeps the frame on the stack until the
/// wake fires — complementing <see cref="LoopFrame"/> (loops) and
/// <see cref="BranchFrame"/> (if/case).
/// </summary>
/// <remarks>
/// <para>Formal→actual binding uses the rebuild-per-call strategy
/// (Option C in the plan): <see cref="ProcedureCallStatement.ResolveBoundBody"/>
/// textually renames formal identifiers to actual identifiers in the
/// procedure's <c>BodyRaw</c> and re-parses through
/// <see cref="Interpreter.Services.FunctionExecutor.ParseFunctionBodyStatic"/>. The resulting
/// <see cref="Tasks"/> hold <c>Operation</c>s whose RPN items reference
/// caller-side <c>Variable</c>s directly, so signal writes route through
/// <c>_signalScheduler</c> without any per-execute remap, and
/// <c>wait until &lt;cond&gt;</c> sensitivity sets capture the caller's
/// actual signals for correct condition-blocked wakeup.</para>
/// <para>Reentrancy is trivially safe: each call gets its own
/// <see cref="Tasks"/>, <see cref="BoundScope"/>, and <see cref="OutMap"/>
/// via <c>ResolveBoundBody</c>. Two concurrent processes calling the
/// same procedure never share state.</para>
/// </remarks>
public sealed class ProcedureFrame : IProcessFrame
{
    /// <summary>
    /// The per-call bound body — freshly parsed against
    /// <see cref="BoundScope"/>, with formals replaced by actual names.
    /// Never null; empty if the procedure has no synthesizable body.
    /// </summary>
    public required List<object> Tasks { get; init; }

    /// <summary>
    /// Resume index into <see cref="Tasks"/>. Set to (idx + 1) when a
    /// wait or nested-structure suspend fires at task index idx, so the
    /// resume re-enters at the statement AFTER the yield point.
    /// </summary>
    public int InnerPc { get; set; }

    /// <summary>
    /// The originating call statement — kept so the completion path can
    /// find the caller-side actual for write-back and for diagnostic
    /// context on errors.
    /// </summary>
    public required ProcedureCallStatement CallRef { get; init; }

    /// <summary>
    /// Formal parameter → caller-side actual <c>Variable</c>. Populated
    /// only for Out/InOut variable formals whose actual is a caller-scope
    /// <c>Variable</c> (not a literal or expression). Applied at
    /// completion via <c>ApplyProcedureWriteBack</c>; never at suspend
    /// (VHDL LRM: write-back is visible only after the procedure returns).
    /// </summary>
    public required Dictionary<ParameterDefinition, Variable> OutMap { get; init; }

    /// <summary>
    /// The caller-facing scope used when <see cref="Tasks"/> was parsed:
    /// caller variables + cloned local variables + coerced literals bound
    /// under their formal names. Kept alive by the frame so its
    /// <see cref="Variable"/> objects are not eligible for GC while the
    /// frame is suspended.
    /// </summary>
    public required HashSet<Variable> BoundScope { get; init; }
}
