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
    /// The last tick of the run (the stop time): ticks 0..TotalTicks are simulated,
    /// events at the stop time included, as GHDL's <c>--stop-time</c>.
    /// </summary>
    public ulong TotalTicks { get; init; }

    /// <summary>
    /// Nanoseconds per tick: what every tick count in this configuration is in,
    /// and what the engine converts <c>after</c>/<c>wait for</c> delays with.
    /// </summary>
    public int TickResNs { get; init; } = TimeMachine.Models.TimeClock.NS_PER_TICK;

    /// <summary>
    /// Clock signal configurations (auto-toggling clocks).
    /// </summary>
    public List<ClockConfig> Clocks { get; init; } = new();

    /// <summary>
    /// Stimulus events (value changes at specific ticks).
    /// </summary>
    public List<StimulusEvent> Stimuli { get; init; } = new();

    /// <summary>
    /// Initial values of input ports set before time 0 (the actual's initial value in a
    /// testbench): the port starts there, with no event at t=0.
    /// </summary>
    public List<StimulusEvent> PortInitials { get; init; } = new();

    /// <summary>How objects declared without <c>:=</c> start (LRM by default).</summary>
    public InitialValuePolicy InitialValues { get; init; } = InitialValuePolicy.Lrm;
}

/// <summary>Initial values of objects declared without an explicit <c>:=</c>.</summary>
public enum InitialValuePolicy
{
    /// <summary>IEEE 1076 (like GHDL): every object starts at its declared value or <c>T'left</c> (<c>'U'</c> for std_logic).</summary>
    Lrm,
    /// <summary>
    /// Teaching aid: objects without <c>:=</c> and undriven ports start at 0 (<c>en</c>/<c>enable</c> at 1),
    /// so a design without reset shows numbers instead of <c>U</c>.
    /// </summary>
    Didactic,
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

    /// <summary>
    /// Applies the stimulus to its signal (or to the selected part of it). An
    /// <c>inout</c> port is driven through <paramref name="scheduler"/>'s external
    /// driver, so its value is the resolution with the design's own drivers.
    /// </summary>
    public void Apply(Interpreter.Services.SignalScheduler? scheduler = null)
    {
        if (scheduler is not null && Signal.PortType == Constants.PortType.INOUT)
        {
            if (Target is { } p) p.DriveExternal(scheduler, Value);
            else scheduler.DriveExternal(Signal, Value);
        }
        else if (Target is { } part) part.WriteNow(Value);
        else Signal.Value = Value;
    }
}
