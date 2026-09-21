namespace Interpreter.Synthesis.Layout;

/// <summary>
/// Bounding rectangle for a cell glyph in layout coordinates (logical
/// pixels at 1:1 zoom — the renderer scales from there).
/// </summary>
public readonly record struct Rect(double X, double Y, double Width, double Height);

/// <summary>
/// Polyline path for a routed wire. Consumed by the renderer as a
/// whitespace-separated list of <c>L</c>-style points in SVG. The
/// per-endpoint cell ids let the renderer hide individual branches
/// when their target is hidden — needed for Naive-mode isolation,
/// where a fan-out from an input port has some branches going to the
/// focused group and others going to hidden groups; without these
/// IDs the renderer can only hide the whole net or none of it.
/// </summary>
public sealed record WirePath(
    string NetId,
    IReadOnlyList<(double X, double Y)> Points,
    string? FromCellId,
    string? ToCellId
);

/// <summary>
/// Result of laying out a netlist: every cell has a placed
/// <see cref="Rect"/>, every input/output port has an absolute pin
/// position, and every routed net has a polyline. The layout is
/// deterministic — same input, same output — so snapshot tests can
/// hash the result.
/// </summary>
public sealed record LaidOutNetlist(
    Netlist Netlist,
    IReadOnlyDictionary<string, Rect> CellBounds,
    IReadOnlyDictionary<(string CellId, string PinName), (double X, double Y)> PinPositions,
    IReadOnlyDictionary<string, (double X, double Y)> PortPositions,
    IReadOnlyList<WirePath> Wires,
    double CanvasWidth,
    double CanvasHeight
);

/// <summary>
/// Layered DAG layout: assigns each cell a column based on its longest
/// path from any input port (Sugiyama's first phase), orders cells
/// within a column with one barycenter sweep to reduce wire crossings,
/// then routes each net as a Manhattan polyline. Hand-rolled in C# so
/// there's no JS dependency.
/// </summary>
/// <remarks>
/// Spacing was tuned for readability over compactness — students need
/// labels to stay legible at 1:1, and the parent dock pane scrolls,
/// so erring on the side of "use more space" is the right call. The
/// canvas grows to its natural size; the renderer scales by zoom and
/// the container handles overflow.
/// </remarks>
public static class LayeredLayout
{
    // Glyph dimensions match GateGlyphs.cs (70×50). Don't change these
    // without updating the SVG path coordinates over there.
    private const double GateW = 70;
    private const double GateH = 50;
    // Generous gaps so wire trunks have room to fan out and labels
    // don't crowd neighbouring cells. Trunk staggering inside a gap
    // uses up to TrunkStaggerLanes lanes worth of horizontal space.
    private const double ColGap = 170;
    private const double RowGap = 80;
    // Side strips host the entity's input ports (left) and output
    // ports (right). Wide enough to fit long port names ([n] suffix
    // included) at default zoom.
    private const double PortStripW = 130;
    // Padding between the port strip and the first/last cell column.
    private const double EdgePad = 60;
    private const double TopPad = 60;
    private const double BotPad = 60;
    // How many parallel "lanes" we slice each inter-column gap into
    // when staggering wire trunks. 8 covers the worst case in any
    // reasonable teaching example before the gap visibly fills up.
    private const int TrunkStaggerLanes = 8;
    // Vertical gap between swimlane bands (one band per VHDL statement /
    // process, keyed by Cell.SourceLine) in group-aware layout. Wide
    // enough that adjacent group tint boxes never touch, so their labels
    // stay legible.
    private const double BandGap = 70;

    /// <param name="groupAware">
    /// When true (default) cells sharing a <c>Cell.SourceLine</c> are kept
    /// in one contiguous horizontal band so the viewer's translucent group
    /// tints stay compact and non-overlapping. When false, falls back to
    /// the legacy per-column-centered placement.
    /// </param>
    public static LaidOutNetlist Layout(Netlist netlist, bool groupAware = true)
    {
        // 1. Build cell-to-cell adjacency from net producer/consumer maps.
        var producerOf = new Dictionary<string, string>(StringComparer.Ordinal); // netId → cellId producing it
        foreach (var c in netlist.Cells)
            foreach (var pin in c.Outputs)
                producerOf[pin.NetId] = c.Id;

        var consumersOf = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var c in netlist.Cells)
            foreach (var pin in c.Inputs)
            {
                if (!consumersOf.TryGetValue(pin.NetId, out var list))
                    consumersOf[pin.NetId] = list = new();
                list.Add(c.Id);
            }

