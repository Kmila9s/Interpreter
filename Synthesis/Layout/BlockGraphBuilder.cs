namespace Interpreter.Synthesis.Layout;

/// <summary>
/// Coarsens a gate-level <see cref="Netlist"/> into a block-level one: cells
/// that belong to the same VHDL statement / process (keyed by
/// <see cref="Cell.SourceLine"/>) are merged into a single named functional
/// block (Registro, Sumador, MUX, Comparador, ROM, Lógica…). Only the nets
/// that cross a block boundary survive; nets internal to a block are dropped.
///
/// The result is an ordinary <see cref="Netlist"/> whose "cells" are the
/// blocks, so it flows through the exact same <see cref="LayeredLayout"/> and
/// <see cref="LaidOutNetlist"/> the gate styles use — the block renderers
/// (RTL, Nodos) just draw each cell as a box/card instead of a gate glyph.
/// A block cell carries its display data in <see cref="Cell.Attrs"/>:
/// <c>Block="1"</c>, <c>Label</c>, <c>Sub</c>, and <c>Members</c> (the member
/// cell ids, for selection / live-value overlay).
/// </summary>
public static class BlockGraphBuilder
{
    /// <summary>Marker attr key: present (="1") on every block cell.</summary>
    public const string BlockAttr = "Block";
    /// <summary>Attr key for a block's display title.</summary>
    public const string LabelAttr = "Label";
    /// <summary>Attr key for a block's subtitle (width / operator / count).</summary>
    public const string SubAttr = "Sub";
    /// <summary>Attr key: comma-joined member cell ids backing this block.</summary>
    public const string MembersAttr = "Members";

    /// <summary>Builds the block-level netlist from a (simplified) netlist.</summary>
    public static Netlist Build(Netlist src)
    {
        // 1. Assign each non-constant cell a block key. Cells with a source
        //    line group by statement; nameless synthetic cells get their own
        //    block so they never silently vanish.
        var members = new Dictionary<string, List<Cell>>(StringComparer.Ordinal);
        var keyOrder = new List<string>();
        foreach (var c in src.Cells)
        {
            if (c.Kind == CellKind.CONST) continue; // constants ride on wires, not blocks
            var key = c.SourceLine is { Length: > 0 } sl ? "L:" + sl : "§:" + c.Id;
            if (!members.TryGetValue(key, out var list))
            {
                members[key] = list = new();
                keyOrder.Add(key);
            }
            list.Add(c);
        }

        // 2. Producer / consumer maps over the ORIGINAL netlist.
        var producerCellOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in src.Cells)
            foreach (var p in c.Outputs) producerCellOf[p.NetId] = c.Id;

