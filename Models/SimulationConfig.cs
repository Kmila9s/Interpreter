using Parser.Models;

namespace Interpreter.Models;

/// <summary>
/// Configuration for running a simulation via SimulationRunner.
/// </summary>
public class SimulationConfig
{
    /// <summary>The design to simulate, in its language-neutral form (<see cref="Ir.IrDesign"/>).</summary>
    public required Ir.IrDesign Design { get; init; }

    /// <summary>
    /// The last tick of the run (the stop time): ticks 0..TotalTicks are simulated,
    /// events at the stop time included, as GHDL's <c>--stop-time</c>.
    /// </summary>
    public ulong TotalTicks { get; init; }

    /// <summary>
    /// Femtoseconds per tick: what every tick count in this configuration is in,
    /// and what the engine converts <c>after</c>/<c>wait for</c> delays with
    /// (<see cref="Services.StaticDelays.TickFs"/> picks one that lands every static delay).
    /// </summary>
    public long TickFs { get; init; } = PhysicalTime.DefaultTickFs;

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
    /// <summary>The clock port.</summary>
    public required Ir.IrSignal Signal { get; init; }

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

    /// <summary>The port to change.</summary>
    public required Ir.IrSignal Signal { get; init; }

    /// <summary>
    /// The value to set (as string, matching VHDL signal assignment format).
    /// </summary>
    public required string Value { get; init; }

    /// <summary>
    /// The part of <see cref="Signal"/> driven, as written (<c>p.lo</c>, <c>v(3)</c>; see
    /// <see cref="Ir.IrDesign.ResolvePort"/>), or null for the whole port.
    /// </summary>
    public string? Part { get; init; }

    /// <summary>This stimulus on the objects that hold <paramref name="design"/>'s values during a run.</summary>
    internal BoundStimulus Bind(Ir.IrDesign design) => new(
        design.Binding.Cell(Signal),
        Part is null ? null : design.Binding.ResolvePort(Part)?.Target,
        Value,
        Signal.PortMode);
}

/// <summary>
/// A <see cref="StimulusEvent"/> resolved to the object that holds its port's value
/// (transitional, until the engine drives ports by <see cref="Ir.IrSignal"/> in v3.2.3).
/// </summary>
internal sealed record BoundStimulus(Variable Cell, AssignmentTarget? Target, string Value, Ir.IrPortMode? Mode)
{
    /// <summary>
    /// Applies the stimulus to its port (or to the selected part of it). An
    /// <c>inout</c> port is driven through <paramref name="scheduler"/>'s external
    /// driver, so its value is the resolution with the design's own drivers.
    /// </summary>
    public void Apply(Interpreter.Services.SignalScheduler? scheduler = null)
    {
        if (scheduler is not null && Mode == Ir.IrPortMode.InOut)
        {
            if (Target is { } p) p.DriveExternal(scheduler, Value);
            else scheduler.DriveExternal(Cell, Value);
        }
        else if (Target is { } part) part.WriteNow(Value);
        else Cell.Value = Value;
    }
}
