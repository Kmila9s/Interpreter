using Parser.Models;
using static Parser.Models.Constants;

namespace Interpreter.Synthesis;

/// <summary>
/// Mutable builder that accumulates nets and cells while a single
/// <see cref="Synthesizer"/> walks an architecture. Final immutable
/// <see cref="Netlist"/> comes out of <see cref="Build"/> at the end.
/// Kept internal to the synthesizer to avoid leaking the mutable state
/// to callers.
/// </summary>
internal sealed class SynthesisContext
{
    private readonly Dictionary<string, Net> _netsByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Net> _nets = new();
    private readonly List<Cell> _cells = new();
    private readonly List<PortPin> _ports = new();
    private readonly List<Diagnostic> _diagnostics = new();
    private int _internalCounter;
    private int _cellCounter;

    /// <summary>
    /// Name of the entity being synthesized. Stamped onto the final
    /// <see cref="Netlist"/> so consumers (export, schematic viewer) can
    /// label the diagram.
    /// </summary>
    public string EntityName { get; }

    /// <summary>
    /// Initializes a new builder bound to a specific entity name.
    /// </summary>
    /// <param name="entityName">VHDL entity name; used as the netlist label.</param>
    public SynthesisContext(string entityName) { EntityName = entityName; }

    /// <summary>
    /// Returns the existing net for a signal name, creating it lazily as
    /// an internal net if it isn't a port or declared signal yet. Lookup
    /// is case-insensitive to match VHDL identifier rules.
    /// </summary>
    public Net GetOrCreateNet(string name, NetKind kind, int width, NetOrigin origin, string? sourceLine = null)
    {
        if (_netsByName.TryGetValue(name, out var existing))
            return existing;

        var net = new Net(
            Id: $"net_{_nets.Count}",
            Name: name,
            Kind: kind,
            Width: Math.Max(1, width),
            Origin: origin,
            SourceLine: sourceLine);
        _netsByName[name] = net;
        _nets.Add(net);
        return net;
    }

    /// <summary>
    /// Allocates a fresh internal net for an intermediate gate output. The
    /// generated name embeds the source line when available so the Naive
    /// view can group cells by origin without an extra side-table.
    /// </summary>
    public Net NewInternal(int width = 1, string? sourceLine = null)
    {
        string name = sourceLine != null
            ? $"_n{_internalCounter}_L{sourceLine}"
            : $"_n{_internalCounter}";
        _internalCounter++;
        return GetOrCreateNet(name, width > 1 ? NetKind.Bus : NetKind.Wire, width, NetOrigin.Internal, sourceLine);
    }

    /// <summary>
    /// Records a constant net (e.g. literal '0', "0101"). Reuses an
    /// existing constant of the same value to keep the diagram tidy.
    /// </summary>
    public Net Constant(string value, int width = 1, string? sourceLine = null)
    {
        string name = $"const_{value.Replace("'", "").Replace("\"", "")}";
        if (_netsByName.TryGetValue(name, out var existing))
            return existing;
        var net = new Net(
            Id: $"net_{_nets.Count}",
            Name: name,
            Kind: width > 1 ? NetKind.Bus : NetKind.Wire,
            Width: Math.Max(1, width),
            Origin: NetOrigin.Constant,
            SourceLine: sourceLine,
            ConstValue: value);
        _netsByName[name] = net;
        _nets.Add(net);
        return net;
    }

    /// <summary>
    /// Appends a logic cell (gate/MUX/DFF/etc.) to the netlist with a fresh
    /// monotonic id. Inputs and outputs are passed by reference and stored as
    /// supplied — the caller is responsible for wiring them to existing nets.
    /// </summary>
    /// <param name="kind">Cell kind from <see cref="CellKind"/>.</param>
    /// <param name="inputs">Input pins of the cell, in declaration order.</param>
    /// <param name="outputs">Output pins of the cell, in declaration order.</param>
    /// <param name="sourceLine">Optional VHDL source line that produced the cell; used by Naive grouping.</param>
    /// <param name="attrs">Optional cell-specific attribute dictionary (e.g. equality comparand).</param>
    /// <returns>The newly added <see cref="Cell"/>.</returns>
    public Cell AddCell(CellKind kind, IReadOnlyList<Pin> inputs, IReadOnlyList<Pin> outputs,
        string? sourceLine = null, IReadOnlyDictionary<string, string>? attrs = null)
    {
        var cell = new Cell(
            Id: $"c{_cellCounter++}",
            Kind: kind,
            Inputs: inputs,
            Outputs: outputs,
            SourceLine: sourceLine,
            Attrs: attrs);
        _cells.Add(cell);
        return cell;
    }

    /// <summary>
    /// Registers an entity port: creates the corresponding net if missing and
    /// emits a <see cref="PortPin"/> entry for the netlist boundary.
    /// </summary>
    /// <param name="port">VHDL port descriptor parsed from the entity declaration.</param>
    public void AddPort(Variable port)
    {
        var origin = port.PortType switch {
            PortType.IN => NetOrigin.PortIn,
            PortType.OUT => NetOrigin.PortOut,
            PortType.INOUT => NetOrigin.PortInOut,
            _ => NetOrigin.PortIn,
        };
        int width = port.Type == DATATYPES.STD_LOGIC_VECTOR ? Math.Max(1, port.Size) : 1;
        var kind = width > 1 ? NetKind.Bus : NetKind.Wire;
        var net = GetOrCreateNet(port.Name, kind, width, origin);
        _ports.Add(new PortPin(
            port.Name,
            port.PortType switch {
                PortType.IN => PortDir.In,
                PortType.OUT => PortDir.Out,
                PortType.INOUT => PortDir.InOut,
                _ => PortDir.In,
            },
            width,
            net.Id));
    }

    /// <summary>
    /// Records a declared internal signal so it has a stable named net before
    /// any cell wires it. No-op if the signal name is already known.
    /// </summary>
    /// <param name="signal">Signal descriptor parsed from the architecture.</param>
    public void AddSignal(Variable signal)
    {
        int width = signal.Type == DATATYPES.STD_LOGIC_VECTOR ? Math.Max(1, signal.Size) : 1;
        GetOrCreateNet(signal.Name, width > 1 ? NetKind.Bus : NetKind.Wire, width, NetOrigin.Signal);
    }

    /// <summary>
    /// Adds a diagnostic message (e.g. unsynthesizable construct, blackbox).
    /// Bubbled up to the final <see cref="Netlist"/> so the schematic viewer
    /// can show banners and the user can act on them.
    /// </summary>
    /// <param name="kind">Diagnostic kind label, e.g. <c>"BlackboxProcess"</c>.</param>
    /// <param name="message">Human-readable explanation.</param>
    /// <param name="sourceLine">Optional source line associated with the diagnostic.</param>
    public void AddDiagnostic(string kind, string message, string? sourceLine = null)
        => _diagnostics.Add(new Diagnostic(kind, message, sourceLine));

    /// <summary>
    /// Freezes the accumulated state into an immutable <see cref="Netlist"/>
    /// suitable for layout, rendering, and export. After this call the
    /// context should be discarded — callers wanting to mutate further must
    /// build a new context.
    /// </summary>
    /// <returns>A fully populated, immutable netlist.</returns>
    public Netlist Build()
        => new(EntityName, _ports, _nets, _cells, _diagnostics);
}
