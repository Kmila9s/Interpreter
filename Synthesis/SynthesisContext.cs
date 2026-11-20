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
    // Tier 4.3 B3 (2026-07-28): scope-alias stack. When a callee (function or
    // procedure body) is being walked at synth time, its formal-parameter
    // names must resolve to the CALLER-provided nets rather than to
    // architecture-scope nets that happen to share the identifier. Each frame
    // is a case-insensitive map of {callee-local-name → caller-net}. Callers
    // push a frame before walking the body and pop it after. Case-insensitive
    // to match VHDL identifier rules (parallel to <c>_netsByName</c>).
    private readonly Stack<Dictionary<string, Net>> _scopeStack = new();
    // Tier 4.3 B1: recursion / self-call guard. Prevents infinite inlining
    // when a function body references itself. Also caps mutual-recursion by
    // aborting after the first re-entry per name.
    private readonly HashSet<string> _activeInlineFns = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Attempt to enter an inlining context for the given fn name; returns false on re-entry.</summary>
    public bool BeginInline(string functionName) => _activeInlineFns.Add(functionName);

    /// <summary>Exit an inlining context. Must pair with a successful BeginInline.</summary>
    public void EndInline(string functionName) => _activeInlineFns.Remove(functionName);

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

    /// <summary>Returns an already-created net by name (case-insensitive), or
    /// null. Used by the component schematic pass to tie a port-map bit-slice
    /// like <c>a(0)</c> back to its existing bus net <c>a</c>.</summary>
    public Net? TryGetNet(string name)
        => _netsByName.TryGetValue(name, out var n) ? n : null;

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
            PortType.OUT or PortType.BUFFER => NetOrigin.PortOut,
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
                PortType.OUT or PortType.BUFFER => PortDir.Out,
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
    /// Tier 4.3 B3: push a scope frame whose callee-local names bind to
    /// caller-provided nets. Used by function/procedure body walkers so
    /// references to formal parameters (or local variables materialised as
    /// intermediate nets) resolve to the right wire during body inlining.
    /// Every push MUST be matched by a <see cref="PopScope"/>.
    /// </summary>
    public void PushScope(IDictionary<string, Net> aliases)
    {
        var frame = new Dictionary<string, Net>(aliases, StringComparer.OrdinalIgnoreCase);
        _scopeStack.Push(frame);
    }

    /// <summary>Pop the top scope frame. No-op if the stack is empty.</summary>
    public void PopScope()
    {
        if (_scopeStack.Count > 0) _scopeStack.Pop();
    }

    /// <summary>
    /// Tier 4.3 (2026-07-28): update or insert a callee-local name binding on
    /// the top scope frame. Used by the function-body walker to record local-
    /// variable writes as an SSA rename — each write overwrites the previous
    /// binding, so later reads resolve to the most-recent driver. No-op if the
    /// scope stack is empty (guards against callers forgetting PushScope).
    /// </summary>
    public void SetAlias(string name, Net net)
    {
        if (_scopeStack.Count == 0) return;
        _scopeStack.Peek()[name] = net;
    }

    /// <summary>
    /// Walk the scope stack top-down looking for an alias for <paramref name="name"/>.
    /// Returns true and sets <paramref name="net"/> on hit; false otherwise. Used by
    /// <c>NetForVariable</c> in body walkers to shadow architecture-scope names
    /// with caller-provided nets during callee inlining.
    /// </summary>
    public bool TryResolveAlias(string name, out Net? net)
    {
        foreach (var frame in _scopeStack)
        {
            if (frame.TryGetValue(name, out var aliased))
            {
                net = aliased;
                return true;
            }
        }
        net = null;
        return false;
    }

    /// <summary>
    /// Adds a diagnostic message (e.g. unsynthesizable construct, blackbox).
    /// Bubbled up to the final <see cref="Netlist"/> so the schematic viewer
    /// can show banners and the user can act on them. Severity defaults to
    /// <see cref="DiagnosticSeverity.Warning"/> so existing call sites keep
    /// their prior behaviour; covered-path fall-backs pass
    /// <see cref="DiagnosticSeverity.Info"/> explicitly.
    /// </summary>
    /// <param name="kind">Diagnostic kind label, e.g. <c>"BlackboxProcess"</c>.</param>
    /// <param name="message">Human-readable explanation.</param>
    /// <param name="sourceLine">Optional source line associated with the diagnostic.</param>
    /// <param name="severity">Info / Warning / Error; see <see cref="DiagnosticSeverity"/>.</param>
    public void AddDiagnostic(string kind, string message, string? sourceLine = null,
        DiagnosticSeverity severity = DiagnosticSeverity.Warning)
        => _diagnostics.Add(new Diagnostic(kind, message, sourceLine, severity));

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
