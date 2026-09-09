namespace Interpreter.Models;

/// <summary>
/// F-G-13d (Phase 2, 2026-07-22): marker interface unifying the
/// resumable-execution frames a process can stack while suspended on a
/// wait statement inside a nested body. Two concrete implementations:
/// <list type="bullet">
/// <item><see cref="LoopFrame"/> — a for/while/simple loop paused
/// mid-iteration (shipped F-G-13c, Session 23).</item>
/// <item><see cref="BranchFrame"/> — an if or case branch paused
/// mid-task-list (shipped F-G-13d, Phase 2).</item>
/// </list>
/// The engine's frame-drain switch (in
/// <c>DeltaCycleEngine.ExecuteProcessSegmentAsync</c>) dispatches by
/// runtime type. Adding a new frame kind (e.g. <c>ProcedureFrame</c>
/// planned for Phase 3) means implementing this interface + adding
/// its switch arm.
/// </summary>
public interface IProcessFrame { }
