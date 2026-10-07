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
    /// <summary>Logical AND gate.</summary>
    AND,
    /// <summary>Logical NAND (NOT-AND) gate.</summary>
    NAND,
    /// <summary>Logical OR gate.</summary>
    OR,
    /// <summary>Logical NOR (NOT-OR) gate.</summary>
    NOR,
    /// <summary>Logical XOR (exclusive-OR) gate.</summary>
    XOR,
    /// <summary>Logical XNOR (exclusive-NOR) gate.</summary>
    XNOR,
    /// <summary>Logical NOT (inverter) gate.</summary>
    NOT,
    /// <summary>Buffer (non-inverting pass-through) gate.</summary>
    BUF,
    /// <summary>Constant-value driver.</summary>
    CONST,
    /// <summary>Multiplexer (data selector).</summary>
    MUX,
    /// <summary>D-type flip-flop (edge-triggered register).</summary>
    DFF,
    /// <summary>Arithmetic addition.</summary>
    ADD,
    /// <summary>Arithmetic subtraction.</summary>
    SUB,
    /// <summary>Arithmetic multiplication.</summary>
    MUL,
    /// <summary>Arithmetic division.</summary>
    DIV,
    /// <summary>Arithmetic modulo (remainder).</summary>
    MOD,
    /// <summary>Arithmetic exponentiation (power).</summary>
    POW,
    /// <summary>Logical/arithmetic shift left.</summary>
    SHL,
    /// <summary>Logical/arithmetic shift right.</summary>
    SHR,
    /// <summary>Rotate left.</summary>
    ROL,
    /// <summary>Rotate right.</summary>
    ROR,
    /// <summary>Equality comparison (=).</summary>
    EQ,
    /// <summary>Inequality comparison (/=).</summary>
    NEQ,
    /// <summary>Less-than comparison (&lt;).</summary>
    LT,
    /// <summary>Less-than-or-equal comparison (&lt;=).</summary>
    LTE,
    /// <summary>Greater-than comparison (&gt;).</summary>
    GT,
    /// <summary>Greater-than-or-equal comparison (&gt;=).</summary>
    GTE,
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
    /// <summary>Signal 'last_value attribute: sample of the source signal
    /// at its previous transition. Opaque cell — implementation uses the
    /// engine's shadow-rotation store, not a discrete hardware primitive,
    /// so the schematic shows a labeled box rather than gates. Emitted by
    /// the DeferredAttribute walker case in ConcurrentAssignSynthesizer.
    /// Width matches the source signal.</summary>
    LAST_VALUE,
    /// <summary>Signal 'stable[(T)] attribute: predicate that reports
    /// whether the source has remained unchanged for T ns (or since
    /// the last delta if T is omitted). Opaque cell — implementation
    /// uses the engine's per-signal change-time store. Emitted by the
    /// DeferredAttribute walker case; the T value (if present) rides
    /// on the cell's Attrs["TimeNs"]. Always 1-bit output.</summary>
    STABLE,
    /// <summary>Constant-array lookup with a runtime-computed integer
    /// index — a real N-to-1 MUX in hardware. Emitted by the
    /// DynamicIndex walker case when the source is a constant array of
    /// vectors (Source.Value non-empty, Source.ElementSize > 0) AND the
    /// index doesn't resolve statically. Pin shape: `Sel` (selector,
    /// integer-typed), `D0`..`D{N-1}` (constant data pins, one per array
    /// entry), `Y` (output, ElementSize wide). Attrs["N"] carries the
    /// entry count so the renderer can stamp `rom(N)` on the box.</summary>
    ROM_LOOKUP,
    /// <summary>Signal-typed array read with a runtime-computed index —
    /// the mutable-memory counterpart to <see cref="ROM_LOOKUP"/>. Emitted
    /// when the source is a signal (not constant) whose ElementSize > 0.
    /// Doesn't carry frozen data pins (the RAM's contents change at
    /// runtime); the source signal net feeds the `Src` pin as a bus of
    /// N*ElementSize bits and the cell semantically slices at `Sel *
    /// ElementSize`. Attrs["N"] and Attrs["Element"] label the box; the
    /// renderer stamps `ram(N)`. Pass 16 (2026-07-29).</summary>
    RAM_READ,
    /// <summary>Signal-typed array WRITE with a runtime-computed index —
    /// the write-side counterpart of <see cref="RAM_READ"/>. Emitted for
    /// dynamic-index assignments like <c>mem(to_integer(unsigned(addr))) &lt;= din</c>
    /// inside a process. Pin shape: `MemIn` (current memory state),
    /// `Sel` (index selector), `Din` (data to write) — output pin
    /// `MemOut` becomes the new memory state driving the base signal.
    /// Attrs["Element"], ["Source"], and ["N"] mirror RAM_READ. Pass 17
    /// (2026-07-29). Before pass 17 these writes silently dropped as
    /// Operation.ConditionResult placeholders — the RAM's write side
    /// was invisible on the schematic.</summary>
    RAM_WRITE,
    /// <summary>Fallback cell for a process body too tangled to decompose into
    /// discrete gates; rendered as an opaque labeled box.</summary>
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
public enum NetOrigin
{
    /// <summary>Net driven by an input port.</summary>
    PortIn,
    /// <summary>Net driving an output port.</summary>
    PortOut,
    /// <summary>Net tied to a bidirectional (inout) port.</summary>
    PortInOut,
    /// <summary>Net corresponding to a declared internal signal.</summary>
    Signal,
    /// <summary>Synthesizer-created internal net (no source-level name).</summary>
    Internal,
    /// <summary>Net carrying a constant value.</summary>
    Constant
}

