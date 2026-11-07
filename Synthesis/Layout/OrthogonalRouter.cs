namespace Interpreter.Synthesis.Layout;

/// <summary>
/// Obstacle-aware orthogonal wire router for the block styles (RTL / Nodos).
///
/// The layered layout places blocks in dataflow columns with wide, always
/// empty <em>corridors</em> between them. This router threads every wire
/// through those corridors so no wire body ever crosses a block:
/// <list type="bullet">
///   <item>vertical runs always sit inside a column gap (corridors contain
///         no blocks by construction, so they are clear at every y);</item>
///   <item>long forward hops cross intermediate columns through a horizontal
///         lane scanned to be clear of block rows (falling back to an express
///         lane below the whole diagram);</item>
///   <item>feedback / self-loop edges return through a dedicated express lane
///         below all blocks, so a signal that must loop back never overlaps
///         its own block.</item>
/// </list>
///
/// It only reasons about geometry (corridor x-ranges, obstacle rectangles,
/// endpoint coordinates), so it stays decoupled from the netlist model. Lane
/// assignment is deterministic (first-come per net, in net order) so the
/// layout still hashes stably for snapshot tests.
/// </summary>
internal sealed class OrthogonalRouter
{
    // How many parallel vertical lanes to slice a corridor into before
    // wrapping. Corridors are ~260px wide in block mode, so a handful of
    // lanes never collide in any reasonable teaching diagram.
    private const int CorridorLanes = 10;
    private const double LaneMinStep = 7;
    private const double LaneMaxStep = 18;
    private const double CorridorInset = 4;   // keep lanes off the corridor edges
    // Clearance a horizontal lane must keep from a block edge, and how far
    // past an edge the candidate lanes are generated.
    private const double ClearMargin = 4;
    private const double FlyoverGap = 16;
    private const double HorizLaneSpacing = 8; // spread parallel horizontals
    // Feedback express band: distance below the lowest block, then one lane
    // per feedback net stacked downward.
    private const double FeedbackGap = 44;
    private const double FeedbackLaneSpacing = 16;

    private readonly double[] _colXArr;
    private readonly double[] _colW;
    private readonly int _nCols;
    private readonly double _portStripW;
    private readonly double _canvasW;
    private readonly IReadOnlyList<Rect> _obstacles;
    private readonly double _maxBottom;

    // Deterministic lane bookkeeping.
    private readonly Dictionary<(int Corridor, string Net), double> _corridorLaneX = new();
    private readonly Dictionary<int, int> _corridorNext = new();
    // Planned lane assignment: the ordered, distinct nets that pass through each
    // corridor, filled by Plan() before any routing and turned into evenly-spread
    // lane x's by FinalizeLanes(). This replaces the old modulo-wrapped lanes,
    // which made the 11th+ net in a corridor reuse an earlier net's exact x and
    // draw on top of it.
    private readonly Dictionary<int, List<string>> _corridorNets = new();
    private bool _lanesPlanned;
    private readonly Dictionary<string, int> _feedbackLane = new(StringComparer.Ordinal);
    private int _feedbackCount;
    // Horizontal crossing lanes already placed, so a new crossing over an
    // overlapping x-range picks a different y instead of drawing on top.
    private readonly List<(double Y, double X1, double X2)> _placedHoriz = new();

    public OrthogonalRouter(double[] colXArr, double[] colW, int nCols,
        double portStripW, double canvasW, double canvasH, IReadOnlyCollection<Rect> obstacles)
    {
        _colXArr = colXArr;
        _colW = colW;
        _nCols = nCols;
        _portStripW = portStripW;
        _canvasW = canvasW;
        _obstacles = obstacles as IReadOnlyList<Rect> ?? obstacles.ToList();
        _maxBottom = _obstacles.Count > 0
            ? _obstacles.Max(o => o.Y + o.Height)
            : canvasH - FeedbackGap;
        CanvasHeight = canvasH;
    }

    /// <summary>Grows as feedback lanes are added below the diagram.</summary>
    public double CanvasHeight { get; private set; }

    private double ColLeft(int c) => _colXArr[c];
    private double ColRight(int c) => _colXArr[c] + _colW[c];

    /// <summary>Column index whose box spans <paramref name="x"/>; -1 for the
    /// left port strip, <c>_nCols</c> for the right strip.</summary>
    private int ColumnOfX(double x)
    {
        if (x < ColLeft(0)) return -1;
        for (int c = 0; c < _nCols; c++)
            if (x <= ColRight(c) + 0.5) return c;
        return _nCols;
    }

