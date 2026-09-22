using Interpreter.Synthesis.Layout;

namespace Interpreter.Synthesis;

/// <summary>
/// Caller-supplied picks driving <see cref="SelectionFilter"/>. Mirrors
/// the SchematicViewer's basket fields plus the Naive focus state. All
/// hash sets are case-sensitive (cell ids and net ids are synthesizer-
/// generated so case is stable).
/// </summary>
public sealed class SelectionPicks
{
    /// <summary>Cell ids the user has explicitly basketed.</summary>
    public HashSet<string> CellIds { get; init; } = new();

    /// <summary>Internal net ids basketed for export.</summary>
    public HashSet<string> NetIds { get; init; } = new();

    /// <summary>Entity-port net ids basketed for export.</summary>
    public HashSet<string> PortNetIds { get; init; } = new();

    /// <summary>
    /// VHDL source-line key when the user has pinned a Naive block;
    /// <c>null</c> otherwise. Used to scope selections to a single source line.
    /// </summary>
    public string? FocusedSourceLine { get; init; }
}

/// <summary>
/// Result of filtering a netlist + layout for export. Same coordinate
/// space as the source layout — the renderer can either honour the
/// canvas size as-is, or trim to the bounding box of the visible
/// elements via <see cref="Bounds"/>.
/// </summary>
public sealed record FilteredLayout(
    LaidOutNetlist Layout,
    HashSet<string> VisibleCellIds,
    HashSet<string> VisibleNetIds,
    HashSet<string> VisiblePortNetIds,
    Rect Bounds
);

/// <summary>
/// Pure function that turns a "show me X" request into the concrete
/// set of cell, net, and port-net ids the exporter should render. The
/// schematic UI component is the only place that holds the user's raw
/// picks; this class is the seam between picks-and-intent and the
/// exporter's flat visibility-set model.
/// </summary>
public static class SelectionFilter
{
    /// <summary>
    /// Builds the visible set for a "Full diagram" export — every
    /// element in the netlist passes through.
    /// </summary>
    public static FilteredLayout Full(LaidOutNetlist layout)
    {
        var cells = new HashSet<string>(layout.Netlist.Cells.Select(c => c.Id));
        var nets = new HashSet<string>(layout.Netlist.Nets.Select(n => n.Id));
        var ports = new HashSet<string>(layout.Netlist.Ports.Select(p => p.NetId));
        return new FilteredLayout(layout, cells, nets, ports, ComputeBounds(layout, cells, nets, ports));
    }

    /// <summary>
    /// Cells of one specific source-line group ("Naive block"). Picks
    /// every cell with the matching <c>SourceLine</c> plus the wires
    /// touching them and the entity ports those wires reach.
    /// </summary>
    public static FilteredLayout NaiveBlock(LaidOutNetlist layout, string sourceLine)
    {
        var cells = new HashSet<string>(
            layout.Netlist.Cells.Where(c => c.SourceLine == sourceLine).Select(c => c.Id));
        return ExpandFromCells(layout, cells, autoIncludeEndpoints: true);
    }

    /// <summary>
    /// Cells of one <see cref="CellKind"/> — "show me every AND gate"
    /// for cross-comparing usage patterns.
    /// </summary>
    public static FilteredLayout ByKind(LaidOutNetlist layout, CellKind kind)
    {
        var cells = new HashSet<string>(
            layout.Netlist.Cells.Where(c => c.Kind == kind).Select(c => c.Id));
        return ExpandFromCells(layout, cells, autoIncludeEndpoints: true);
    }

    /// <summary>
    /// Every entity input port + the gates those inputs feed into,
    /// one hop out. Useful as a "what does the input fan-out look
    /// like" view.
    /// </summary>
    public static FilteredLayout Inputs(LaidOutNetlist layout)
    {
        var portNets = new HashSet<string>(
            layout.Netlist.Ports.Where(p => p.Direction is PortDir.In or PortDir.InOut)
                .Select(p => p.NetId));
        return ExpandFromPortNets(layout, portNets, autoIncludeEndpoints: true);
    }

    /// <summary>
    /// Every entity output port + the last gate driving each.
    /// </summary>
    public static FilteredLayout Outputs(LaidOutNetlist layout)
    {
        var portNets = new HashSet<string>(
            layout.Netlist.Ports.Where(p => p.Direction is PortDir.Out or PortDir.Buffer)
                .Select(p => p.NetId));
        return ExpandFromPortNets(layout, portNets, autoIncludeEndpoints: true);
    }

    /// <summary>
    /// Internal nets — signals declared in the architecture and
    /// auto-named intermediate nets — plus the cells that drive or
    /// consume them. Excludes entity ports.
    /// </summary>
    public static FilteredLayout MidTerms(LaidOutNetlist layout)
    {
        var nets = new HashSet<string>(
            layout.Netlist.Nets
                .Where(n => n.Origin is NetOrigin.Signal or NetOrigin.Internal)
                .Select(n => n.Id));
        return ExpandFromNets(layout, nets, autoIncludeEndpoints: true);
    }

