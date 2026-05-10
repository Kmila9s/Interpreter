namespace Interpreter.Synthesis;

/// <summary>
/// Argument passed to an <see cref="IIEEEPrimitive"/> emitter — the input
/// nets the call site has already resolved, plus a context handle and the
/// optional source line for diagnostics / Naive-view grouping.
/// </summary>
/// <remarks>
/// Marked <c>internal</c> because <see cref="SynthesisContext"/> itself is
/// internal — the emitter table is a synth-internal extension point, not a
/// public API. Hosts that want to extend it do so through the
/// <see cref="IEEEPrimitives.Register"/> static, which exposes a public
/// surface keyed only on names + cell kinds.
/// </remarks>
/// <param name="Args">Input nets in the order they appeared in the call.</param>
/// <param name="Ctx">Synthesis context for emitting nets and cells.</param>
/// <param name="SourceLine">Optional VHDL source line.</param>
internal readonly record struct IEEEPrimitiveArgs(
    IReadOnlyList<Net> Args,
    SynthesisContext Ctx,
    string? SourceLine);

/// <summary>
/// Result of an <see cref="IIEEEPrimitive"/> emit pass.
/// </summary>
/// <param name="OutputNet">
/// The single output net the call produces. Callers wire this into the
/// surrounding expression — for an assignment that's the LHS net, for a
/// nested call it goes back onto the operand stack.
/// </param>
/// <param name="EmittedCells">
/// Number of cells the emitter added. Useful for the test harness: it can
/// snapshot the netlist before/after the emit and assert the delta.
/// </param>
internal readonly record struct IEEEPrimitiveResult(Net OutputNet, int EmittedCells);

/// <summary>
/// Lowering rule for one IEEE function name. Each implementation describes
/// the cells it emits and which output net it returns. Stateless — all
/// state lives in the <see cref="SynthesisContext"/> the caller supplies.
/// </summary>
/// <remarks>
/// Marked <c>internal</c> for the same reason as <see cref="IEEEPrimitiveArgs"/> —
/// it composes with the internal <see cref="SynthesisContext"/>.
/// </remarks>
internal interface IIEEEPrimitive
{
    /// <summary>The canonical IEEE function name (lowercased).</summary>
    string CanonicalName { get; }

    /// <summary>
    /// Other names that resolve to the same primitive. Empty for pure
    /// canonical entries; non-empty for legacy / Synopsys aliases like
    /// <c>conv_integer</c> aliased to <c>to_integer</c>.
    /// </summary>
    IReadOnlyList<string> Aliases { get; }

    /// <summary>The cell kind this primitive emits as its terminal cell.</summary>
    CellKind PrimaryCellKind { get; }

    /// <summary>
    /// Lowers a call into one or more cells in the supplied context, and
    /// returns the net that carries the result.
    /// </summary>
    IEEEPrimitiveResult Lower(IEEEPrimitiveArgs args);
}

/// <summary>
/// Process-wide registry of IEEE primitive emitters. Keyed case-insensitively
/// by both canonical name and any declared aliases. Hosts can extend the
/// table at startup with additional primitives — useful for vendor-specific
/// libraries (e.g. Xilinx UNISIM) that reuse the same lowering machinery.
/// </summary>
/// <remarks>
/// The class itself is <c>internal</c> because its inputs reference the
/// internal <see cref="SynthesisContext"/>. Tests in the same assembly
/// can drive it directly; hosts that want to extend it can do so via
/// <c>InternalsVisibleTo</c> if needed.
/// </remarks>
internal static class IEEEPrimitives
{
    private static readonly Dictionary<string, IIEEEPrimitive> _byName =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _lock = new();

    static IEEEPrimitives()
    {
        // Seed the canonical IEEE set on first access. Order doesn't matter —
        // entries are looked up by name.
        Register(new RisingEdgePrimitive());
        Register(new FallingEdgePrimitive());
        Register(new ToIntegerPrimitive());
        Register(new ToUnsignedPrimitive());
        Register(new ToSignedPrimitive());
        Register(new ResizePrimitive());
        Register(new ShiftLeftPrimitive());
        Register(new ShiftRightPrimitive());
        Register(new RotateLeftPrimitive());
        Register(new RotateRightPrimitive());
        Register(new TypeCastPrimitive("unsigned"));
        Register(new TypeCastPrimitive("signed"));
        Register(new TypeCastPrimitive("std_logic_vector"));
    }

    /// <summary>Adds or replaces a primitive entry by canonical name + aliases.</summary>
    public static void Register(IIEEEPrimitive primitive)
    {
        lock (_lock)
        {
            _byName[primitive.CanonicalName] = primitive;
            foreach (var alias in primitive.Aliases)
                _byName[alias] = primitive;
        }
    }