/// <summary>
/// Whether a net is a single-bit wire or a multi-bit bus. Drives stroke
/// thickness in the renderer (buses draw thicker) and the bit-width chip on
/// the schematic info panel.
/// </summary>
public enum NetKind
{
    /// <summary>Single-bit net.</summary>
    Wire,
    /// <summary>Multi-bit net (vector).</summary>
    Bus
}

/// <summary>
/// Direction of a port pin. Mirrors the upstream Variable.PortType but kept
/// separate so the netlist data model has zero dependency on Parser.Models.
/// </summary>
public enum PortDir
{
    /// <summary>Input port.</summary>
    In,
    /// <summary>Output port.</summary>
    Out,
    /// <summary>Bidirectional port.</summary>
    InOut,
    /// <summary>Buffer port (readable output).</summary>
    Buffer
}

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
/// no layout, no UI. Layout adds <c>LaidOutNetlist</c> on top.
/// </summary>
public sealed record Netlist(
    string EntityName,
    IReadOnlyList<PortPin> Ports,
    IReadOnlyList<Net> Nets,
    IReadOnlyList<Cell> Cells,
    IReadOnlyList<Diagnostic> Diagnostics
);

/// <summary>
/// Severity of a synthesis-time <see cref="Diagnostic"/>. Info surfaces
/// covered-path fall-backs where the runtime is still correct and only the
/// schematic degrades (e.g. UserFunctionBlackBox for shapes we haven't
/// inlined yet); Warning surfaces the traditional black-box fall-back;
/// Error is reserved for structurally broken inputs.
/// </summary>
public enum DiagnosticSeverity
{
    /// <summary>Informational message.</summary>
    Info,
    /// <summary>Warning that does not prevent synthesis.</summary>
    Warning,
    /// <summary>Error that prevents successful synthesis.</summary>
    Error
}

/// <summary>
/// Synthesis-time message surfaced to the UI (e.g. "process body fell back
/// to BLACKBOX_PROCESS"). Doesn't fail the build — just informs.
/// <see cref="Severity"/> defaults to <c>Warning</c> for source compatibility
/// with call sites that predate the severity field.
/// </summary>
public sealed record Diagnostic(
    string Kind,
    string Message,
    string? SourceLine = null,
    DiagnosticSeverity Severity = DiagnosticSeverity.Warning);
