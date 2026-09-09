namespace Interpreter.Models;

/// <summary>
/// F-G-13d (Phase 2, 2026-07-22): reified execution state for an
/// <c>if</c> / <c>elsif</c> / <c>else</c> branch OR a <c>case when</c>
/// arm currently executing inside a wait-driven process. Pushed on
/// <c>Process.Frames</c> when the engine's process-aware if/case runner
/// enters a branch; popped when the branch runs to completion. If a
/// <c>wait</c> fires inside the branch body, the frame captures the
/// resume position and stays on the stack until the wake fires.
///
/// Complements <see cref="LoopFrame"/> — same lifecycle, different
/// AST parent. Both implement <see cref="IProcessFrame"/> so the
/// engine's frame-drain switch can pattern-match by runtime type.
/// </summary>
/// <remarks>
/// Scope: a wait inside an if/case body suspends the whole process.
/// The resume path returns to the exact task after the wait — later
/// branches in the same if/case are NOT re-evaluated (the process
/// already picked its arm before yielding). This matches VHDL
/// semantics: <c>if x then wait for 5 ns; y &lt;= '1'; end if;</c>
/// resumes at <c>y &lt;= '1'</c>, not at re-checking <c>x</c>.
/// </remarks>
public sealed class BranchFrame : IProcessFrame
{
    /// <summary>
    /// The if/case AST node — an
    /// <see cref="Repositories.IfStructure"/> or
    /// <see cref="Repositories.CaseStructure"/>. Kept as
    /// <see cref="object"/> so the engine can pattern-match rather
    /// than commit to a common base type across the repositories.
    /// </summary>
    public required object StructureRef { get; init; }

    /// <summary>
    /// Which branch/arm we picked at entry.
    /// For <c>IfStructure</c>: index into <c>ifStruct.Branches</c>.
    /// For <c>CaseStructure</c>: index into <c>caseStruct.Branches</c>
    /// (the matched arm; -1 means no match — the runner returns
    /// immediately in that case, so a frame with BranchIdx = -1
    /// should never actually be pushed).
    /// </summary>
    public required int BranchIdx { get; init; }

    /// <summary>
    /// Resume index into the selected branch's <c>Tasks</c>. Set to
    /// (idx + 1) when a wait suspends at task index idx, so the resume
    /// re-enters at the statement AFTER the wait.
    /// </summary>
    public int InnerPc { get; set; }
}
