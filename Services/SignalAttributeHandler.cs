using Parser.Models;
using static Parser.Models.Constants;

namespace Interpreter.Services;

/// <summary>
/// Handles VHDL signal attributes like 'event, 'last_value, 'stable, 'active, etc.
///
/// Tier 4.1 (2026-07-28): `'last_value` and `'stable(T)` now use commit-time
/// granularity — <c>_previousValues[sig]</c> captures the value the signal held
/// immediately BEFORE its most recent transition (populated by
/// <see cref="OnDeltaCommit"/> during the scheduler commit), and
/// <c>_lastChangeMoments[sig]</c> stamps when. `'event`/`_activeFlags` retains
/// the pre-Tier-4.1 tick-scoped semantics driven by <c>Variable.OnValueChange</c>
/// — a strict per-delta rewrite regressed 100+ bench cases whose clocked processes
/// depend on the tick-persistent event flag; deferring that stricter semantic to
/// a follow-up arc gated on a targeted delta-scope bench case.
/// </summary>
public class SignalAttributeHandler
{
    // F-G-08 (2026-07-15): keyed by Variable reference (identity hash). String
    // keys did case-folding hash on every read/write and were a leading
    // contributor to O(N)-per-tick cost on the 196-signal capstone design.
    // Kept the name-lookup overloads (via _byName) for the EvaluateAttribute
    // path called by expression eval from source strings.
    /// <summary>Value the signal held immediately BEFORE its most recent transition (per-write granularity).</summary>
    private readonly Dictionary<Variable, object> _previousValues = new();
    /// <summary>Name → Variable index kept in sync so EvaluateAttribute-by-name still resolves.</summary>
    private readonly Dictionary<string, Variable> _byName = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Signals that had a value change since the last tick-boundary reset (tick-scoped).</summary>
    private readonly Dictionary<Variable, bool> _activeFlags = new();
    /// <summary>Timestamp (tick*1000+delta) of each signal's most recent transition, for `'stable(T)`.
    /// <c>long</c> so it survives runs past ~21 ms (tick*1000 overflows int at tick ≈ 2.147M).</summary>
    private readonly Dictionary<Variable, long> _lastChangeMoments = new();
    /// <summary>
    /// Tier 4.1 close-out: number of value transitions the signal has undergone in the
    /// current delta only. Populated by <see cref="OnDeltaCommit"/> and cleared at the
    /// top of the next delta by <see cref="BeginDelta"/>. Only consumed by the opt-in
    /// <see cref="HadEventThisDelta(Variable)"/> predicate — the default `'event` /
    /// <c>GetEvent</c> stays tick-scoped because process wake-up fanout in
    /// <c>DeltaCycleEngine.RunTimeStepAsync</c> and cross-delta re-reads of
    /// <c>rising_edge</c> depend on the flag surviving the whole tick.
    /// </summary>
    private readonly Dictionary<Variable, int> _deltaTransitionCount = new();
    /// <summary>Current commit timestamp, updated by <see cref="OnDeltaCommit"/>. Used by GetStable(T).
    /// <c>long</c> to match <see cref="_lastChangeMoments"/> — see the overflow note there.</summary>
    private long _currentTimeMoment;
    /// <summary>Shadow of each signal's last-known value — updated in OnValueChange so we can
    /// recover the pre-transition value even for stimulus writes that bypass SignalScheduler.</summary>
    private readonly Dictionary<Variable, object> _shadowValues = new();
    /// <summary>Tick resolution (ns per tick) used by <see cref="GetStable(Variable, long)"/> to
    /// scale a caller-supplied ns duration into the tick*1000+delta scale used internally.
    /// Set by <c>DeltaCycleEngine.SetContext</c>; defaults to 1 so unit-tests that don't
    /// touch a full engine still round-trip 1 ns per tick.</summary>
    private int _tickResolutionNs = 1;
    public int TickResolutionNs
    {
        get => _tickResolutionNs;
        set => _tickResolutionNs = Math.Max(1, value);
    }

