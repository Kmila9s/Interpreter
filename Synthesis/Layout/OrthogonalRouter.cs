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
    private readonly Dictionary<string, int> _feedbackLane = new(StringComparer.Ordinal);
    private int _feedbackCount;
    private readonly Dictionary<long, int> _flyoverUse = new();

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

    /// <summary>A deterministic vertical lane x inside corridor
    /// <paramref name="g"/>. The same net reuses the same lane so a fan-out
    /// collapses into a shared trunk.</summary>
    private double CorridorLaneX(int g, string net)
    {
        if (_corridorLaneX.TryGetValue((g, net), out var cached)) return cached;
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

        // Nudge parallel crossings that land on the same lane apart, keeping
        // the result clear.
        long bucket = (long)Math.Round(best);
        int used = _flyoverUse.GetValueOrDefault(bucket);
        _flyoverUse[bucket] = used + 1;
        if (used > 0)
        {
            double nudged = best + ((used % 2 == 1 ? 1 : -1) * ((used + 1) / 2) * HorizLaneSpacing);
            if (HorizClear(x1, x2, nudged)) best = nudged;
        }

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
