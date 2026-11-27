namespace Interpreter.Ir;

/// <summary>When a process runs.</summary>
public enum IrSensitivityKind
{
    /// <summary>On an event of a signal of its written sensitivity list.</summary>
    Explicit,
    /// <summary>VHDL-2008 <c>process (all)</c>: on an event of any signal its statements read.</summary>
    All,
    /// <summary>The equivalent process of a concurrent statement: on an event of a signal the statement names.</summary>
    Implicit,
    /// <summary>It suspends on its own <c>wait</c> statements (no sensitivity list).</summary>
    Wait,
    /// <summary>
    /// Every delta cycle: its value can change without an event on a signal it names
    /// (<c>'stable</c>, <c>'quiet</c>, <c>'delayed</c>, <c>'transaction</c>, <c>now</c>).
    /// </summary>
    EveryDelta,
}

/// <summary>Where a process comes from.</summary>
public enum IrProcessOrigin
{
    /// <summary>A <c>process</c> the user wrote.</summary>
    Written,
    /// <summary>The equivalent process of a concurrent statement (assignment, assertion, procedure call).</summary>
    Equivalent,
}

/// <summary>
/// One concurrent unit of a design — a written process or the equivalent process of a
/// concurrent statement — in the order the design declares it, with any generate already
/// unrolled. Its statements are not part of the IR yet (v3.2.2/v3.2.3): until then
/// <see cref="Body"/> is the parsed statement the engine runs.
/// </summary>
public sealed class IrProcess
{
    /// <summary>The label (<c>lbl : process</c>), or null.</summary>
    public string? Label { get; init; }

    /// <summary>1-based source line of the process or statement (0 when unknown).</summary>
    public int SourceLine { get; init; }

    /// <summary>Written process or equivalent process of a concurrent statement.</summary>
    public required IrProcessOrigin Origin { get; init; }

    /// <summary>When it runs.</summary>
    public required IrSensitivityKind SensitivityKind { get; init; }

    /// <summary>
    /// The signals whose events wake it (<see cref="IrSensitivityKind.Explicit"/>,
    /// <see cref="IrSensitivityKind.All"/>, <see cref="IrSensitivityKind.Implicit"/>); empty otherwise.
    /// </summary>
    public IReadOnlyList<IrSignal> Sensitivity { get; init; } = [];

    /// <summary>Its local variables (and constants).</summary>
    public IReadOnlyList<IrSignal> Variables { get; init; } = [];

    /// <summary>The parsed statement the engine runs (transitional: replaced by IR statements in v3.2.2/v3.2.3).</summary>
    internal object Body { get; init; } = null!;
}
