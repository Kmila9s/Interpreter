using Interpreter.Models;
using Parser.Models;

namespace Interpreter.Services;

/// <summary>Delay mechanism of a signal assignment (<c>transport</c> or <c>[reject t] inertial</c>, the default).</summary>
public enum DelayMechanism { Inertial, Transport }

/// <summary>One element of a waveform: <c>value after t</c> (<paramref name="AfterNs"/> = 0: the next delta).</summary>
public readonly record struct WaveformElement(object Value, long AfterNs);

/// <summary>
/// The signal update machinery of the delta-cycle engine, with VHDL's driver
/// model (IEEE 1076 §14.7): every process — and every concurrent statement,
/// which is a process — that assigns a signal owns a <em>driver</em> of it. A
/// driver holds its current value and its projected waveform (pending
/// transactions); assignments edit the waveform (transport, inertial with
/// pulse rejection), the transactions mature at their time, and the signal
/// takes the resolution of all its drivers: the IEEE 1164 table for
/// std_logic-based types (<see cref="StdLogic.Resolve"/>), and an error for
/// an unresolved type with more than one driver («several sources»).
/// </summary>
/// <remarks>
/// The writing process is the <see cref="CurrentOwner"/> the engine sets while
/// it runs a process or a concurrent statement (procedures write with their
/// caller's driver). A driver covers the scalar subelements its assignments
/// name statically (<c>v(3 downto 2)</c>, <c>p.lo</c>) and the whole signal
/// when an index is dynamic (<c>w(i)</c>). A signal with one driver covering
/// it whole takes that driver's value as is (no resolution).
/// </remarks>
public class SignalScheduler
{
    /// <summary>A pending transaction of one driver.</summary>
    private sealed class Transaction
    {
        public ulong Tick;
        /// <summary>True when due in the next delta (zero delay, or matured).</summary>
        public bool Now;
        public (int Start, int Width)? Span;
        public object Value = string.Empty;
        public int Line;
        public long Seq;
    }

    /// <summary>One driver: its owner, current driving value, coverage and projected waveform.</summary>
    private sealed class Driver
    {
        public required object Owner;
        public object? Value;
        /// <summary>The positions (characters of the value) this driver drives; null = the whole signal.</summary>
        public bool[]? Covers;
        /// <summary>Pending transactions in time order (the ones due now first, in statement order).</summary>
        public readonly List<Transaction> Waveform = [];
    }

    /// <summary>The drivers of one signal.</summary>
    private sealed class SignalDrivers
    {
        public required Variable Signal;
        public readonly List<Driver> Drivers = [];
        /// <summary>The signal's value before any driver existed: a new driver's initial driving value.</summary>
        public object? Initial;
    }

    /// <summary>Owner of writes made outside any process (tests, legacy callers).</summary>
    public static readonly object AnonymousOwner = new();

    /// <summary>Owner of the stimulus driving an <c>inout</c> port of the top from outside the design.</summary>
    public static readonly object ExternalOwner = new();

    private readonly Dictionary<Variable, SignalDrivers> _signals = [];
    private readonly List<SignalDrivers> _active = [];
    private readonly HashSet<SignalDrivers> _activeSet = new(ReferenceEqualityComparer.Instance);
    private readonly SortedDictionary<ulong, List<SignalDrivers>> _future = [];
    private readonly object _lock = new();
    private long _seq;
    private int _pendingNow;
    private ulong _currentTick;
    private int _tickResolutionNs = 10;

    /// <summary>
    /// The process or concurrent statement whose assignments are being executed:
    /// the owner of the drivers they write. Set by the engine around each run
    /// (<see cref="OwnedBy"/>); null means <see cref="AnonymousOwner"/>.
    /// </summary>
    public object? CurrentOwner { get; set; }

    /// <summary>Makes <paramref name="owner"/> the <see cref="CurrentOwner"/> until the returned scope is disposed (nesting restores the previous one).</summary>
    public OwnerScope OwnedBy(object owner)
    {
        var previous = CurrentOwner;
        CurrentOwner = owner;
        return new OwnerScope(this, previous);
    }

    /// <summary>Restores the previous <see cref="CurrentOwner"/> on dispose.</summary>
    public readonly struct OwnerScope(SignalScheduler scheduler, object? previous) : IDisposable
    {
        /// <inheritdoc/>
        public void Dispose() => scheduler.CurrentOwner = previous;
    }

    /// <summary>
    /// Tick resolution in nanoseconds used to convert delays into future ticks
    /// (minimum 1 ns).
    /// </summary>
    public int TickResolutionNs
    {
        get { lock (_lock) return _tickResolutionNs; }
        set { lock (_lock) _tickResolutionNs = Math.Max(1, value); }
    }

