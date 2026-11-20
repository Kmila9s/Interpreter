using Parser.Models;

namespace Interpreter.Services;

/// <summary>
/// Wraps an Operation to use signal scheduling instead of immediate assignment.
/// This enables proper VHDL delta cycle semantics where signal assignments are scheduled.
/// </summary>
public class ScheduledOperation
{
    private readonly Operation _operation;
    private readonly SignalScheduler _scheduler;

    /// <summary>
    /// Gets the underlying operation.
    /// </summary>
    public Operation UnderlyingOperation => _operation;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScheduledOperation"/> class.
    /// </summary>
    /// <param name="operation">The operation to wrap.</param>
    /// <param name="scheduler">The signal scheduler to use for updates.</param>
    public ScheduledOperation(Operation operation, SignalScheduler scheduler)
    {
        _operation = operation;
        _scheduler = scheduler;
    }

    /// <summary>
    /// Executes the operation but schedules the result instead of assigning immediately.
    /// This implements proper VHDL delta cycle semantics.
    /// </summary>
    public void Execute() => Run(_operation, _scheduler);

    /// <summary>
    /// Evaluates <paramref name="operation"/> and writes its value through
    /// <paramref name="scheduler"/> (no wrapper allocation for callers that
    /// keep the operation, such as the branches of selected/conditional
    /// assignments).
    /// </summary>
    public static void Run(Operation operation, SignalScheduler scheduler)
    {
        operation.EnsureWritable();
        object computedValue = operation.Evaluate();

        // Whole or part target, waveform with its delay mechanism, or immediate
        // variable (`:=`) semantics: one place decides how the value is written.
        if (operation.IsVariableAssignment)
        {
            if (operation.AggregateTarget is { } va) va.Assign(scheduler, computedValue, operation.SourceLine, immediate: true);
            else if (operation.Target is { } vt) vt.Assign(scheduler, computedValue, operation.SourceLine, immediate: true);
            else operation.Result.Value = computedValue;   // procedure-local temporary (no declared target)
            return;
        }
        var waveform = operation.WaveformValues(computedValue);
        var mechanism = operation.Waveform?.Mechanism ?? DelayMechanism.Inertial;
        if (operation.AggregateTarget is { } aggregate)
            aggregate.Schedule(scheduler, waveform, operation.SourceLine, mechanism, operation.RejectNs);
        else if (operation.Target is { } target)
            target.Schedule(scheduler, waveform, operation.SourceLine, mechanism, operation.RejectNs);
        else
            throw new InvalidOperationException($"signal assignment without a target at Line {operation.SourceLine}");
    }

    /// <summary>
    /// Creates a ScheduledOperation from an Operation and SignalScheduler.
    /// </summary>
    /// <param name="operation">The operation to wrap.</param>
    /// <param name="scheduler">The signal scheduler.</param>
    /// <returns>A new ScheduledOperation instance.</returns>
    public static ScheduledOperation Create(Operation operation, SignalScheduler scheduler)
    {
        return new ScheduledOperation(operation, scheduler);
    }
}