        // cellId → block key
        var blockOfCell = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, list) in members)
            foreach (var c in list) blockOfCell[c.Id] = key;

        var netById = src.Nets.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var portNetIds = new HashSet<string>(src.Ports.Select(p => p.NetId), StringComparer.Ordinal);
        var outPortNetIds = new HashSet<string>(
            src.Ports.Where(p => p.Direction is PortDir.Out or PortDir.Buffer or PortDir.InOut)
                     .Select(p => p.NetId), StringComparer.Ordinal);

        // 3. Build one block cell per group.
        var blockCells = new List<Cell>(members.Count);
        var usedNetIds = new HashSet<string>(StringComparer.Ordinal);

        int blockIndex = 0;
        foreach (var key in keyOrder)
        {
            var group = members[key];
            var memberIds = new HashSet<string>(group.Select(c => c.Id), StringComparer.Ordinal);

            // Boundary input nets: consumed by a member, produced OUTSIDE the
            // block (another block, an entity input, or a constant / no
            // producer). Deduped by net id, with the member pin that touches
            // the net remembered for a friendly port label.
            var inNets = new List<(string NetId, string Label, bool Clock)>();
            var seenIn = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in group)
                foreach (var pin in c.Inputs)
                {
                    var nid = pin.NetId;
                    bool producedInside = producerCellOf.TryGetValue(nid, out var pc) && memberIds.Contains(pc);
                    if (producedInside) continue;
                    if (!seenIn.Add(nid)) continue;
                    inNets.Add((nid, PortLabel(pin.Name, netById, nid), IsClockish(pin.Name, netById, nid)));
                }

            // Boundary output nets: produced by a member and consumed outside
            // the block OR driving an entity output port.
            var outNets = new List<(string NetId, string Label)>();
            var seenOut = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in group)
                foreach (var pin in c.Outputs)
                {
                    var nid = pin.NetId;
                    if (!seenOut.Add(nid)) continue;
                    bool crosses = outPortNetIds.Contains(nid) || ConsumedOutside(nid, src, memberIds);
                    if (!crosses) continue;
                    outNets.Add((nid, PortLabel(pin.Name, netById, nid)));
                }

            // Order inputs: data first, clock/reset last (datapath convention).
            inNets = inNets.OrderBy(n => n.Clock ? 1 : 0).ToList();

            // Make pin names unique within the block: the layout keys pin
            // positions by (cellId, pinName), so two boundary nets that resolve
            // to the same label would otherwise clobber each other's position.
            var uniq = new Dictionary<string, int>(StringComparer.Ordinal);
            string Unique(string name)
            {
                if (!uniq.TryGetValue(name, out var n)) { uniq[name] = 1; return name; }
                uniq[name] = n + 1;
                return $"{name}{n + 1}";
            }
            var inputs = inNets.Select(n => new Pin(Unique(n.Label), n.NetId)).ToList();
            var outputs = outNets.Select(n => new Pin(Unique(n.Label), n.NetId)).ToList();
            foreach (var p in inputs) usedNetIds.Add(p.NetId);
            foreach (var p in outputs) usedNetIds.Add(p.NetId);

            var (label, sub, kind) = Describe(group, netById, outNets);
            var attrs = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [BlockAttr] = "1",
                [LabelAttr] = label,
                [MembersAttr] = string.Join(",", group.Select(c => c.Id)),
            };
            if (!string.IsNullOrEmpty(sub)) attrs[SubAttr] = sub;

            // Prefix the ordinal so the id stays unique even when two distinct
            // statement keys sanitise to the same string (e.g. `q + 1` vs
            // `q - 1`) — a non-unique id duplicate-keys the viewer's lookups.
            blockCells.Add(new Cell(
                Id: $"blk_{blockIndex++}_{SanitizeId(key)}",
                Kind: kind,
                Inputs: inputs,
                Outputs: outputs,
                SourceLine: group[0].SourceLine,
                Attrs: attrs));
        }

        // 4. Keep only boundary nets (+ any entity port net, so ports anchor).
        foreach (var pid in portNetIds) usedNetIds.Add(pid);
        var keptNets = src.Nets.Where(n => usedNetIds.Contains(n.Id)).ToList();

        return new Netlist(src.EntityName, src.Ports, keptNets, blockCells, src.Diagnostics);
    }

    /// <summary>Per-block box size for the layout. Sized by port count so the
    /// biggest side fits, with a floor that keeps the title + subtitle legible.
    /// Shared by the on-screen viewer and the exporter so geometry matches.</summary>
    public static (double Width, double Height) BlockSize(Cell block)
    {
        int rows = Math.Max(1, Math.Max(block.Inputs.Count, block.Outputs.Count));
        double h = Math.Max(84, 40 + rows * 26);
        return (184, h);
    }

    private static bool ConsumedOutside(string netId, Netlist src, HashSet<string> memberIds)
    {
        foreach (var c in src.Cells)
        {
            if (memberIds.Contains(c.Id)) continue;
            foreach (var pin in c.Inputs)
                if (pin.NetId == netId) return true;
        }
        return false;
    }

    // Friendly port label: prefer a meaningful member pin name, else the net's
    // own name, else a generic tag.
    private static string PortLabel(string pinName, Dictionary<string, Net> netById, string netId)
    {
        if (!string.IsNullOrWhiteSpace(pinName) && !pinName.StartsWith("in", StringComparison.OrdinalIgnoreCase)
            && !pinName.StartsWith("out", StringComparison.OrdinalIgnoreCase))
            return pinName;
        if (netById.TryGetValue(netId, out var net) && !string.IsNullOrWhiteSpace(net.Name)
            && net.Origin is NetOrigin.PortIn or NetOrigin.PortOut or NetOrigin.Signal)
            return net.Name;
        return string.IsNullOrWhiteSpace(pinName) ? "·" : pinName;
    }

    private static bool IsClockish(string pinName, Dictionary<string, Net> netById, string netId)
    {
        bool Hit(string s) => s.Contains("clk", StringComparison.OrdinalIgnoreCase)
            || s.Contains("clock", StringComparison.OrdinalIgnoreCase)
            || s.Equals("rst", StringComparison.OrdinalIgnoreCase)
            || s.Contains("reset", StringComparison.OrdinalIgnoreCase);
        if (Hit(pinName)) return true;
        return netById.TryGetValue(netId, out var net) && Hit(net.Name);
    }

    // Title / subtitle / representative kind for a group of cells.
    private static (string Label, string Sub, CellKind Kind) Describe(
        List<Cell> group, Dictionary<string, Net> netById, List<(string NetId, string Label)> outNets)
    {
        // Pick the "most significant" member to name the block after.
        var dominant = group
            .OrderByDescending(c => Significance(c.Kind))
            .First();
        var kind = dominant.Kind;

        // Widest output bus → subtitle "N bits".
        int width = 0;
        foreach (var (nid, _) in outNets)
            if (netById.TryGetValue(nid, out var n)) width = Math.Max(width, n.Width);
        string widthSub = width > 1 ? $"{width} bits" : "";

        switch (kind)
        {
            case CellKind.DFF:
            {
                int ff = group.Count(c => c.Kind == CellKind.DFF);
                string sub = ff > 1 ? $"{ff}× · flanco" : "flanco";
                return ("Registro", sub, kind);
            }
            case CellKind.ADD: return ("Sumador", "A + B", kind);
            case CellKind.SUB: return ("Restador", "A − B", kind);
            case CellKind.MUL: return ("Multiplic.", "A × B", kind);
            case CellKind.DIV: return ("División", "A ÷ B", kind);
            case CellKind.MOD: return ("Módulo", "A mod B", kind);
            case CellKind.POW: return ("Potencia", "A ** B", kind);
            case CellKind.SHL: return ("Desplaz. ≪", widthSub, kind);
            case CellKind.SHR: return ("Desplaz. ≫", widthSub, kind);
            case CellKind.ROL: return ("Rotar ←", widthSub, kind);
            case CellKind.ROR: return ("Rotar →", widthSub, kind);
            case CellKind.MUX: return ("MUX", "selector", kind);
            case CellKind.EQ: return ("Comparador", "A = B", kind);
            case CellKind.NEQ: return ("Comparador", "A ≠ B", kind);
            case CellKind.LT: return ("Comparador", "A < B", kind);
            case CellKind.LTE: return ("Comparador", "A ≤ B", kind);
            case CellKind.GT: return ("Comparador", "A > B", kind);
            case CellKind.GTE: return ("Comparador", "A ≥ B", kind);
            case CellKind.ROM_LOOKUP: return ("ROM", "tabla", kind);
            case CellKind.RAM_READ: return ("RAM", "lectura", kind);
            case CellKind.RAM_WRITE: return ("RAM", "escritura", kind);
            case CellKind.TO_INTEGER: return ("A entero", widthSub, kind);
            case CellKind.TO_VECTOR: return ("A vector", widthSub, kind);
            case CellKind.RESIZE: return ("Redim.", widthSub, kind);
            case CellKind.EDGE_DETECT: return ("Flanco", "detector", kind);
            case CellKind.LAST_VALUE: return ("'last", "valor previo", kind);
            case CellKind.STABLE: return ("'stable", "estable", kind);
            case CellKind.BLACKBOX_PROCESS: return ("Proceso", "lógica", kind);
            case CellKind.BLACKBOX_COMPONENT:
            {
                var name = dominant.Attrs != null && dominant.Attrs.TryGetValue("Op", out var op) && !string.IsNullOrEmpty(op)
                    ? op : "componente";
                return (name, "módulo", kind);
            }
            default:
            {
                // Pure combinational glue. Name it after how many gates it folds.
                int gates = group.Count;
                string sub = gates > 1 ? $"{gates} compuertas" : widthSub;
                return ("Lógica", sub, kind);
            }
        }
    }

    // Ranking so Describe() names a block after its defining operator rather
    // than an incidental buffer/gate sharing the same source line.
    private static int Significance(CellKind k) => k switch
    {
        CellKind.BLACKBOX_COMPONENT => 100,
        CellKind.BLACKBOX_PROCESS => 95,
        CellKind.ROM_LOOKUP or CellKind.RAM_READ or CellKind.RAM_WRITE => 90,
        CellKind.DFF => 85,
        CellKind.ADD or CellKind.SUB or CellKind.MUL or CellKind.DIV
            or CellKind.MOD or CellKind.POW => 80,
        CellKind.EQ or CellKind.NEQ or CellKind.LT or CellKind.LTE
            or CellKind.GT or CellKind.GTE => 75,
        CellKind.SHL or CellKind.SHR or CellKind.ROL or CellKind.ROR => 70,
        CellKind.MUX => 65,
        CellKind.TO_INTEGER or CellKind.TO_VECTOR or CellKind.RESIZE
            or CellKind.EDGE_DETECT or CellKind.LAST_VALUE or CellKind.STABLE => 50,
        CellKind.AND or CellKind.OR or CellKind.XOR or CellKind.NAND
            or CellKind.NOR or CellKind.XNOR or CellKind.NOT => 10,
        CellKind.BUF => 5,
        _ => 1,
    };

    private static string SanitizeId(string key)
    {
        var chars = key.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray();
        return new string(chars);
    }
}
