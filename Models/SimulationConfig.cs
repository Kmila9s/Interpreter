using Parser.Models;

namespace Interpreter.Models;

/// <summary>
/// Configuration for running a simulation via SimulationRunner.
/// </summary>
public class SimulationConfig
{
    /// <summary>
    /// The entity to simulate (must have an architecture).
    /// </summary>
    public required Services.Entity Entity { get; init; }

    /// <summary>
    /// Total number of simulation ticks to run.
    /// </summary>
    public ulong TotalTicks { get; init; }

    /// <summary>
    /// Clock signal configurations (auto-toggling clocks).
    /// </summary>
    public List<ClockConfig> Clocks { get; init; } = new();

    /// <summary>
    /// Stimulus events (value changes at specific ticks).
    /// </summary>
    public List<StimulusEvent> Stimuli { get; init; } = new();
}

/// <summary>
/// Defines an auto-toggling clock signal.
/// </summary>
public class ClockConfig
{
    /// <summary>
    /// The clock signal variable.
    /// </summary>
    public required Variable Signal { get; init; }

    /// <summary>
    /// Number of ticks per half-period (e.g., 1 means toggle every tick).
    /// </summary>
    public ulong HalfPeriodTicks { get; init; } = 1;

    /// <summary>
    /// Initial state of the clock ('0' or '1').
    /// </summary>
    public bool InitialHigh { get; init; } = false;
}

/// <summary>
/// A stimulus event: set a signal to a value at a specific tick.
/// </summary>
public class StimulusEvent
{
    /// <summary>
    /// The tick at which to apply this stimulus.
    /// </summary>
    public ulong Tick { get; init; }

    /// <summary>
    /// The signal to change.
    /// </summary>
    public required Variable Signal { get; init; }

    /// <summary>
    /// The value to set (as string, matching VHDL signal assignment format).
    /// </summary>
    public required string Value { get; init; }

    /// <summary>
    /// Part of <see cref="Signal"/> driven (<c>p.lo</c>, <c>v(3)</c>), or null for the whole port.
    /// </summary>
    public AssignmentTarget? Target { get; init; }

    /// <summary>Applies the stimulus to its signal (or to the selected part of it).</summary>
    public void Apply()
    {
        if (Target is { } part) part.WriteNow(Value);
        else Signal.Value = Value;
    }
}