        // 2. Rank cells via longest path from input ports / nets without producers.
        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        var byCellId = netlist.Cells.ToDictionary(c => c.Id);
        int Rank(string cellId)
        {
            if (rank.TryGetValue(cellId, out var r)) return r;
            rank[cellId] = 0; // placeholder to break cycles
            int best = 0;
            var cell = byCellId[cellId];
            foreach (var pin in cell.Inputs)
            {
                if (producerOf.TryGetValue(pin.NetId, out var upstream))
                    best = Math.Max(best, Rank(upstream) + 1);
            }
            rank[cellId] = best;
            return best;
        }
        foreach (var c in netlist.Cells) Rank(c.Id);

        int maxRank = rank.Count == 0 ? 0 : rank.Values.Max();

        // 3. Group cells by rank, then run a single barycenter sweep using
        //    upstream column positions. This is the cheapest crossing-
        //    reduction pass that still produces visibly tidy diagrams.
        var columns = new List<List<string>>(maxRank + 1);
        for (int i = 0; i <= maxRank; i++) columns.Add(new List<string>());
        foreach (var (cellId, r) in rank) columns[r].Add(cellId);

        var rowIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int col = 0; col < columns.Count; col++)
        {
            if (col == 0)
            {
                // First column: declaration order so the user sees their
                // VHDL roughly top-to-bottom.
                for (int i = 0; i < columns[col].Count; i++) rowIndex[columns[col][i]] = i;
                continue;
            }
            var scored = columns[col]
                .Select(id =>
                {
                    var cell = byCellId[id];
                    double avg = 0; int n = 0;
                    foreach (var pin in cell.Inputs)
                    {
                        if (producerOf.TryGetValue(pin.NetId, out var upId)
                            && rowIndex.TryGetValue(upId, out var up))
                        {
                            avg += up; n++;
                        }
                    }
                    return (id, avg: n > 0 ? avg / n : 0);
                })
                .OrderBy(x => x.avg)
                .Select(x => x.id)
                .ToList();
            columns[col] = scored;
            for (int i = 0; i < scored.Count; i++) rowIndex[scored[i]] = i;
        }

        // 4. Compute coordinates with left-to-right port placement:
        //    [LeftPortStrip] EdgePad [col0 ColGap col1 ...] EdgePad [RightPortStrip]
        //
        //    Cells always keep their dataflow column (rank) for X. The two
        //    branches below differ only in how Y is assigned:
        //      - group-aware: one horizontal swimlane per Cell.SourceLine,
        //        stacked in source-declaration order, so the viewer's group
        //        tints are compact and never overlap.
        //      - legacy: each column centered vertically on its own.

        // Port strips need enough vertical room to space ports out so
        // their labels don't collide. Each port gets a generous slot.
        var inPorts = netlist.Ports.Where(p => p.Direction is PortDir.In or PortDir.InOut).ToList();
        var outPorts = netlist.Ports.Where(p => p.Direction is PortDir.Out or PortDir.Buffer).ToList();
        double portMinH = Math.Max(inPorts.Count, outPorts.Count) * 50;

        double ColX(int col) => PortStripW + EdgePad + col * (GateW + ColGap);
        double canvasW = PortStripW + EdgePad + Math.Max(1, columns.Count) * GateW
            + Math.Max(0, columns.Count - 1) * ColGap + EdgePad + PortStripW;

        var cellBounds = new Dictionary<string, Rect>(StringComparer.Ordinal);
        double interiorH;

        if (groupAware && columns.Count > 0)
        {
            // Bands ordered by first appearance in declaration order so the
            // diagram reads roughly top-to-bottom like the source VHDL.
            var bandOrder = new List<string>();
            var seenBand = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in netlist.Cells)
            {
                var key = c.SourceLine ?? "";
                if (seenBand.Add(key)) bandOrder.Add(key);
            }

            double slot = GateH + RowGap;
            double y = TopPad;
            foreach (var band in bandOrder)
            {
                // Per-column cell lists for this band, each ordered by the
                // global barycenter row index from step 3 so intra-band
                // crossings stay low.
                var perCol = new List<string>[columns.Count];
                int bandRows = 0;
                for (int col = 0; col < columns.Count; col++)
                {
                    var list = columns[col]
                        .Where(id => (byCellId[id].SourceLine ?? "") == band)
                        .OrderBy(id => rowIndex[id])
                        .ToList();
                    perCol[col] = list;
                    bandRows = Math.Max(bandRows, list.Count);
                }
                if (bandRows == 0) continue;

                double bandH = bandRows * GateH + Math.Max(0, bandRows - 1) * RowGap;
                for (int col = 0; col < columns.Count; col++)
                {
                    var list = perCol[col];
                    // Center this column's cells within the band's rows so a
                    // single-cell column lines up with taller neighbours.
                    double off = (bandRows - list.Count) / 2.0 * slot;
                    for (int r = 0; r < list.Count; r++)
                        cellBounds[list[r]] = new Rect(ColX(col), y + off + r * slot, GateW, GateH);
                }
                y += bandH + BandGap;
            }
            // Drop the trailing BandGap added after the last band.
            double bodyH = Math.Max(GateH, y - TopPad - BandGap);
            interiorH = Math.Max(bodyH, portMinH);
        }
        else
        {
            int rowsTall = columns.Count == 0 ? 0 : columns.Max(c => c.Count);
            double bodyH = Math.Max(GateH, rowsTall * (GateH + RowGap));
            interiorH = Math.Max(bodyH, portMinH);
            for (int col = 0; col < columns.Count; col++)
            {
                int rows = columns[col].Count;
                double colHeight = rows * (GateH + RowGap);
                double y0 = TopPad + (interiorH - colHeight) / 2.0;
                for (int row = 0; row < rows; row++)
                    cellBounds[columns[col][row]] = new Rect(ColX(col), y0 + row * (GateH + RowGap), GateW, GateH);
            }
        }

        double canvasH = TopPad + interiorH + BotPad;

        // 5. Pin positions: inputs distributed along the cell's left edge,
        //    output(s) along the right.
        var pinPositions = new Dictionary<(string, string), (double, double)>();
        foreach (var c in netlist.Cells)
        {
            if (!cellBounds.TryGetValue(c.Id, out var b)) continue;
            for (int i = 0; i < c.Inputs.Count; i++)
            {
                double frac = (i + 1.0) / (c.Inputs.Count + 1.0);
                pinPositions[(c.Id, c.Inputs[i].Name)] = (b.X, b.Y + b.Height * frac);
            }
            for (int i = 0; i < c.Outputs.Count; i++)
            {
                double frac = (i + 1.0) / (c.Outputs.Count + 1.0);
                pinPositions[(c.Id, c.Outputs[i].Name)] = (b.X + b.Width, b.Y + b.Height * frac);
            }
        }

        // 6. Port positions: input ports on the left strip, output ports
        //    on the right strip, both spaced vertically. The dot sits at
        //    the strip's horizontal centre; its label hangs to the side.
        var portPositions = new Dictionary<string, (double, double)>(StringComparer.Ordinal);
        for (int i = 0; i < inPorts.Count; i++)
        {
            double frac = (i + 1.0) / (inPorts.Count + 1.0);
            portPositions[inPorts[i].NetId] = (PortStripW / 2.0, TopPad + frac * interiorH);
        }
        for (int i = 0; i < outPorts.Count; i++)
        {
            double frac = (i + 1.0) / (outPorts.Count + 1.0);
            portPositions[outPorts[i].NetId] = (canvasW - PortStripW / 2.0, TopPad + frac * interiorH);
        }

        // 7. Wire routing. Two-pass strategy:
        //    a) For each net, derive a single trunk-x sitting in the
        //       column gap between source and the closest sink. Every
        //       wire of the net shares that trunk so fan-outs visually
        //       collapse to a tree rather than N independent paths.
        //    b) Stagger trunk-x per net inside its gap so unrelated nets
        //       crossing the same gap don't draw on top of each other.
        var wires = new List<WirePath>();
        // Per-gap counter: how many distinct nets we've already routed
        // through this gap. Drives the lane offset.
        var gapNetIndex = new Dictionary<int, int>();

        for (int netIdx = 0; netIdx < netlist.Nets.Count; netIdx++)
        {
            var net = netlist.Nets[netIdx];
            var src = ResolveSource(net.Id, producerOf, pinPositions, portPositions, netlist);
            if (src is not var (sx, sy, fromCellId)) continue;
            var sinks = ResolveSinks(net.Id, consumersOf, pinPositions, portPositions, netlist).ToList();
            if (sinks.Count == 0) continue;

            // Pick the trunk x: the midpoint between the source and the
            // sink whose x is closest to the source. Then stagger by net
            // ordinal in this gap so adjacent nets get different lanes.
            // (Empirically the midpoint beats trunk-near-source and
            // trunk-near-sink on both wire length and crossings for the
            // swimlane layout — moving the trunk to either side pushes the
            // long horizontal onto occupied cell rows.)
            double avgSinkX = sinks.Average(s => s.X);
            double rawTrunk = (sx + avgSinkX) / 2.0;
            int gapKey = (int)Math.Round(rawTrunk / 5); // bucket into 5-px columns
            if (!gapNetIndex.TryGetValue(gapKey, out var laneIdx)) laneIdx = 0;
            gapNetIndex[gapKey] = laneIdx + 1;
            double laneOffset = ((laneIdx % TrunkStaggerLanes) - TrunkStaggerLanes / 2.0) * 6.0;
            double trunkX = rawTrunk + laneOffset;

            foreach (var (tx, ty, toCellId) in sinks)
            {
                wires.Add(new WirePath(
                    net.Id,
                    new (double, double)[]
                    {
                        (sx, sy), (trunkX, sy), (trunkX, ty), (tx, ty),
                    },
                    FromCellId: fromCellId,
                    ToCellId: toCellId));
            }
        }

        return new LaidOutNetlist(netlist, cellBounds, pinPositions, portPositions, wires, canvasW, canvasH);
    }

    /// <summary>
    /// Resolves a net's source endpoint along with the cell that
    /// drives it (or <c>null</c> when the source is an entity input
    /// port). Carrying the cell id forward lets the renderer hide a
    /// single branch of a fan-out tree based on its origin.
    /// </summary>
    private static (double X, double Y, string? CellId)? ResolveSource(
        string netId,
        Dictionary<string, string> producerOf,
        Dictionary<(string, string), (double, double)> pinPositions,
        Dictionary<string, (double, double)> portPositions,
        Netlist netlist)
    {
        if (producerOf.TryGetValue(netId, out var cellId))
        {
            var cell = netlist.Cells.First(c => c.Id == cellId);
            var output = cell.Outputs.FirstOrDefault(p => p.NetId == netId);
            if (output != null && pinPositions.TryGetValue((cellId, output.Name), out var pos))
                return (pos.Item1, pos.Item2, cellId);
        }
        if (portPositions.TryGetValue(netId, out var portPos))
        {
            // Input port acts as the source for downstream cells.
            var port = netlist.Ports.FirstOrDefault(p => p.NetId == netId);
            if (port != null && port.Direction is PortDir.In or PortDir.InOut)
                return (portPos.Item1, portPos.Item2, null);
        }
        return null;
    }

    /// <summary>
    /// Yields each sink endpoint for a net plus the cell that consumes
    /// it (or <c>null</c> when the sink is an entity output port). Per-
    /// sink resolution keeps the wire's branch identity intact so the
    /// renderer can hide individual branches whose target cell is
    /// outside the user's current Naive-mode focus.
    /// </summary>
    private static IEnumerable<(double X, double Y, string? CellId)> ResolveSinks(
        string netId,
        Dictionary<string, List<string>> consumersOf,
        Dictionary<(string, string), (double, double)> pinPositions,
        Dictionary<string, (double, double)> portPositions,
        Netlist netlist)
    {
        if (consumersOf.TryGetValue(netId, out var cells))
        {
            foreach (var cellId in cells)
            {
                var cell = netlist.Cells.First(c => c.Id == cellId);
                foreach (var pin in cell.Inputs.Where(p => p.NetId == netId))
                {
                    if (pinPositions.TryGetValue((cellId, pin.Name), out var pos))
                        yield return (pos.Item1, pos.Item2, cellId);
                }
            }
        }
        var port = netlist.Ports.FirstOrDefault(p => p.NetId == netId);
        if (port != null && port.Direction is PortDir.Out or PortDir.Buffer or PortDir.InOut
            && portPositions.TryGetValue(netId, out var pp))
            yield return (pp.Item1, pp.Item2, null);
    }
}