    /// <summary>True iff <paramref name="name"/> resolves to a known emitter.</summary>
    public static bool IsKnown(string name)
    {
        lock (_lock) return _byName.ContainsKey(name);
    }

    /// <summary>Returns the emitter registered for <paramref name="name"/>, or null.</summary>
    public static IIEEEPrimitive? Lookup(string name)
    {
        lock (_lock)
        {
            return _byName.TryGetValue(name, out var p) ? p : null;
        }
    }

    /// <summary>
    /// Convenience: lookup-and-lower in a single call. Returns null when the
    /// function name isn't in the table — caller handles the fallback path
    /// (typically: emit a placeholder constant and surface a diagnostic).
    /// </summary>
    public static IEEEPrimitiveResult? TryLower(string name, IEEEPrimitiveArgs args)
    {
        var p = Lookup(name);
        return p is null ? null : p.Lower(args);
    }

    /// <summary>Snapshot of every registered name (canonical + aliases). Useful for tests.</summary>
    public static IReadOnlyCollection<string> AllRegisteredNames()
    {
        lock (_lock) return _byName.Keys.ToArray();
    }

    /// <summary>Drops every registration. For tests that re-exercise the seeding path.</summary>
    public static void ClearForTests()
    {
        lock (_lock) _byName.Clear();
    }

    // ---- Emitters ----------------------------------------------------------
    //
    // Each emitter follows the same shape: validate arg count, allocate a
    // result net of the right width, emit one or more cells, return the net.
    //
    // The emitters are tagged with `internal` so consumers go through the
    // table rather than constructing emitters directly. The table is the
    // canonical entry point.

    /// <summary>Lowers <c>rising_edge(clk)</c> to a single <see cref="CellKind.EDGE_DETECT"/> cell with <c>Edge=rising</c>.</summary>
    internal sealed class RisingEdgePrimitive : IIEEEPrimitive
    {
        public string CanonicalName => "rising_edge";
        public IReadOnlyList<string> Aliases => Array.Empty<string>();
        public CellKind PrimaryCellKind => CellKind.EDGE_DETECT;

        public IEEEPrimitiveResult Lower(IEEEPrimitiveArgs a)
        {
            if (a.Args.Count == 0)
                return new IEEEPrimitiveResult(a.Ctx.Constant("@rising_edge_empty", 1), 0);

            var clk = a.Args[0];
            var outNet = a.Ctx.NewInternal(width: 1, sourceLine: a.SourceLine);
            a.Ctx.AddCell(
                kind:    CellKind.EDGE_DETECT,
                inputs:  new[] { new Pin("CLK", clk.Id) },
                outputs: new[] { new Pin("Y",   outNet.Id) },
                sourceLine: a.SourceLine,
                attrs:   new Dictionary<string, string> { ["Edge"] = "rising" });
            return new IEEEPrimitiveResult(outNet, 1);
        }
    }

    /// <summary>Lowers <c>falling_edge(clk)</c> to a single <see cref="CellKind.EDGE_DETECT"/> cell with <c>Edge=falling</c>.</summary>
    internal sealed class FallingEdgePrimitive : IIEEEPrimitive
    {
        public string CanonicalName => "falling_edge";
        public IReadOnlyList<string> Aliases => Array.Empty<string>();
        public CellKind PrimaryCellKind => CellKind.EDGE_DETECT;

        public IEEEPrimitiveResult Lower(IEEEPrimitiveArgs a)
        {
            if (a.Args.Count == 0)
                return new IEEEPrimitiveResult(a.Ctx.Constant("@falling_edge_empty", 1), 0);

            var clk = a.Args[0];
            var outNet = a.Ctx.NewInternal(width: 1, sourceLine: a.SourceLine);
            a.Ctx.AddCell(
                kind:    CellKind.EDGE_DETECT,
                inputs:  new[] { new Pin("CLK", clk.Id) },
                outputs: new[] { new Pin("Y",   outNet.Id) },
                sourceLine: a.SourceLine,
                attrs:   new Dictionary<string, string> { ["Edge"] = "falling" });
            return new IEEEPrimitiveResult(outNet, 1);
        }
    }

    /// <summary>
    /// Lowers <c>to_integer(unsigned)</c> / <c>to_integer(signed)</c> /
    /// <c>conv_integer</c> to a bit-weighted-sum reduction cell. The output
    /// width is fixed at 32 bits — the standard <c>integer</c> width.
    /// </summary>
    internal sealed class ToIntegerPrimitive : IIEEEPrimitive
    {
        public string CanonicalName => "to_integer";
        public IReadOnlyList<string> Aliases => new[] { "conv_integer" };
        public CellKind PrimaryCellKind => CellKind.TO_INTEGER;