    // Corridor g sits between column g and g+1 (g from -1 to _nCols-1).
    private double CorridorLeft(int g) => g < 0 ? _portStripW : ColRight(g);
    private double CorridorRight(int g) => g + 1 >= _nCols ? _canvasW - _portStripW : ColLeft(g + 1);

    /// <summary>Registers that <paramref name="net"/> will pass through the
    /// corridor(s) between its source and sink columns, so FinalizeLanes can give
    /// every net in a corridor its own evenly-spread lane. Call once per routed
    /// branch, before any Route call.</summary>
    public void Plan(string net, double sx, double tx)
    {
        int a = ColumnOfX(sx), b = ColumnOfX(tx);
        // Mirror the corridors Route() actually uses: the one right of the source
        // and the one left of the sink (same corridor for an adjacent hop).
        RegisterCorridor(a, net);
        RegisterCorridor(b - 1, net);
    }

    private void RegisterCorridor(int g, string net)
    {
        if (g < -1 || g >= _nCols) return;
        var list = _corridorNets.TryGetValue(g, out var l) ? l : (_corridorNets[g] = new List<string>());
        if (!list.Contains(net)) list.Add(net);
    }

    /// <summary>Turns the planned per-corridor net lists into evenly-spread lane
    /// x's — one distinct lane per net, filling the corridor width — so no two
    /// nets share a vertical track. Idempotent; call after all Plan() calls.</summary>
    public void FinalizeLanes()
    {
        foreach (var (g, nets) in _corridorNets)
        {
            double left = CorridorLeft(g) + CorridorInset;
            double right = CorridorRight(g) - CorridorInset;
            if (right <= left) { double c = (CorridorLeft(g) + CorridorRight(g)) / 2.0; left = right = c; }
            int n = nets.Count;
            for (int i = 0; i < n; i++)
                _corridorLaneX[(g, nets[i])] = left + (i + 1.0) / (n + 1.0) * (right - left);
        }
        _lanesPlanned = true;
    }

    /// <summary>A deterministic vertical lane x inside corridor
    /// <paramref name="g"/>. The same net reuses the same lane so a fan-out
    /// collapses into a shared trunk. Lanes are planned up front (see
    /// <see cref="FinalizeLanes"/>) so distinct nets never share a track; the
    /// lazy path below is a fallback for any unplanned branch.</summary>
    private double CorridorLaneX(int g, string net)
    {
        if (_corridorLaneX.TryGetValue((g, net), out var cached)) return cached;
        if (_lanesPlanned)
        {
            // Unplanned net reaching here is rare (planning covers every routed
            // branch); append it to the corridor and re-space so it still gets a
            // unique lane rather than colliding.
            RegisterCorridor(g, net);
            int gg = Math.Clamp(g, -1, _nCols - 1);
            if (_corridorNets.TryGetValue(gg, out var nets))
            {
                double l = CorridorLeft(gg) + CorridorInset, r = CorridorRight(gg) - CorridorInset;
                if (r <= l) { double c = (CorridorLeft(gg) + CorridorRight(gg)) / 2.0; l = r = c; }
                int n = nets.Count;
                for (int i = 0; i < n; i++)
                    _corridorLaneX[(gg, nets[i])] = l + (i + 1.0) / (n + 1.0) * (r - l);
                if (_corridorLaneX.TryGetValue((g, net), out var now)) return now;
            }
        }
        int idx = _corridorNext.GetValueOrDefault(g);
        _corridorNext[g] = idx + 1;
        double left = CorridorLeft(g), right = CorridorRight(g);
        double center = (left + right) / 2.0;
        double width = right - left;
        double step = Math.Clamp(width * 0.7 / CorridorLanes, LaneMinStep, LaneMaxStep);
        double off = ((idx % CorridorLanes) - CorridorLanes / 2.0 + 0.5) * step;
        double x = Math.Clamp(center + off, left + CorridorInset, right - CorridorInset);
        _corridorLaneX[(g, net)] = x;
        return x;
    }

    /// <summary>True if a horizontal crossing at <paramref name="y"/> over
    /// [<paramref name="x1"/>, <paramref name="x2"/>] doesn't share a lane with an
    /// already-placed crossing whose x-range overlaps it.</summary>
    private bool HorizLaneFree(double x1, double x2, double y)
    {
        if (x1 > x2) (x1, x2) = (x2, x1);
        foreach (var p in _placedHoriz)
        {
            if (Math.Abs(p.Y - y) >= HorizLaneSpacing - 0.5) continue;
            if (x2 <= p.X1 || x1 >= p.X2) continue; // x-ranges don't overlap
            return false;
        }
        return true;
    }

