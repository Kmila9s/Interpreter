using Parser.Models;

namespace Interpreter.Models;

/// <summary>
/// F-G-13c (Session 23, 2026-07-18): reified iteration state for a loop
/// currently executing inside a wait-driven process. Pushed on the
/// owning <c>Process.Frames</c> stack when the engine's resumable loop
/// executor enters a loop, and popped when the loop completes normally.
///
/// When a <c>wait for</c> / <c>wait until</c> fires inside the loop
/// body, the frame captures which iteration and which body-task index
/// to resume at; the engine returns from <c>ExecuteProcessSegmentAsync</c>
/// without popping the frame, and the next wake picks up from the
/// frame's saved state.
/// </summary>
/// <remarks>
/// Only the resumable executors in <see cref="Services.DeltaCycleEngine"/>
/// consult these frames. The concurrent-task path (non-process ISControl
/// nodes reached via <c>ExecuteControlWithSchedulingAsync</c>) never sees
/// waits, so it stays on the host-stack executor and never touches the
/// frame stack.
///
/// Scope: F-G-13c handles waits captured directly in a for/while/simple
/// loop's task list. Waits inside a nested if/case/loop body remain
/// dropped at parse time (future work) because the frame model only
/// tracks one level of loop nesting per pushed frame.
/// </remarks>
public sealed class LoopFrame
{
    /// <summary>
    /// The loop AST node — a <see cref="Repositories.ForLoopStructure"/>,
    /// <see cref="Repositories.WhileLoopStructure"/>, or
    /// <see cref="Repositories.SimpleLoopStructure"/>. Discriminated by
    /// runtime type in the resumable executor.
    /// </summary>
    public required object LoopRef { get; init; }

    /// <summary>
    /// For a <c>for</c> loop, the current iterator value (bench bug #14 —
    /// the loop's own <see cref="Repositories.ForLoopStructure.LoopVar"/>
    /// is a shared mutable Variable, and this int is the snapshot we
    /// restore to it on resume).
    /// For while/simple loops, the iteration count (used only for the
    /// safety cap).
    /// </summary>
    public int IterationVal { get; set; }

    /// <summary>
    /// Resume index into the loop body's task list. Set to (idx + 1)
    /// when we suspend at task index idx, so the resume re-enters at
    /// the statement AFTER the wait.
    /// </summary>
    public int InnerPc { get; set; }
}