    private static SignalAttributeHandler? _instance;
    public static SignalAttributeHandler Instance => _instance ??= new SignalAttributeHandler();

    /// <summary>
    /// Registers a signal for attribute tracking. Bug #18 fix: (re)seed
    /// per-signal state so a follow-up simulation in the same process doesn't
    /// inherit `_previousValues["clk"] = "1"` from the prior run.
    /// </summary>
    public void RegisterSignal(Variable signal)
    {
        _previousValues[signal] = signal.Value!;
        _shadowValues[signal] = signal.Value!;
        _activeFlags[signal] = false;
        _lastChangeMoments[signal] = 0;
        if (!string.IsNullOrEmpty(signal.Name))
            _byName[signal.Name] = signal;

        // Subscribe to value changes — fires from Variable.Value setter every
        // time the value is committed (whether via SignalScheduler or direct
        // stimulus write). Sets _activeFlags eagerly + rotates the shadow
        // into _previousValues so `'last_value` reflects the actual pre-
        // transition value regardless of which write path drove it.
        signal.OnValueChange += OnSignalValueChange;
    }

    /// <summary>
    /// Incremental hook fired from <c>Variable.Value</c>'s setter each time a
    /// signal is committed by <see cref="SignalScheduler.ApplyCurrentUpdatesWithLinesAndOldValues"/>
    /// OR a direct stimulus write in <c>SimulationRunner</c>. Rotates the shadow
    /// (previous last-known) into <c>_previousValues</c> and updates the
    /// tick-scoped <c>_activeFlags</c>. <see cref="OnDeltaCommit"/> additionally
    /// stamps <c>_lastChangeMoments</c> for `'stable(T)` — that path only fires
    /// for scheduler-driven writes but stimulus signals rarely carry a
    /// meaningful `'stable(T)` semantic anyway.
    /// </summary>
    private void OnSignalValueChange(Variable signal)
    {
        if (_shadowValues.TryGetValue(signal, out var oldVal) && !Equals(oldVal, signal.Value))
        {
            _previousValues[signal] = oldVal;
        }
        _shadowValues[signal] = signal.Value!;
        _activeFlags[signal] = true;
    }

    /// <summary>
    /// Tier 4.1: commits per-delta attribute state from the scheduler's
    /// snapshot. Called once per delta from <c>DeltaCycleEngine.RunDeltaCycleAsync</c>
    /// right after <c>ApplyCurrentUpdatesWithLinesAndOldValues</c>. Only signals
    /// that actually changed value in this delta appear in
    /// <paramref name="oldValues"/>, keyed by the value they held BEFORE the
    /// commit. Updates <c>_previousValues</c> and <c>_lastChangeMoments</c> at
    /// per-commit granularity so `'last_value` and `'stable(T)` return LRM-correct
    /// results. Does NOT touch <c>_activeFlags</c> — that's the OnValueChange path.
    /// </summary>
    /// <param name="oldValues">signal → value-immediately-before-this-delta.</param>
    /// <param name="timeMoment">tick*1000+delta (<c>long</c>, overflow-safe); same units as SignalHistory.</param>
    public void OnDeltaCommit(IReadOnlyDictionary<Variable, object?> oldValues, long timeMoment)
    {
        _currentTimeMoment = timeMoment;
        foreach (var (sig, oldVal) in oldValues)
        {
            if (oldVal is not null) _previousValues[sig] = oldVal;
            _lastChangeMoments[sig] = timeMoment;
            // Bump the per-delta counter so `HadEventThisDelta` can catch
            // 0→1→0-within-a-tick pathological cases that collapse under the
            // tick-scoped `_activeFlags`.
            _deltaTransitionCount.TryGetValue(sig, out int c);
            _deltaTransitionCount[sig] = c + 1;
        }
    }

