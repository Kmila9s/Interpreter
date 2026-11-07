using Interpreter.Services;
using Parser.Models;
using static Parser.Models.Constants;

namespace Interpreter.Synthesis;

/// <summary>
/// Emits a <see cref="CellKind.BLACKBOX_COMPONENT"/> cell for every
/// component instantiation the parser stored in
/// <see cref="EntityRegistry"/> for the entity being synthesised.
///
/// The pass is deliberately shallow — it does not walk the referenced
/// component's own architecture. The referenced entity's internals are
/// covered by simulation via <c>SourceElaborator</c>; the schematic view
/// only needs to make the hierarchy visible so students see how the
/// top-level module wires its sub-components together.
///
/// When the referenced entity is known to the registry
/// (<see cref="ComponentInstance.ResolvedEntity"/> non-null), formal-pin
/// direction is honoured — IN pins land on the left edge, OUT/BUFFER on
/// the right, and INOUT is treated as an input for the schematic. When
/// direction is unknown, every formal is emitted as an input pin so at
/// least the wiring shows up.
/// </summary>
internal static class ComponentSynthesizer
{
    /// <summary>
    /// Emit one BLACKBOX_COMPONENT cell per instantiation registered
    /// against <paramref name="parentEntityName"/> in
    /// <see cref="EntityRegistry.Instance"/>. Ports referenced in port
    /// maps that aren't already nets in the context are created on the
    /// fly (as internal signals) so the resulting cell always has
    /// non-dangling pin nets.
    /// </summary>
    public static void SynthesizeAll(string parentEntityName, SynthesisContext ctx)
    {
        var instances = EntityRegistry.Instance.GetComponentInstances(parentEntityName);
        foreach (var ci in instances)
        {
            SynthesizeOne(ci, ctx);
        }
    }

    private static void SynthesizeOne(ComponentInstance ci, SynthesisContext ctx)
    {
        // Resolve each port-map binding to a Pin. If the referenced
        // component was itself parsed (ResolvedEntity is set), use its
        // formal ports to determine direction and width; otherwise fall
        // back to single-bit wire pins with no direction info.
        var inputs = new List<Pin>();
        var outputs = new List<Pin>();

        // Resolve the referenced entity so we can honour its port directions.
        // ResolvedEntity is wired at registration time, which can run before the
        // child entity has been scanned (and the name may carry a "work."
        // library prefix), so it is often null here. Re-resolve against the now-
        // fully-populated registry, trying the bare name after stripping any
        // library prefix — without this, every pin falls back to "input" and the
        // instance has no driver, so the hierarchy renders with no wires.
        var resolved = ci.ResolvedEntity
            ?? EntityRegistry.Instance.GetEntity(ci.EntityName)
            ?? EntityRegistry.Instance.GetEntity(StripLibrary(ci.EntityName));

        var directionByFormal = new Dictionary<string, PortType>(StringComparer.OrdinalIgnoreCase);
        var widthByFormal = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (resolved != null)
        {
            foreach (var p in resolved.Ports)
            {
                directionByFormal[p.Name] = p.PortType;
                widthByFormal[p.Name] = p.Type == DATATYPES.STD_LOGIC_VECTOR ? Math.Max(1, p.Size) : 1;
            }
        }

        foreach (var (formal, actual) in ci.PortMap)
        {
            // Skip `open` associations — the port is intentionally left
            // dangling; drawing a stub pin adds visual noise.
            if (string.Equals(actual, "open", StringComparison.OrdinalIgnoreCase))
                continue;

            int width = widthByFormal.TryGetValue(formal, out var w) ? w : 1;
            var actualNet = ResolveActualNet(ctx, actual, width);

            var pin = new Pin(formal, actualNet.Id);

            // Direction: OUT/BUFFER → right edge; everything else (IN,
            // INOUT, unknown) → left edge. Keeping INOUT on the left
            // gives the diagram a bias toward "reads flow in", which is
            // the common case in student designs.
            var dir = directionByFormal.TryGetValue(formal, out var d) ? d : PortType.IN;
            if (dir == PortType.OUT || dir == PortType.BUFFER)
                outputs.Add(pin);
            else
                inputs.Add(pin);
        }

        // Nothing bound? Emit a labeled box with no pins so the student
        // still sees "this component exists" — better than dropping it.
        var attrs = new Dictionary<string, string>
        {
            ["Op"] = StripLibrary(ci.EntityName), // shown as the glyph label (no "work." prefix)
            ["Instance"] = ci.InstanceName,       // instance label (u_cpu, etc.)
            ["Component"] = ci.EntityName,
        };

        ctx.AddCell(
            kind: CellKind.BLACKBOX_COMPONENT,
            inputs: inputs,
            outputs: outputs,
            sourceLine: $"component: {ci.InstanceName} : {ci.EntityName}",
            attrs: attrs);
    }

