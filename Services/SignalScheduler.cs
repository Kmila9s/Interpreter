using Interpreter.Models;
using Parser.Models;

namespace Interpreter.Services;

/// <summary>
/// Represents a scheduled signal update.
/// </summary>
/// <param name="Target">The target signal/variable to update.</param>
/// <param name="NewValue">The new value to assign.</param>
/// <param name="Delay">Optional delay for "after X ns" syntax.</param>
/// <param name="SourceLine">Source line number that produced this update.</param>
/// <param name="Span">Start and width, in the target's value string, of the part written (resolved when the
/// statement ran), or null for the whole signal.</param>
public record SignalUpdate(
    Variable Target,
    object NewValue,
    TimeSpan? Delay = null,
    int SourceLine = 0,
    (int Start, int Width)? Span = null
);

/// <summary>
/// Manages scheduled signal updates for delta cycle simulation.
/// Implements VHDL semantics where signal assignments are scheduled, not immediate.
/// </summary>
public class SignalScheduler
{
    /// <summary>
    /// Updates to apply at current delta cycle.
    /// </summary>
    private readonly Queue<SignalUpdate> _currentDeltaUpdates = new();

    /// <summary>
    /// Updates scheduled for future time (after X ns).
    /// Key is the tick at which the update should be applied.
    /// </summary>
    private readonly SortedDictionary<ulong, Queue<SignalUpdate>> _futureUpdates = new();

    /// <summary>
    /// Lock object for thread safety.
    /// </summary>
    private readonly object _lock = new();

    /// <summary>
    /// Cached current tick + tick resolution so `after N ns` assignments
    /// (`ScheduledOperation` → `ScheduleAfterNs`) can compute a future
    /// tick without having to plumb the engine state through every caller.
    /// Updated by `AdvanceTime` and the engine's setup. Bench bug #8.
    /// </summary>
    private ulong _currentTick = 0;
    private int _tickResolutionNs = 10;

    /// <summary>
    /// Tick resolution in nanoseconds used to convert delays into future ticks
    /// (minimum 1 ns).
    /// </summary>
    public int TickResolutionNs
    {
        get { lock (_lock) return _tickResolutionNs; }
        set { lock (_lock) _tickResolutionNs = Math.Max(1, value); }
    }

    /// <summary>
    /// F-G-08 pass 2 (2026-07-16): reusable scratch buffer returned by
    /// <see cref="ApplyCurrentUpdatesWithLines"/>. The caller consumes the
    /// returned dictionary synchronously before the next apply call (see
    /// <c>DeltaCycleEngine.RunDeltaCycleAsync</c>, which snapshots the
    /// per-frame copy into its own container), so reuse is safe. Saves one
    /// dict allocation per delta.
    /// </summary>
    private readonly Dictionary<Variable, int> _changedScratch = new();

    /// <summary>
    /// Schedules a signal update for the current delta cycle.
    /// The update will be applied when ApplyCurrentUpdates() is called.
    /// </summary>
    /// <param name="target">The target signal to update.</param>
    /// <param name="newValue">The new value to assign.</param>
    /// <param name="sourceLine">Source line number that produced this update.</param>
    /// <param name="span">Part written (start and width in the value string, e.g. of <c>v(3 downto 2)</c> or <c>p.lo</c>), or null for the whole signal.
    /// Part writes keep their statement order relative to every other write of the same delta.</param>
    public void Schedule(Variable target, object newValue, int sourceLine = 0, (int Start, int Width)? span = null)
    {
        lock (_lock)
        {
            _currentDeltaUpdates.Enqueue(new SignalUpdate(target, newValue, null, sourceLine, span));
        }
    }

    /// <summary>
    /// Convenience overload: schedules an update at `currentTick +
    /// ceil(delayNs / tickResNs)`. Tracks delay in nanoseconds rather than
    /// `TimeSpan` so sub-100ns delays (1 ns, 10 ns) round correctly.
    /// Bench bug #8 fix.
    /// </summary>
    public void ScheduleAfterNs(Variable target, object newValue, long delayNs, int sourceLine = 0, (int Start, int Width)? span = null)
    {
        lock (_lock)
        {
            int res = Math.Max(1, _tickResolutionNs);
            ulong delayTicks = (ulong)Math.Max(1, (delayNs + res - 1) / res);
            ulong futureTick = _currentTick + delayTicks;
            if (!_futureUpdates.TryGetValue(futureTick, out var queue))
            {
                queue = new Queue<SignalUpdate>();
                _futureUpdates[futureTick] = queue;
            }
            queue.Enqueue(new SignalUpdate(target, newValue, TimeSpan.FromTicks(delayNs * 10), sourceLine, span));
        }
    }

    /// <summary>
    /// Applies all pending updates for the current delta cycle.
    /// </summary>
    /// <returns>Set of signals that actually changed value.</returns>
    public HashSet<Variable> ApplyCurrentUpdates()
    {
        // Back-compat thin wrapper around ApplyCurrentUpdatesWithLines —
        // discards the source-line map for callers that only care about
        // which signals changed.
        return new HashSet<Variable>(ApplyCurrentUpdatesWithLines().Keys);
    }