    /// <summary>
    /// Tier 4.1 close-out: clears the per-delta transition counters. Called from
    /// <c>DeltaCycleEngine.RunDeltaCycleAsync</c> at the top of every delta so
    /// <see cref="HadEventThisDelta"/> reads reflect only the current delta.
    /// The tick-scoped <c>_activeFlags</c> are untouched — they're cleared by
    /// <see cref="ClearActiveFlagsAtTickBoundary"/> at end-of-tick.
    /// </summary>
    public void BeginDelta()
    {
        _deltaTransitionCount.Clear();
    }

    /// <summary>
    /// Tier 4.1 close-out: stamp `_lastChangeMoments` for signals whose values
    /// changed via a direct stimulus write (clocks, external ports). Those
    /// writes route through <see cref="OnSignalValueChange"/> and flip
    /// <c>_activeFlags</c> but bypass <see cref="OnDeltaCommit"/>, so without
    /// this hook `'stable(T)` never sees the transition. Called from
    /// <c>DeltaCycleEngine.RunTimeStepAsync</c> once per tick, after
    /// <c>AdvanceTime</c>, with the set of stimulus-changed signals.
    /// </summary>
    public void StampStimulusChanges(IEnumerable<Variable> signals, long timeMoment)
    {
        _currentTimeMoment = timeMoment;
        foreach (var sig in signals)
        {
            _lastChangeMoments[sig] = timeMoment;
        }
    }

    /// <summary>
    /// Opt-in per-delta `'event` predicate for the pathological 0→1→0-within-a-tick
    /// case. Returns true iff <paramref name="signal"/> transitioned at least once
    /// in the delta just committed. Consumers must call this AFTER
    /// <see cref="OnDeltaCommit"/> in the same delta or the count reads zero.
    /// The default <see cref="GetEvent(Variable)"/> stays tick-scoped for the
    /// reasons documented on <c>_deltaTransitionCount</c>.
    /// </summary>
    public bool HadEventThisDelta(Variable signal) =>
        _deltaTransitionCount.TryGetValue(signal, out int c) && c > 0;

    /// <summary>
    /// Tick-boundary reset: clears the per-tick <c>_activeFlags</c> so the
    /// next tick's clocked-process wake sees the flag settle at false before
    /// the next event fires. Called from <c>DeltaCycleEngine.RunTimeStepAsync</c>
    /// end-of-tick.
    /// </summary>
    public void ClearActiveFlagsAtTickBoundary(HashSet<Variable> signals)
    {
        foreach (var signal in signals)
        {
            _activeFlags[signal] = false;
        }
    }

    /// <summary>signal'event — true iff signal changed value in the current tick's active window.</summary>
    public bool GetEvent(Variable signal) =>
        _activeFlags.TryGetValue(signal, out var active) && active;

    /// <summary>Name-based path used by <see cref="EvaluateAttribute"/>.</summary>
    public bool GetEvent(string signalName) =>
        _byName.TryGetValue(signalName, out var s) && GetEvent(s);

    /// <summary>
    /// signal'last_value — the value the signal held immediately before its
    /// most recent transition (per-commit granularity as of Tier 4.1).
    /// Falls back to current value if the signal has never transitioned.
    /// </summary>
    public object GetLastValue(Variable signal) =>
        _previousValues.TryGetValue(signal, out var v) ? v : signal.Value!;

    public object GetLastValue(string signalName) =>
        _byName.TryGetValue(signalName, out var s) && _previousValues.TryGetValue(s, out var v) ? v : "U";

    /// <summary>signal'stable — no-arg form: true iff no `'event` right now.</summary>
    public bool GetStable(Variable signal) => !GetEvent(signal);