    /// <summary>Resolves the net a port-map actual binds to. When the actual is a
    /// bit or slice of a declared bus <em>port</em> (e.g. <c>a(0)</c> of input port
    /// <c>a</c>, or <c>sum(0)</c> of output port <c>sum</c>), it ties the pin to
    /// that existing port net so the schematic draws a wire between the port and the
    /// instance — a standalone <c>a(0)</c>/<c>sum(0)</c> net would have no driver (or
    /// no sink) and render unconnected, leaving the whole bus port floating. This
    /// covers inputs (port drives the slice), outputs (the slices drive the port —
    /// the four full-adder <c>sum</c> bits converge on <c>sum[3:0]</c>) and
    /// bidirectional ports alike. Carry-chain and other internal-signal bit-slices
    /// are left as their own named nets (so instance-to-instance connections that
    /// share a bit name still join, and the carry nets stay distinct).</summary>
    private static Net ResolveActualNet(SynthesisContext ctx, string actual, int width)
    {
        int paren = actual.IndexOf('(');
        if (paren > 0)
        {
            string baseName = actual[..paren].Trim();
            var baseNet = ctx.TryGetNet(baseName);
            if (baseNet != null &&
                baseNet.Origin is NetOrigin.PortIn or NetOrigin.PortInOut or NetOrigin.PortOut)
                return baseNet;

            // Internal-signal bit-select (e.g. a carry bit `c(4)`): name it with the
            // engine-wide `parent_idx_bit` convention so it is the SAME net a
            // concurrent assignment / process bit-select produces (the parser emits
            // `c_idx_4`, never the literal `c(4)`). Without this a carry bit that
            // crosses between a component instance and a concurrent assignment
            // (`c(0) <= cin`, `cout <= c(4)`) would land on two differently-named
            // nets and the port would render unconnected — while the pure
            // instance-to-instance carry chain, matching itself, looked fine.
            var canonical = CanonicalBitSelectName(actual);
            if (canonical != null)
                return ctx.GetOrCreateNet(
                    canonical, width > 1 ? NetKind.Bus : NetKind.Wire, width, NetOrigin.Signal);
        }
        return ctx.GetOrCreateNet(
            actual, width > 1 ? NetKind.Bus : NetKind.Wire, width, NetOrigin.Signal);
    }

    /// <summary>Rewrites a bit/slice reference like <c>c(4)</c> or
    /// <c>c(3 downto 0)</c> into the engine's canonical tap-net name
    /// (<c>c_idx_4</c> / <c>c_idx_3_0</c>) so it unifies with the same bit-select
    /// synthesised elsewhere. Returns null when the form isn't a simple numeric bit
    /// or range (the caller then keeps the literal name).</summary>
    private static string? CanonicalBitSelectName(string actual)
    {
        int lp = actual.IndexOf('(');
        int rp = actual.LastIndexOf(')');
        if (lp <= 0 || rp <= lp) return null;
        string parent = actual[..lp].Trim();
        string inner = actual[(lp + 1)..rp].Trim();
        if (parent.Length == 0 || inner.Length == 0) return null;

        // Range: "H downto L" or "L to H" → _idx_{high}_{low}.
        foreach (var kw in new[] { " downto ", " to " })
        {
            int k = inner.IndexOf(kw, StringComparison.OrdinalIgnoreCase);
            if (k < 0) continue;
            string a = inner[..k].Trim(), b = inner[(k + kw.Length)..].Trim();
            if (!int.TryParse(a, out var ai) || !int.TryParse(b, out var bi)) return null;
            int high = Math.Max(ai, bi), low = Math.Min(ai, bi);
            return $"{parent}_idx_{high}_{low}";
        }

        // Single bit: "c(4)" → c_idx_4.
        if (int.TryParse(inner, out var bit)) return $"{parent}_idx_{bit}";
        return null;
    }

    /// <summary>Strips a leading VHDL library prefix (e.g. "work.") from an
    /// entity reference so "work.full_adder" resolves and labels as "full_adder".</summary>
    private static string StripLibrary(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot >= 0 && dot < name.Length - 1 ? name[(dot + 1)..] : name;
    }
}