    /// <summary>
    /// Expand an `(others=>X)` aggregate-marker string to a literal bitstring
    /// matching the target signal's declared width. Without this, the marker
    /// emitted by <c>Operation.ParseOthersAggregate</c> and the init-value
    /// path in <c>VariableSyntetizer</c> would land in the target's Value
    /// verbatim — the VCD writer then emits the 11-char string (non-bit chars
    /// map to 'x') instead of, say, eight clean zeros. The init-value path
    /// has its own eager-expansion guard at construction; this is the
    /// engine-side guard for sequential / concurrent <c>signal &lt;=
    /// (others =&gt; '0')</c> assignments.
    /// </summary>
    private static object ExpandAggregateMarker(object newValue, Variable target)
    {
        return target.Size > 0 && HdlLiteral.TryOthersFill(newValue as string, out char fill)
            ? new string(fill, target.Size)
            : newValue;
    }

    /// <summary>
    /// Phase B+ Deep AST (2026-05-19): drains the per-delta queue, returning
    /// the signals that actually changed along with the 1-based source line
    /// of the operation that drove each change (0 when unknown). When
    /// multiple updates target the same signal in the same delta, the
    /// LAST update wins for both value AND line — matches scheduler semantics.
    ///
    /// Tier 4.1 (2026-07-28): back-compat thin shim over
    /// <see cref="ApplyCurrentUpdatesWithLinesAndOldValues"/>. Existing callers
    /// that only need the changed-lines map keep working; the engine's hot
    /// path uses the two-out variant to feed <c>SignalAttributeHandler.OnDeltaCommit</c>.
    /// </summary>
    public Dictionary<Variable, int> ApplyCurrentUpdatesWithLines()
    {
        var (changed, _) = ApplyCurrentUpdatesWithLinesAndOldValues();
        return changed;
    }

    private readonly Dictionary<Variable, object?> _oldValuesScratch = new();

    /// <summary>
    /// Tier 4.1: same commit pass as <see cref="ApplyCurrentUpdatesWithLines"/>
    /// but also exposes the per-signal value-immediately-before-the-commit.
    /// <c>SignalAttributeHandler.OnDeltaCommit</c> consumes the OldValues dict
    /// to populate `'last_value`, `'event`, and `'stable(T)` per-delta state.
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
            while (_currentDeltaUpdates.Count > 0)
            {
                var update = _currentDeltaUpdates.Dequeue();
                object? oldValue = update.Target.Value;
                update.Target.Value = update.Span is { } part
                    ? VectorIndexing.Write(update.Target, oldValue?.ToString() ?? string.Empty,
                                           part, update.NewValue?.ToString() ?? string.Empty)
                    : ExpandAggregateMarker(update.NewValue, update.Target);
                if (!Equals(oldValue, update.Target.Value))
                {
                    changed[update.Target] = update.SourceLine;
                    // First writer for this target within the delta wins the
                    // "before" snapshot; if the same signal is updated more
                    // than once, later writes replace update.Target.Value
                    // but the ORIGINAL delta-start value is what LRM's
                    // `'last_value` demands.
                    if (!oldValues.ContainsKey(update.Target))
                        oldValues[update.Target] = oldValue;
                }
            }
        }
        return (changed, oldValues);
    }

    /// <summary>
    /// Moves future updates that are now due to the current queue.
    /// Call this at the beginning of each time step.
    /// </summary>
    /// <param name="newTick">The new simulation tick.</param>
    public void AdvanceTime(ulong newTick)
    {
        lock (_lock)
        {
            _currentTick = newTick;
            // v0.17.0: Avoid LINQ allocation - SortedDictionary is ordered, so we can break early
            // Also reuse a static list to avoid repeated allocations
            while (_futureUpdates.Count > 0)
            {
                var firstEntry = _futureUpdates.First();
                if (firstEntry.Key > newTick)
                    break; // All remaining entries are in the future

                var updates = firstEntry.Value;
                while (updates.Count > 0)
                {
                    _currentDeltaUpdates.Enqueue(updates.Dequeue());
                }
                _futureUpdates.Remove(firstEntry.Key);
            }
        }
    }

    /// <summary>
    /// Returns true if there are pending updates for the current delta cycle.
    /// </summary>
    public bool HasPendingUpdates
    {
        get
        {
            lock (_lock)
            {
                return _currentDeltaUpdates.Count > 0;
            }
        }
    }

    /// <summary>
    /// Returns true if there are any future scheduled updates.
    /// </summary>
    public bool HasFutureUpdates
    {
        get
        {
            lock (_lock)
            {
                return _futureUpdates.Count > 0;
            }
        }
    }

    /// <summary>
    /// Gets the number of pending updates for the current delta cycle.
    /// </summary>
    public int PendingCount
    {
        get
        {
            lock (_lock)
            {
                return _currentDeltaUpdates.Count;
            }
        }
    }

    /// <summary>
    /// Gets the next tick at which a future update is scheduled.
    /// Returns null if no future updates are scheduled.
    /// </summary>
    public ulong? NextScheduledTick
    {
        get
        {
            lock (_lock)
            {
                return _futureUpdates.Count > 0 ? _futureUpdates.Keys.First() : null;
            }
        }
    }

    /// <summary>
    /// Clears all scheduled updates (both current and future).
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _currentDeltaUpdates.Clear();
            _futureUpdates.Clear();
        }
    }

    /// <summary>
    /// Clears only the current delta cycle updates.
    /// </summary>
    public void ClearCurrent()
    {
        lock (_lock)
        {
            _currentDeltaUpdates.Clear();
        }
    }
}