    private readonly Dictionary<Variable, int> _changedScratch = [];
    private readonly Dictionary<Variable, object?> _oldValuesScratch = [];

    /// <summary>
    /// Schedules <paramref name="newValue"/> for the next delta (a zero-delay,
    /// inertial assignment by the <see cref="CurrentOwner"/>).
    /// </summary>
    /// <param name="target">The signal written.</param>
    /// <param name="newValue">The value (for a part, its characters).</param>
    /// <param name="sourceLine">1-based source line of the statement (0 = unknown).</param>
    /// <param name="span">The part written (start and width in the value string), or null for the whole signal.</param>
    public void Schedule(Variable target, object newValue, int sourceLine = 0, (int Start, int Width)? span = null) =>
        ScheduleWaveform(target, [new WaveformElement(newValue, 0)], sourceLine, span, wholeDriver: false, DelayMechanism.Inertial, -1);

    /// <summary>
    /// Schedules a waveform (<c>v1 after t1, v2 after t2, …</c>) on the
    /// <see cref="CurrentOwner"/>'s driver of <paramref name="target"/>, editing
    /// its projected waveform as the LRM does: transactions at or after the
    /// first new one are deleted; with <see cref="DelayMechanism.Inertial"/>,
    /// the older ones inside the pulse-rejection window
    /// (<paramref name="rejectNs"/>, or the first delay when negative) are
    /// deleted too unless they lead, with the new value, up to it.
    /// </summary>
    public void ScheduleWaveform(Variable target, IReadOnlyList<WaveformElement> elements, int sourceLine,
        (int Start, int Width)? span, bool wholeDriver, DelayMechanism mechanism, long rejectNs)
    {
        if (elements.Count == 0) return;
        lock (_lock)
        {
            var signal = SignalOf(target);
            var driver = DriverOf(signal, CurrentOwner ?? AnonymousOwner, span, wholeDriver, sourceLine);
            for (int i = 0; i < elements.Count; i++)
            {
                var e = elements[i];
                var t = new Transaction
                {
                    Now = e.AfterNs <= 0,
                    Tick = e.AfterNs <= 0 ? _currentTick : _currentTick + DelayTicks(e.AfterNs),
                    Span = span,
                    Value = e.Value,
                    Line = sourceLine,
                    Seq = ++_seq,
                };
                if (i == 0) Preempt(driver, t, mechanism, rejectNs < 0 ? e.AfterNs : rejectNs);
                Insert(driver, t);
                if (t.Now)
                {
                    _pendingNow++;
                    Activate(signal);
                }
                else
                {
                    if (!_future.TryGetValue(t.Tick, out var list)) _future[t.Tick] = list = [];
                    list.Add(signal);
                }
            }
        }
    }

    /// <summary>
    /// Sets the value the outside world drives on <paramref name="target"/> (the
    /// stimulus of an <c>inout</c> port of the top, or of a part of it) and
    /// resolves the signal now against the design's drivers: an external
    /// change, like a stimulus on an input, seen as an event of this time.
    /// </summary>
    public void DriveExternal(Variable target, object value, (int Start, int Width)? span = null)
    {
        lock (_lock)
        {
            var signal = SignalOf(target);
            var driver = DriverOf(signal, ExternalOwner, span, wholeDriver: false, line: 0);
            driver.Value = span is { } part
                ? VectorIndexing.Write(target, driver.Value?.ToString() ?? string.Empty, part, value?.ToString() ?? string.Empty)
                : ExpandAggregateMarker(value!, target);
            target.Value = Resolved(signal);
        }
    }

    /// <summary>Delay in ticks of <paramref name="delayNs"/>, rounded up and at least <paramref name="min"/>.</summary>
    private ulong DelayTicks(long delayNs, long min = 1)
    {
        int res = Math.Max(1, _tickResolutionNs);
        return (ulong)Math.Max(min, (delayNs + res - 1) / res);
    }

    private SignalDrivers SignalOf(Variable target)
    {
        if (!_signals.TryGetValue(target, out var signal))
            _signals[target] = signal = new SignalDrivers { Signal = target, Initial = target.Value };
        return signal;
    }

