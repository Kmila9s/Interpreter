using Parser.Models;

namespace Interpreter.Services;

/// <summary>
/// Represents a scheduled signal update.
/// </summary>
/// <param name="Target">The target signal/variable to update.</param>
/// <param name="NewValue">The new value to assign.</param>
/// <param name="Delay">Optional delay for "after X ns" syntax.</param>
public record SignalUpdate(
    Variable Target,
    object NewValue,
    TimeSpan? Delay = null
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
    /// Schedules a signal update for the current delta cycle.
    /// The update will be applied when ApplyCurrentUpdates() is called.
    /// </summary>
    /// <param name="target">The target signal to update.</param>
    /// <param name="newValue">The new value to assign.</param>
    public void Schedule(Variable target, object newValue)
    {
        lock (_lock)
        {
            _currentDeltaUpdates.Enqueue(new SignalUpdate(target, newValue));
        }
    }

    /// <summary>
    /// Schedules a signal update for a future time.
    /// Used for "signal <= value after X ns" syntax.
    /// </summary>
    /// <param name="target">The target signal to update.</param>
    /// <param name="newValue">The new value to assign.</param>
    /// <param name="delay">The delay before the assignment takes effect.</param>
    /// <param name="currentTick">The current simulation tick.</param>
    /// <param name="tickResolutionNs">The resolution of a single tick in nanoseconds.</param>
    public void ScheduleAfter(Variable target, object newValue, TimeSpan delay, ulong currentTick, int tickResolutionNs = 10)
    {
        lock (_lock)
        {
            // Calculate the future tick when this update should be applied
            ulong delayTicks = (ulong)(delay.TotalSeconds * 1_000_000_000.0 / tickResolutionNs);
            ulong futureTick = currentTick + delayTicks;

            if (!_futureUpdates.TryGetValue(futureTick, out var queue))
            {
                queue = new Queue<SignalUpdate>();
                _futureUpdates[futureTick] = queue;
            }

            queue.Enqueue(new SignalUpdate(target, newValue, delay));
        }
    }

    /// <summary>
    /// Applies all pending updates for the current delta cycle.
    /// </summary>
    /// <returns>Set of signals that actually changed value.</returns>
    public HashSet<Variable> ApplyCurrentUpdates()
    {
        HashSet<Variable> changedSignals = new();

        lock (_lock)
        {
            while (_currentDeltaUpdates.Count > 0)
            {
                var update = _currentDeltaUpdates.Dequeue();

                // Track old value for change detection
                object? oldValue = update.Target.Value;

                // Apply the update
                update.Target.Value = update.NewValue;

                // Check if value actually changed
                if (!Equals(oldValue, update.Target.Value))
                {
                    changedSignals.Add(update.Target);
                }
            }
        }

        return changedSignals;
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
