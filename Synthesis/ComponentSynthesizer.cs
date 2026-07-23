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

        var directionByFormal = new Dictionary<string, PortType>(StringComparer.OrdinalIgnoreCase);
        var widthByFormal = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (ci.ResolvedEntity != null)
        {
            foreach (var p in ci.ResolvedEntity.Ports)
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
            var actualNet = ctx.GetOrCreateNet(
                actual,
                width > 1 ? NetKind.Bus : NetKind.Wire,
                width,
                NetOrigin.Signal);

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
            ["Op"] = ci.EntityName,               // shown as the glyph label
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
}
