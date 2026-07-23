namespace Interpreter.Synthesis;

/// <summary>
/// Distinct gate kinds emitted by the synthesizer. Combinational primitives
/// (AND/OR/XOR/NOT/BUF) come from boolean expressions; CONST/MUX from
/// conditional or selected assignments; DFF from clocked processes; the
/// arithmetic kinds (ADD/SUB/MUL/DIV/MOD/POW/SHL/SHR) are kept opaque so
/// an 8-bit adder doesn't render as 50 gates by default. BLACKBOX_PROCESS
/// is the fallback when a process body is too tangled to decompose.
/// </summary>
public enum CellKind {
    AND, NAND, OR, NOR, XOR, XNOR, NOT, BUF,
    CONST, MUX,
    DFF,
    ADD, SUB, MUL, DIV, MOD, POW,
    SHL, SHR, ROL, ROR,
    EQ, NEQ, LT, LTE, GT, GTE,
    // IEEE primitive cells emitted by Interpreter.Synthesis.IEEEPrimitives.
    // The lookup table maps `to_integer`, `to_unsigned`, `resize`, etc. to
    // these kinds so the netlist reflects real hardware shapes instead of
    // an opaque "@FunctionName" constant placeholder.
    /// <summary>Bit-weighted-sum reduction: bus → integer.</summary>
    TO_INTEGER,
    /// <summary>Integer literal expanded into a bus of declared width.</summary>
    TO_VECTOR,
    /// <summary>Zero/sign-extend or truncate a bus to a new width.</summary>
    RESIZE,
    /// <summary>Posedge or negedge detector on a clock-class wire.</summary>
    EDGE_DETECT,
    BLACKBOX_PROCESS,
    /// <summary>Opaque module for a component instantiation. Rendered as a
    /// rectangle with one pin per port-map binding; the label carries the
    /// referenced entity name so students see the hierarchy without the
    /// synth pass having to inline every sub-module.</summary>
    BLACKBOX_COMPONENT
}

/// <summary>
/// Where a net came from in the source. Drives the renderer's labelling and
/// terminal placement (top/bottom strip).
/// </summary>
public enum NetOrigin { PortIn, PortOut, PortInOut, Signal, Internal, Constant }

/// <summary>
/// Whether a net is a single-bit wire or a multi-bit bus. Drives stroke
/// thickness in the renderer (buses draw thicker) and the bit-width chip on
/// the schematic info panel.
/// </summary>
public enum NetKind { Wire, Bus }

/// <summary>
/// Direction of a port pin. Mirrors the upstream Variable.PortType but kept
/// separate so the netlist data model has zero dependency on Parser.Models.
/// </summary>
public enum PortDir { In, Out, InOut, Buffer }

/// <summary>
/// A wire (1-bit) or bus (N-bit). Buses carry the bit width so the renderer
/// can draw thicker lines and the Phase-5 explode toggle can fan a single
/// bus out to N parallel wires without re-synthesizing.
/// </summary>
public sealed record Net(
    string Id,
    string Name,
    NetKind Kind,
    int Width,
    NetOrigin Origin,
    string? SourceLine = null,
    string? ConstValue = null
);

/// <summary>
/// A pin on a cell. NetId references a <see cref="Net"/> in the parent
/// netlist; BusBit is set when the pin taps a single bit out of a bus
/// (for indexed access like <c>a(3)</c>).
/// </summary>
public sealed record Pin(string Name, string NetId, int? BusBit = null);

/// <summary>
/// A single gate / register / black-box cell. Inputs and Outputs are pin
/// lists rather than scalars because a DFF has both D and clock pins, a
/// MUX has selector + data inputs, and an adder cell can have a carry-out.
/// </summary>
public sealed record Cell(
    string Id,
    CellKind Kind,
    IReadOnlyList<Pin> Inputs,
    IReadOnlyList<Pin> Outputs,
    string? SourceLine = null,
    IReadOnlyDictionary<string, string>? Attrs = null
);

/// <summary>
/// External-facing pin on the entity (port). Lives at the top/bottom of the
/// schematic and connects to a normal <see cref="Net"/>.
/// </summary>
public sealed record PortPin(string Name, PortDir Direction, int Width, string NetId);

/// <summary>
/// The full synthesised circuit. One per entity. Pure data — no behaviour,
/// no layout, no UI. Layout adds <see cref="LaidOutNetlist"/> on top.
/// </summary>
public sealed record Netlist(
    string EntityName,
    IReadOnlyList<PortPin> Ports,
    IReadOnlyList<Net> Nets,
    IReadOnlyList<Cell> Cells,
    IReadOnlyList<Diagnostic> Diagnostics
);

/// <summary>
/// Synthesis-time message surfaced to the UI (e.g. "process body fell back
/// to BLACKBOX_PROCESS"). Doesn't fail the build — just informs.
/// </summary>
public sealed record Diagnostic(string Kind, string Message, string? SourceLine = null);