    /// <summary>
    /// Tier 4.1: signal'stable(T) — true iff the signal has NOT changed for
    /// at least <paramref name="elapsedNs"/> ns. The comparison happens in the
    /// tick*1000+delta scale used by <see cref="OnDeltaCommit"/>, so the ns
    /// argument is scaled by <see cref="TickResolutionNs"/> here — Tier 4.1
    /// close-out (non-1 ns bench resolutions were previously off by that
    /// factor because callers pre-scaled with a `ns == tick` assumption).
    /// </summary>
    public bool GetStable(Variable signal, long elapsedNs)
    {
        long last = _lastChangeMoments.GetValueOrDefault(signal, 0L);
        long elapsedRawUnits = (elapsedNs * 1000L) / Math.Max(1, _tickResolutionNs);
        return (_currentTimeMoment - last) >= elapsedRawUnits;
    }

    /// <summary>signal'active — treated as an alias for 'event today (no driver-transaction model yet).</summary>
    public bool GetActive(Variable signal) => GetEvent(signal);

    /// <summary>rising_edge shorthand: 'event AND value == '1'.</summary>
    public bool IsRisingEdge(Variable signal)
    {
        if (!GetEvent(signal)) return false;
        string v = signal.Value?.ToString()?.Replace("'", "").Replace("\"", "") ?? "";
        return v == "1";
    }

    /// <summary>falling_edge shorthand: 'event AND value == '0'.</summary>
    public bool IsFallingEdge(Variable signal)
    {
        if (!GetEvent(signal)) return false;
        string v = signal.Value?.ToString()?.Replace("'", "").Replace("\"", "") ?? "";
        return v == "0";
    }

    /// <summary>Clears per-simulation state. Called from <c>DeltaCycleEngine.Reset</c>.</summary>
    public void Reset()
    {
        _previousValues.Clear();
        _shadowValues.Clear();
        _activeFlags.Clear();
        _lastChangeMoments.Clear();
        _deltaTransitionCount.Clear();
        _byName.Clear();
        _currentTimeMoment = 0;
        _tickResolutionNs = 1;
    }

    /// <summary>Name-based dispatch used by <see cref="ParseAttributeExpression"/>.</summary>
    public object EvaluateAttribute(string signalName, string attributeName, HashSet<Variable> variables)
    {
        var signal = variables.FirstOrDefault(v => v.Name.Equals(signalName, StringComparison.OrdinalIgnoreCase))
                     ?? throw new InvalidOperationException($"Signal '{signalName}' not found for attribute evaluation");
        return attributeName.ToLower() switch
        {
            "event"      => GetEvent(signal),
            "last_value" => GetLastValue(signal),
            "stable"     => GetStable(signal),
            "active"     => GetActive(signal),
            _            => throw new NotSupportedException($"Signal attribute '{attributeName}' is not supported")
        };
    }

    /// <summary>
    /// Parses and evaluates a source-string signal attribute expression like
    /// "clk'event". Kept for legacy paths that arrive with raw strings; the
    /// engine's hot loop now goes through the identity-keyed API above.
    /// </summary>
    public (bool isAttribute, string signalName, string attributeName, object value) ParseAttributeExpression(
        string expression, HashSet<Variable> variables)
    {
        if (!expression.Contains("'")) return (false, "", "", null!);
        int apostropheIndex = expression.IndexOf("'");
        if (apostropheIndex == 0) return (false, "", "", null!);

        string signalName = expression[..apostropheIndex].Trim();
        string attributeName = expression[(apostropheIndex + 1)..].Trim();
        string[] validAttributes = ["event", "last_value", "stable", "active", "left", "right", "high", "low", "length", "range"];
        string baseAttribute = attributeName.Split(' ', '(', ')', '=')[0].ToLower();
        if (!validAttributes.Contains(baseAttribute)) return (false, "", "", null!);

        try
        {
            var value = EvaluateAttribute(signalName, baseAttribute, variables);
            return (true, signalName, baseAttribute, value);
        }
        catch
        {
            return (false, "", "", null!);
        }
    }
}
