using Interpreter.Models;
using Parser.Models;

namespace Interpreter.Ir;

/// <summary>
/// Transitional link between a design's IR and the parsed objects that still hold the run's
/// values (<see cref="Variable"/>): the IR describes, the cell stores. It goes away in v3.2.3,
/// when the engine owns signal storage and drives everything by <see cref="IrSignal"/>.
/// </summary>
public sealed class IrBinding
{
    private readonly Dictionary<IrSignal, Variable> _cells;
    // The port cells, as the scope a port path resolves in (VHDL: by name, case-insensitive).
    private readonly HashSet<Variable> _portScope;
    private readonly Dictionary<Variable, IrSignal> _ports = new(ReferenceEqualityComparer.Instance);

    internal IrBinding(Dictionary<IrSignal, Variable> cells, IEnumerable<IrSignal> ports, IEnumerable<IrSignal> objects)
    {
        _cells = cells;
        foreach (var port in ports) _ports[cells[port]] = port;
        _portScope = [.. _ports.Keys];
        Objects = [.. objects.Select(o => cells[o])];
    }

    /// <summary>The cells of <see cref="IrDesign.Objects"/>, in that order: what a run registers, settles and traces.</summary>
    internal HashSet<Variable> Objects { get; }

    /// <summary>The parsed object that holds <paramref name="signal"/>'s value during a run.</summary>
    public Variable Cell(IrSignal signal) => _cells[signal];

    /// <summary>
    /// The port <paramref name="path"/> names (with any index, slice or field selectors) and the
    /// write target on its cell; null when no port has that name or the selectors do not apply.
    /// </summary>
    internal (IrSignal Port, AssignmentTarget Target)? ResolvePort(string path) =>
        AssignmentTarget.Resolve(path, _portScope) is { } target ? (_ports[target.Base], target) : null;
}