        public IEEEPrimitiveResult Lower(IEEEPrimitiveArgs a)
        {
            if (a.Args.Count == 0)
                return new IEEEPrimitiveResult(a.Ctx.Constant("@to_integer_empty", 32), 0);

            var v = a.Args[0];
            var outNet = a.Ctx.NewInternal(width: 32, sourceLine: a.SourceLine);
            a.Ctx.AddCell(
                kind:    CellKind.TO_INTEGER,
                inputs:  new[] { new Pin("A", v.Id) },
                outputs: new[] { new Pin("Y", outNet.Id) },
                sourceLine: a.SourceLine,
                attrs:   new Dictionary<string, string>
                {
                    ["Width"]  = v.Width.ToString(),
                    ["Signed"] = "false"  // unsigned by default; wrapper sets "true" for signed
                });
            return new IEEEPrimitiveResult(outNet, 1);
        }
    }

    /// <summary><c>to_unsigned(int, width)</c> → bus of declared width.</summary>
    internal sealed class ToUnsignedPrimitive : IIEEEPrimitive
    {
        public string CanonicalName => "to_unsigned";
        public IReadOnlyList<string> Aliases => Array.Empty<string>();
        public CellKind PrimaryCellKind => CellKind.TO_VECTOR;
        public IEEEPrimitiveResult Lower(IEEEPrimitiveArgs a) => LowerToVector(a, signed: false);
    }

    /// <summary><c>to_signed(int, width)</c> → bus of declared width with sign extension.</summary>
    internal sealed class ToSignedPrimitive : IIEEEPrimitive
    {
        public string CanonicalName => "to_signed";
        public IReadOnlyList<string> Aliases => new[] { "conv_std_logic_vector" };
        public CellKind PrimaryCellKind => CellKind.TO_VECTOR;
        public IEEEPrimitiveResult Lower(IEEEPrimitiveArgs a) => LowerToVector(a, signed: true);
    }

    /// <summary>
    /// Shared lowering for <see cref="ToUnsignedPrimitive"/> / <see cref="ToSignedPrimitive"/>:
    /// emits a single <see cref="CellKind.TO_VECTOR"/> cell with the requested width and the
    /// <c>Signed</c> attribute set per <paramref name="signed"/>.
    /// </summary>
    /// <param name="a">Primitive argument bundle (context + arg nets + source line).</param>
    /// <param name="signed">When <c>true</c>, marks the cell as sign-extending.</param>
    private static IEEEPrimitiveResult LowerToVector(IEEEPrimitiveArgs a, bool signed)
    {
        if (a.Args.Count < 2)
            return new IEEEPrimitiveResult(a.Ctx.Constant("@to_vector_arity", 1), 0);

        var v        = a.Args[0];           // integer source
        var widthNet = a.Args[1];           // width literal (carried via Width attr)
        int width    = Math.Max(1, widthNet.Width);  // best-effort
        var outNet   = a.Ctx.NewInternal(width: width, sourceLine: a.SourceLine);
        a.Ctx.AddCell(
            kind:    CellKind.TO_VECTOR,
            inputs:  new[] { new Pin("A", v.Id), new Pin("W", widthNet.Id) },
            outputs: new[] { new Pin("Y", outNet.Id) },
            sourceLine: a.SourceLine,
            attrs:   new Dictionary<string, string>
            {
                ["Signed"] = signed ? "true" : "false",
                ["Width"]  = width.ToString()
            });
        return new IEEEPrimitiveResult(outNet, 1);
    }

    /// <summary><c>resize(v, n)</c> → zero/sign-extend or truncate.</summary>
    internal sealed class ResizePrimitive : IIEEEPrimitive
    {
        public string CanonicalName => "resize";
        public IReadOnlyList<string> Aliases => Array.Empty<string>();
        public CellKind PrimaryCellKind => CellKind.RESIZE;

        public IEEEPrimitiveResult Lower(IEEEPrimitiveArgs a)
        {
            if (a.Args.Count < 2)
                return new IEEEPrimitiveResult(a.Ctx.Constant("@resize_arity", 1), 0);

            var v        = a.Args[0];
            var widthNet = a.Args[1];
            int newWidth = Math.Max(1, widthNet.Width);
            var outNet   = a.Ctx.NewInternal(width: newWidth, sourceLine: a.SourceLine);
            a.Ctx.AddCell(
                kind:    CellKind.RESIZE,
                inputs:  new[] { new Pin("A", v.Id), new Pin("N", widthNet.Id) },
                outputs: new[] { new Pin("Y", outNet.Id) },
                sourceLine: a.SourceLine,
                attrs:   new Dictionary<string, string>
                {
                    ["FromWidth"] = v.Width.ToString(),
                    ["ToWidth"]   = newWidth.ToString()
                });
            return new IEEEPrimitiveResult(outNet, 1);
        }
    }