    /// <summary>True if a horizontal segment at <paramref name="y"/> spanning
    /// [<paramref name="x1"/>, <paramref name="x2"/>] clears every block.</summary>
    private bool HorizClear(double x1, double x2, double y)
    {
        if (x1 > x2) (x1, x2) = (x2, x1);
        foreach (var o in _obstacles)
        {
            if (y <= o.Y - ClearMargin || y >= o.Y + o.Height + ClearMargin) continue;
            if (x2 <= o.X || x1 >= o.X + o.Width) continue;
            return false;
        }
        return true;
    }

    /// <summary>Picks a clear horizontal lane y for a crossing over
    /// [<paramref name="x1"/>, <paramref name="x2"/>], preferring one near
    /// <paramref name="preferredY"/>. Interior row-gaps win; the express band
    /// below all blocks is the guaranteed fallback.</summary>
    private double ChooseClearLane(double x1, double x2, double preferredY)
    {
        if (x1 > x2) (x1, x2) = (x2, x1);
        double belowAll = _maxBottom + FeedbackGap;

        var candidates = new List<double> { preferredY };
        foreach (var o in _obstacles)
        {
            if (x2 <= o.X || x1 >= o.X + o.Width) continue; // not spanned in x
            candidates.Add(o.Y - FlyoverGap);
            candidates.Add(o.Y + o.Height + FlyoverGap);
        }
        candidates.Add(belowAll);

        double best = belowAll;
        double bestDist = double.MaxValue;
        foreach (var y in candidates)
        {
            if (!HorizClear(x1, x2, y)) continue;
            double d = Math.Abs(y - preferredY);
            if (d < bestDist) { bestDist = d; best = y; }
        }

        // Spread parallel crossings so two nets that would share a lane don't
        // draw on top of each other. Search outward from the preferred clear lane
        // for a y that is both block-clear and not already taken by an
        // overlapping crossing; fall back to the express band if none is free.
        double chosen = best; // fallback: the nearest clear lane, even if shared
        for (int k = 0; k <= 80; k++)
        {
            double cand = best + ((k % 2 == 0 ? 1 : -1) * ((k + 1) / 2) * HorizLaneSpacing);
            if (HorizClear(x1, x2, cand) && HorizLaneFree(x1, x2, cand))
            {
                chosen = cand;
                break;
            }
        }
        _placedHoriz.Add((chosen, x1, x2));
        best = chosen;

        if (best + FeedbackGap > CanvasHeight) CanvasHeight = best + FeedbackGap;
        return best;
    }

    private double FeedbackLaneY(string net)
    {
        if (!_feedbackLane.TryGetValue(net, out var idx))
        {
            idx = _feedbackCount++;
            _feedbackLane[net] = idx;
        }
        double y = _maxBottom + FeedbackGap + idx * FeedbackLaneSpacing;
        if (y + FeedbackGap > CanvasHeight) CanvasHeight = y + FeedbackGap;
        return y;
    }

    /// <summary>Routes one source→sink branch as an orthogonal polyline that
    /// avoids every block.</summary>
    public IReadOnlyList<(double X, double Y)> Route(string net, double sx, double sy, double tx, double ty)
    {
        int a = ColumnOfX(sx);   // source column
        int b = ColumnOfX(tx);   // sink column

        // Feedback / self-loop: sink is at or left of the source. Return via
        // the express band below all blocks so the loop never crosses a block.
        if (b <= a)
        {
            double vxSrc = CorridorLaneX(a, net);          // corridor right of source
            double vxDst = CorridorLaneX(b - 1, net);      // corridor left of sink
            double ly = FeedbackLaneY(net);
            return new (double, double)[]
            {
                (sx, sy), (vxSrc, sy), (vxSrc, ly), (vxDst, ly), (vxDst, ty), (tx, ty),
            };
        }

        // Adjacent forward hop: a single corridor separates the two columns,
        // so a plain elbow through that corridor is already clear.
        if (b == a + 1)
        {
            double trunkX = CorridorLaneX(a, net);
            return new (double, double)[]
            {
                (sx, sy), (trunkX, sy), (trunkX, ty), (tx, ty),
            };
        }

        // Forward hop spanning several columns: drop into the corridor right of
        // the source, cross at a clear horizontal lane, then into the corridor
        // left of the sink.
        double vSrc = CorridorLaneX(a, net);
        double vDst = CorridorLaneX(b - 1, net);
        double laneY = ChooseClearLane(vSrc, vDst, (sy + ty) / 2.0);
        return new (double, double)[]
        {
            (sx, sy), (vSrc, sy), (vSrc, laneY), (vDst, laneY), (vDst, ty), (tx, ty),
        };
    }
}