    /// <summary>
    /// User-driven picks. Cells, nets, and port-nets are all unioned
    /// and (optionally) auto-extended one hop so a single-cell pick
    /// doesn't render a gate floating in white space.
    /// </summary>
    public static FilteredLayout FromPicks(LaidOutNetlist layout, SelectionPicks picks, bool autoIncludeEndpoints)
    {
        var cells = new HashSet<string>(picks.CellIds);
        var nets = new HashSet<string>(picks.NetIds);
        var portNets = new HashSet<string>(picks.PortNetIds);

        if (autoIncludeEndpoints)
        {
            // Wire → its producer cell + every consumer cell.
            foreach (var netId in picks.NetIds.Concat(picks.PortNetIds))
            {
                foreach (var c in layout.Netlist.Cells)
                {
                    if (c.Outputs.Any(p => p.NetId == netId)) cells.Add(c.Id);
                    if (c.Inputs.Any(p => p.NetId == netId)) cells.Add(c.Id);
                }
            }
            // Cell → its connected nets (so the pin doesn't dangle).
            foreach (var cellId in picks.CellIds)
            {
                var cell = layout.Netlist.Cells.FirstOrDefault(x => x.Id == cellId);
                if (cell == null) continue;
                foreach (var p in cell.Inputs) nets.Add(p.NetId);
                foreach (var p in cell.Outputs) nets.Add(p.NetId);
            }
        }

        // Always include the port-net list as a regular net id too
        // (a port net is a net like any other from the renderer's
        // point of view — the port-net hash set is just the user's
        // intent marker).
        foreach (var n in portNets) nets.Add(n);

        return ExpandFromCellsAndNets(layout, cells, nets, autoIncludeEndpoints);
    }

    // ─── Helpers ─────────────────────────────────────────────────────

    private static FilteredLayout ExpandFromCells(
        LaidOutNetlist layout, HashSet<string> cells, bool autoIncludeEndpoints)
    {
        var nets = new HashSet<string>();
        foreach (var c in layout.Netlist.Cells)
        {
            if (!cells.Contains(c.Id)) continue;
            foreach (var p in c.Inputs) nets.Add(p.NetId);
            foreach (var p in c.Outputs) nets.Add(p.NetId);
        }
        return ExpandFromCellsAndNets(layout, cells, nets, autoIncludeEndpoints);
    }

    private static FilteredLayout ExpandFromNets(
        LaidOutNetlist layout, HashSet<string> nets, bool autoIncludeEndpoints)
    {
        var cells = new HashSet<string>();
        foreach (var c in layout.Netlist.Cells)
        {
            if (c.Inputs.Any(p => nets.Contains(p.NetId)) ||
                c.Outputs.Any(p => nets.Contains(p.NetId)))
            {
                cells.Add(c.Id);
            }
        }
        return ExpandFromCellsAndNets(layout, cells, nets, autoIncludeEndpoints);
    }

    private static FilteredLayout ExpandFromPortNets(
        LaidOutNetlist layout, HashSet<string> portNets, bool autoIncludeEndpoints)
    {
        return ExpandFromNets(layout, new HashSet<string>(portNets), autoIncludeEndpoints);
    }

    private static FilteredLayout ExpandFromCellsAndNets(
        LaidOutNetlist layout, HashSet<string> cells, HashSet<string> nets, bool autoIncludeEndpoints)
    {
        if (autoIncludeEndpoints)
        {
            // Pull each cell's pins into the net set so wires
            // terminating at a visible cell are themselves visible.
            foreach (var c in layout.Netlist.Cells)
            {
                if (!cells.Contains(c.Id)) continue;
                foreach (var p in c.Inputs) nets.Add(p.NetId);
                foreach (var p in c.Outputs) nets.Add(p.NetId);
            }
        }

        // Visible ports = entity ports whose net is in the visible
        // net set. Computed last because the net set may have just
        // grown from cell-pin closure.
        var ports = new HashSet<string>(
            layout.Netlist.Ports.Where(p => nets.Contains(p.NetId)).Select(p => p.NetId));

        return new FilteredLayout(layout, cells, nets, ports, ComputeBounds(layout, cells, nets, ports));
    }

    private static Rect ComputeBounds(
        LaidOutNetlist layout, HashSet<string> cells, HashSet<string> nets, HashSet<string> ports)
    {
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        bool any = false;

        foreach (var (id, rect) in layout.CellBounds)
        {
            if (!cells.Contains(id)) continue;
            any = true;
            minX = Math.Min(minX, rect.X);
            minY = Math.Min(minY, rect.Y);
            maxX = Math.Max(maxX, rect.X + rect.Width);
            maxY = Math.Max(maxY, rect.Y + rect.Height);
        }
        foreach (var (netId, pos) in layout.PortPositions)
        {
            if (!ports.Contains(netId)) continue;
            any = true;
            minX = Math.Min(minX, pos.Item1 - 30);
            minY = Math.Min(minY, pos.Item2 - 12);
            maxX = Math.Max(maxX, pos.Item1 + 30);
            maxY = Math.Max(maxY, pos.Item2 + 12);
        }
        foreach (var wire in layout.Wires)
        {
            if (!nets.Contains(wire.NetId)) continue;
            any = true;
            foreach (var (x, y) in wire.Points)
            {
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }

        if (!any) return new Rect(0, 0, layout.CanvasWidth, layout.CanvasHeight);
        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }
}