    /// <summary><c>shift_left / shift_right (v, n)</c> → barrel shifter cell.</summary>
    internal sealed class ShiftLeftPrimitive : IIEEEPrimitive
    {
        public string CanonicalName => "shift_left";
        public IReadOnlyList<string> Aliases => Array.Empty<string>();
        public CellKind PrimaryCellKind => CellKind.SHL;
        public IEEEPrimitiveResult Lower(IEEEPrimitiveArgs a) => LowerShift(a, CellKind.SHL);
    }

    /// <summary>Lowers <c>shift_right(v, n)</c> to a barrel-shifter <see cref="CellKind.SHR"/> cell.</summary>
    internal sealed class ShiftRightPrimitive : IIEEEPrimitive
    {
        public string CanonicalName => "shift_right";
        public IReadOnlyList<string> Aliases => Array.Empty<string>();
        public CellKind PrimaryCellKind => CellKind.SHR;
        public IEEEPrimitiveResult Lower(IEEEPrimitiveArgs a) => LowerShift(a, CellKind.SHR);
    }

    /// <summary>Lowers <c>rotate_left(v, n)</c> to a <see cref="CellKind.ROL"/> cell.</summary>
    internal sealed class RotateLeftPrimitive : IIEEEPrimitive
    {
        public string CanonicalName => "rotate_left";
        public IReadOnlyList<string> Aliases => Array.Empty<string>();
        public CellKind PrimaryCellKind => CellKind.ROL;
        public IEEEPrimitiveResult Lower(IEEEPrimitiveArgs a) => LowerShift(a, CellKind.ROL);
    }

    /// <summary>Lowers <c>rotate_right(v, n)</c> to a <see cref="CellKind.ROR"/> cell.</summary>
    internal sealed class RotateRightPrimitive : IIEEEPrimitive
    {
        public string CanonicalName => "rotate_right";
        public IReadOnlyList<string> Aliases => Array.Empty<string>();
        public CellKind PrimaryCellKind => CellKind.ROR;
        public IEEEPrimitiveResult Lower(IEEEPrimitiveArgs a) => LowerShift(a, CellKind.ROR);
    }

    /// <summary>
    /// Shared lowering for the four shift/rotate primitives: emits a single cell of
    /// <paramref name="kind"/> with inputs <c>A</c> (value) and <c>N</c> (shift amount).
    /// </summary>
    /// <param name="a">Primitive argument bundle.</param>
    /// <param name="kind">Cell kind to emit (<see cref="CellKind.SHL"/>, <see cref="CellKind.SHR"/>, <see cref="CellKind.ROL"/> or <see cref="CellKind.ROR"/>).</param>
    private static IEEEPrimitiveResult LowerShift(IEEEPrimitiveArgs a, CellKind kind)
    {
        if (a.Args.Count < 2)
            return new IEEEPrimitiveResult(a.Ctx.Constant("@shift_arity", 1), 0);

        var v   = a.Args[0];
        var n   = a.Args[1];
        var outNet = a.Ctx.NewInternal(width: v.Width, sourceLine: a.SourceLine);
        a.Ctx.AddCell(
            kind:    kind,
            inputs:  new[] { new Pin("A", v.Id), new Pin("N", n.Id) },
            outputs: new[] { new Pin("Y", outNet.Id) },
            sourceLine: a.SourceLine);
        return new IEEEPrimitiveResult(outNet, 1);
    }

    /// <summary>
    /// Type-casts (<c>unsigned(stdvec)</c>, <c>signed(stdvec)</c>,
    /// <c>std_logic_vector(unsigned)</c>) lower to a single BUF cell — the
    /// bit pattern is unchanged, only the type interpretation differs.
    /// </summary>
    internal sealed class TypeCastPrimitive : IIEEEPrimitive
    {
        public string CanonicalName { get; }
        public IReadOnlyList<string> Aliases => Array.Empty<string>();
        public CellKind PrimaryCellKind => CellKind.BUF;

        public TypeCastPrimitive(string canonicalName) { CanonicalName = canonicalName; }

        public IEEEPrimitiveResult Lower(IEEEPrimitiveArgs a)
        {
            if (a.Args.Count == 0)
                return new IEEEPrimitiveResult(a.Ctx.Constant($"@{CanonicalName}_empty", 1), 0);

            var v = a.Args[0];
            var outNet = a.Ctx.NewInternal(width: v.Width, sourceLine: a.SourceLine);
            a.Ctx.AddCell(
                kind:    CellKind.BUF,
                inputs:  new[] { new Pin("A", v.Id) },
                outputs: new[] { new Pin("Y", outNet.Id) },
                sourceLine: a.SourceLine,
                attrs:   new Dictionary<string, string> { ["Cast"] = CanonicalName });
            return new IEEEPrimitiveResult(outNet, 1);
        }
    }
}