    /// <summary>
    /// The driver of <paramref name="owner"/> on <paramref name="signal"/>, created
    /// on its first assignment (driving the signal's initial value) and widened
    /// to cover the part written. A second driver of an unresolved type is the
    /// «several sources for unresolved signal» error of GHDL's elaboration.
    /// </summary>
    private static Driver DriverOf(SignalDrivers signal, object owner, (int Start, int Width)? span, bool wholeDriver, int line)
    {
        Driver? driver = null;
        foreach (var d in signal.Drivers)
            if (ReferenceEquals(d.Owner, owner)) { driver = d; break; }
        if (driver is null)
        {
            if (signal.Drivers.Count > 0 && !TypeMarks.IsResolved(signal.Signal))
                throw new SimulationRuntimeException($"several sources for unresolved signal \"{signal.Signal.Name}\"", line);
            driver = new Driver { Owner = owner, Value = signal.Initial };
            if (span is { } s && !wholeDriver) driver.Covers = new bool[Width(signal.Signal)];
            signal.Drivers.Add(driver);
        }
        if (driver.Covers is { } covers)
        {
            if (span is { } s && !wholeDriver)
            {
                for (int i = s.Start; i < s.Start + s.Width && i < covers.Length; i++) covers[i] = true;
                if (Array.TrueForAll(covers, c => c)) driver.Covers = null;   // every element: the fast path
            }
            else driver.Covers = null;
        }
        return driver;
    }

    private static int Width(Variable v) => v.Value?.ToString()?.Length ?? 0;

    /// <summary>
    /// Deletes the transactions <paramref name="t"/> preempts on its driver:
    /// those at or after its time (on the same or a smaller part), and with
    /// inertial delay the ones inside the rejection window
    /// [<c>t − reject</c>, <c>t</c>) unless a run of them with <paramref name="t"/>'s
    /// value immediately precedes it.
    /// </summary>
    private void Preempt(Driver driver, Transaction t, DelayMechanism mechanism, long rejectNs)
    {
        var w = driver.Waveform;
        if (w.Count == 0) return;
        for (int i = w.Count - 1; i >= 0; i--)
        {
            var o = w[i];
            if (Before(o, t) || !Within(o.Span, t.Span)) continue;
            if (o.Now) _pendingNow--;   // a due transaction replaced in the same delta
            w.RemoveAt(i);
        }
        if (mechanism != DelayMechanism.Inertial || t.Now) return;
        ulong windowStart = rejectNs <= 0 ? t.Tick : t.Tick - Math.Min(t.Tick, DelayTicks(rejectNs, min: 0));
        bool keep = true;
        for (int i = w.Count - 1; i >= 0; i--)
        {
            var o = w[i];
            if (o.Now || o.Tick < windowStart || !Within(o.Span, t.Span)) continue;
            if (keep && Equals(o.Value, t.Value)) continue;
            keep = false;
            w.RemoveAt(i);
        }
    }

    /// <summary>True when <paramref name="o"/> happens strictly before <paramref name="t"/>.</summary>
    private static bool Before(Transaction o, Transaction t) =>
        !t.Now && (o.Now || o.Tick < t.Tick);

    /// <summary>True when part <paramref name="inner"/> lies inside part <paramref name="outer"/> (null = whole).</summary>
    private static bool Within((int Start, int Width)? inner, (int Start, int Width)? outer) =>
        outer is not { } o || inner is { } i && i.Start >= o.Start && i.Start + i.Width <= o.Start + o.Width;

    /// <summary>Inserts <paramref name="t"/> keeping time order (due-now first; equal times in statement order).</summary>
    private static void Insert(Driver driver, Transaction t)
    {
        var w = driver.Waveform;
        int at = w.Count;
        while (at > 0 && Before(t, w[at - 1])) at--;
        w.Insert(at, t);
    }

    private void Activate(SignalDrivers signal)
    {
        if (_activeSet.Add(signal)) _active.Add(signal);
    }

    /// <summary>Applies all pending updates for the current delta cycle.</summary>
    /// <returns>Set of signals that actually changed value.</returns>
    public HashSet<Variable> ApplyCurrentUpdates() => new(ApplyCurrentUpdatesWithLines().Keys);

    /// <summary>
    /// Expands an <c>(others=>X)</c> aggregate marker to a literal bit string of
    /// the target's declared width.
    /// </summary>
    private static object ExpandAggregateMarker(object newValue, Variable target) =>
        target.Size > 0 && HdlLiteral.TryOthersFill(newValue as string, out char fill)
            ? new string(fill, target.Size)
            : newValue;

    /// <summary>
    /// Commits the delta: the signals whose value changed, each with the 1-based
    /// source line of the last transaction that drove it (0 when unknown).
    /// </summary>
    public Dictionary<Variable, int> ApplyCurrentUpdatesWithLines() => ApplyCurrentUpdatesWithLinesAndOldValues().ChangedLines;

