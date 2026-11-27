using Interpreter.Services;

namespace Interpreter.Ir;

/// <summary>A port, or a part of one (<c>v(3)</c>, <c>p.lo</c>), that a run drives from outside.</summary>
/// <param name="Port">The port.</param>
/// <param name="Part">The selected part as written (<c>p.lo</c>), or null for the whole port.</param>
public sealed record IrPortRef(IrSignal Port, string? Part);

/// <summary>
/// A design ready to simulate, in the language-neutral form every front-end produces: its
/// objects (<see cref="IrSignal"/>) and its concurrent units (<see cref="IrProcess"/>). The
/// engine, the stimulus builder and the test harness read this instead of a language's
/// parse tree, so a new HDL only has to lower its parse tree to it (Fase 2, v3.2.1).
/// </summary>
public sealed class IrDesign
{
    /// <summary>The design unit's name (VHDL entity, Verilog module).</summary>
    public required string Name { get; init; }

    /// <summary>The ports, in declaration order.</summary>
    public required IReadOnlyList<IrSignal> Ports { get; init; }

    /// <summary>The generics/parameters.</summary>
    public IReadOnlyList<IrSignal> Generics { get; init; } = [];

    /// <summary>The objects declared in the body: signals and constants.</summary>
    public IReadOnlyList<IrSignal> Signals { get; init; } = [];

    /// <summary>The concurrent units, in declaration order, generates unrolled.</summary>
    public IReadOnlyList<IrProcess> Processes { get; init; } = [];

    /// <summary>Where the lines of an elaborated design come from (G82); null for a design as written.</summary>
    public ElaborationLineMap? LineMap { get; init; }

    /// <summary>The transitional link to the objects that hold the run's values (until v3.2.3).</summary>
    public required IrBinding Binding { get; init; }

    /// <summary>The ports and the body objects: what a run registers, settles and traces.</summary>
    public IEnumerable<IrSignal> Objects => Ports.Concat(Signals);

    /// <summary>
    /// The port named by <paramref name="path"/>, optionally followed by index, slice or field
    /// selectors (<c>v(3)</c>, <c>p.lo</c>); null when no port has that name or the selectors
    /// do not apply to it.
    /// </summary>
    public IrPortRef? ResolvePort(string path) =>
        Binding.ResolvePort(path) is var (port, target) ? new IrPortRef(port, target.IsPartial ? path : null) : null;
}