    /// <summary>
    /// Commits the delta: every driver applies its due transactions in statement
    /// order, then each active signal takes the resolution of its drivers. Returns
    /// the signals whose value changed (with the line of the last transaction)
    /// and their value before the delta (<c>'last_value</c>, <c>'event</c>). A
    /// signal assigned and restored within one delta does not change.
    /// </summary>
    public (Dictionary<Variable, int> ChangedLines, Dictionary<Variable, object?> OldValues)
        ApplyCurrentUpdatesWithLinesAndOldValues()
    {
        var changed = _changedScratch;
        var oldValues = _oldValuesScratch;
        changed.Clear();
        oldValues.Clear();
        lock (_lock)
        {
            foreach (var signal in _active)
            {
                int line = 0;
                foreach (var driver in signal.Drivers)
                {
                    var w = driver.Waveform;
                    int n = 0;
                    while (n < w.Count && w[n].Now)
                    {
                        var t = w[n++];
                        driver.Value = t.Span is { } part
                            ? VectorIndexing.Write(signal.Signal, driver.Value?.ToString() ?? string.Empty,
                                                   part, t.Value?.ToString() ?? string.Empty)
                            : ExpandAggregateMarker(t.Value, signal.Signal);
                        if (t.Line > 0) line = t.Line;
                    }
                    if (n > 0) w.RemoveRange(0, n);
                }
                object? before = signal.Signal.Value;
                object? after = Resolved(signal);
                if (!Equals(before, after))
                {
                    signal.Signal.Value = after;
                    if (!Equals(before, signal.Signal.Value))
                    {
                        changed[signal.Signal] = line;
                        oldValues[signal.Signal] = before;
                    }
                }
            }
            _active.Clear();
            _activeSet.Clear();
            _pendingNow = 0;
        }
        return (changed, oldValues);
    }

    /// <summary>
    /// The effective value of <paramref name="signal"/>: its only driver's value
    /// when that driver covers it whole, else per position the resolution of the
    /// drivers covering it (positions no driver covers keep their value).
    /// </summary>
    private static object? Resolved(SignalDrivers signal)
    {
        var drivers = signal.Drivers;
        if (drivers.Count == 1 && drivers[0].Covers is null) return drivers[0].Value;
        string current = signal.Signal.Value?.ToString() ?? string.Empty;
        var result = current.ToCharArray();
        var driven = new bool[result.Length];
        foreach (var d in drivers)
        {
            string value = d.Value?.ToString() ?? string.Empty;
            int n = Math.Min(value.Length, result.Length);
            for (int i = 0; i < n; i++)
            {
                if (d.Covers is { } c && (i >= c.Length || !c[i])) continue;
                result[i] = driven[i] ? StdLogic.Resolve(result[i], value[i]) : value[i];
                driven[i] = true;
            }
        }
        return new string(result);
    }

    /// <summary>
    /// Enters tick <paramref name="newTick"/>: the transactions due at or before
    /// it mature and are committed by the next <see cref="ApplyCurrentUpdatesWithLinesAndOldValues"/>
    /// — before any process resumes at this time, so a process woken now reads them.
    /// </summary>
    public void AdvanceTime(ulong newTick)
    {
        lock (_lock)
        {
            _currentTick = newTick;
            while (_future.Count > 0)
            {
                var first = _future.First();
                if (first.Key > newTick) break;
                foreach (var signal in first.Value)
                {
                    bool any = false;
                    foreach (var driver in signal.Drivers)
                        foreach (var t in driver.Waveform)
                            if (!t.Now && t.Tick <= newTick) { t.Now = true; _pendingNow++; any = true; }
                    if (any) Activate(signal);
                }
                _future.Remove(first.Key);
            }
        }
    }

    /// <summary>True when transactions are due in the next delta.</summary>
    public bool HasPendingUpdates
    {
        get { lock (_lock) return _pendingNow > 0; }
    }

    /// <summary>True when transactions are pending at a future time.</summary>
    public bool HasFutureUpdates
    {
        get { lock (_lock) return _future.Count > 0; }
    }

    /// <summary>Number of transactions due in the next delta.</summary>
    public int PendingCount
    {
        get { lock (_lock) return _pendingNow; }
    }

    /// <summary>The next tick at which a transaction is scheduled, or null.</summary>
    public ulong? NextScheduledTick
    {
        get { lock (_lock) return _future.Count > 0 ? _future.Keys.First() : null; }
    }

    /// <summary>Forgets every driver and pending transaction.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _signals.Clear();
            _active.Clear();
            _activeSet.Clear();
            _future.Clear();
            _pendingNow = 0;
        }
    }

    /// <summary>Drops the transactions due in the next delta.</summary>
    public void ClearCurrent()
    {
        lock (_lock)
        {
            foreach (var signal in _active)
                foreach (var driver in signal.Drivers)
                    driver.Waveform.RemoveAll(t => t.Now);
            _active.Clear();
            _activeSet.Clear();
            _pendingNow = 0;
        }
    }
}
