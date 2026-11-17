using Interpreter.Interfaces;
using Interpreter.Repositories;
using Parser.Models;

namespace Interpreter.Services;

/// <summary>
/// Coordinates VHDL delta cycle simulation.
/// Manages the execution of concurrent statements with proper signal scheduling.
/// </summary>
/// <remarks>
/// <para>Delta cycle semantics:</para>
/// <list type="bullet">
/// <item>All signal assignments within a delta cycle are scheduled, not immediate</item>
/// <item>At the end of each delta cycle, all scheduled updates are applied simultaneously</item>
/// <item>If any signal changes, processes sensitive to those signals are re-executed</item>
/// <item>This continues until no more signals change (simulation stabilizes)</item>
/// <item>Only then does simulation time advance</item>
/// </list>
/// </remarks>
public class DeltaCycleEngine : IDisposable
{
    /// <summary>
    /// Signal update scheduling.
    /// </summary>
    private readonly SignalScheduler _signalScheduler = new();

    /// <summary>
    /// Process management.
    /// </summary>
    private readonly ProcessScheduler _processScheduler = new();

    /// <summary>
    /// Signal history for waveform recording.
    /// </summary>
    private readonly SignalHistory _signalHistory = new();

    /// <summary>
    /// All signals/variables in the design.
    /// </summary>
    private HashSet<Variable> _allSignals = new();

    /// <summary>
    /// All concurrent tasks (processes, concurrent assignments).
    /// </summary>
    private List<object> _concurrentTasks = new();

    /// <summary>
    /// v0.17.0: Cache for ScheduledOperation wrappers to avoid repeated allocations.
    /// Key: Original Operation, Value: ScheduledOperation wrapper
    /// </summary>
    private readonly Dictionary<Operation, ScheduledOperation> _scheduledOpCache = new();

    /// <summary>
    /// Phase H.3: per-Operation cache of input-signal sets. Concurrent
    /// assignments re-evaluate only when one of their inputs changed in
    /// the previous delta cycle, so we look this up once per task and
    /// intersect with [[_lastDeltaChangedSignals]]. A null entry means
    /// "inputs not statically determinable" (e.g. the Operation reads a
    /// DeferredFunction whose argument list may include arbitrary live
    /// signals) — those run unconditionally.
    /// </summary>
    private readonly Dictionary<Operation, HashSet<Variable>?> _operationInputs = new();

    /// <summary>
    /// Phase H.3: signals that committed new values in the most recent
    /// delta cycle. Drives the concurrent-task gating: a clean task whose
    /// inputs aren't in this set is skipped this delta. Reset to "run all"
    /// at every time-step boundary so external-stimulus changes propagate.
    /// </summary>
    private HashSet<Variable>? _lastDeltaChangedSignals = null;

    /// <summary>
    /// F-G-08 pass 2 (2026-07-16): reverse index from Variable → concurrent
    /// Operations that read it. Built once in <see cref="Initialize"/> from
    /// the static input scan performed by <see cref="GetOperationInputs"/>.
    /// Reference-keyed by Variable (single-instance-per-signal invariant is
    /// guaranteed by the parser; see project memory).
    /// Replaces the previous per-task <c>inputs.Overlaps(changed)</c> loop
    /// (O(#tasks × avg-inputs)) with a per-changed-signal fanout walk
    /// (O(|changed| × avg-fanout)).
    /// </summary>
    private readonly Dictionary<Variable, List<Operation>> _signalToOps = new();

    /// <summary>
    /// F-G-08 pass 2: Operations whose static input set couldn't be resolved
    /// (DeferredFunction / expression-mode DynamicIndex). Always enqueued on
    /// every non-first delta — matches the pre-index <c>inputs == null</c>
    /// unconditional-run guard.
    /// </summary>
    private readonly List<Operation> _alwaysRunOps = new();

    /// <summary>
    /// F-G-08 pass 2: per-delta scratch set of Operations to execute. Cleared
    /// and refilled from <see cref="_signalToOps"/> + <see cref="_alwaysRunOps"/>
    /// at the top of each gated delta. Reused across deltas to dodge the
    /// per-delta allocation.
    /// </summary>
    private readonly HashSet<Operation> _pendingOpsScratch = new();

    /// <summary>
    /// F-G-13 MVP (2026-07-18): time-scheduled process wakes. Populated at
    /// Initialize (tick 0 for every process containing `wait`) and each
    /// time a WaitForStatement yields (current tick + delay ticks).
    /// Consumed at the start of <see cref="RunTimeStepAsync"/>: any
    /// processes with keys &lt;= current tick are executed from their PC
    /// until the next wait or end of body. Legacy sensitivity-driven
    /// processes (without wait) are unaffected — they stay on
    /// <see cref="_processScheduler"/>'s queue.
    /// </summary>
    private readonly SortedDictionary<ulong, List<Process>> _pendingProcessWakes = new();

    /// <summary>
    /// F-G-13b (Session 23, 2026-07-18): condition-blocked processes. When
    /// a <c>wait until &lt;cond&gt;</c> executes, the process is registered
    /// under each signal in the wait's <see cref="Interpreter.Repositories.WaitForStatement.Sensitivity"/>
    /// set. After every delta-cycle signal-change burst, the engine walks
    /// the intersection of changed signals × this map's keys and
    /// re-evaluates each blocked process's stored condition; on true the
    /// process is dequeued and rescheduled for the current tick via
    /// <see cref="ScheduleProcessWake"/>.
    ///
    /// The tuple carries the WaitForStatement so <c>EvaluateCondition</c>
    /// can be called back to the actual condition, and so removal on wake
    /// removes only entries for THIS wait (not other waits by the same
    /// process, which don't co-exist).
    /// </summary>
    private readonly Dictionary<Variable, List<(Process process, Interpreter.Repositories.WaitForStatement wait)>>
        _conditionBlocked = new();

    /// <summary>
    /// F-G-13 MVP: queue a wait-driven process for execution at a specific
    /// tick. Called from Initialize (tick 0) and from
    /// <see cref="ExecuteProcessSegmentAsync"/> when a WaitForStatement
    /// yields.
    /// </summary>
    private void ScheduleProcessWake(Process process, ulong atTick)
    {
        if (!_pendingProcessWakes.TryGetValue(atTick, out var list))
        {
            list = new List<Process>();
            _pendingProcessWakes[atTick] = list;
        }
        list.Add(process);
    }

    /// <summary>
    /// Read-only: the earliest tick at which a wait-driven process is queued to
    /// wake, or <c>null</c> when none are pending. Mirrors
    /// <see cref="SignalScheduler.NextScheduledTick"/> so an event-driven driver
    /// (e.g. the fast runner) can jump straight to the next interesting tick
    /// without skipping a <c>wait for</c>. Pure accessor — advances nothing and
    /// changes no simulation state, so engine behaviour/output is unaffected.
    /// </summary>
    public ulong? NextProcessWakeTick =>
        _pendingProcessWakes.Count > 0 ? _pendingProcessWakes.Keys.First() : (ulong?)null;

    /// <summary>
    /// F-G-13b: register a condition-blocked process under each signal in
    /// the wait's sensitivity set.
    /// </summary>
    private void RegisterConditionBlocked(Process process, Interpreter.Repositories.WaitForStatement wait)
    {
        foreach (var sig in wait.Sensitivity)
        {
            if (!_conditionBlocked.TryGetValue(sig, out var list))
            {
                list = new List<(Process, Interpreter.Repositories.WaitForStatement)>();
                _conditionBlocked[sig] = list;
            }
            list.Add((process, wait));
        }
    }

    /// <summary>
    /// F-G-13b: drop all condition-blocked entries for the given (process,
    /// wait) pair — called on wake so a resumed process isn't re-woken by
    /// the same signal change on subsequent deltas within the same tick.
    /// </summary>
    private void UnregisterConditionBlocked(Process process, Interpreter.Repositories.WaitForStatement wait)
    {
        foreach (var sig in wait.Sensitivity)
        {
            if (_conditionBlocked.TryGetValue(sig, out var list))
            {
                list.RemoveAll(entry => ReferenceEquals(entry.process, process)
                                      && ReferenceEquals(entry.wait, wait));
                if (list.Count == 0) _conditionBlocked.Remove(sig);
            }
        }
    }

    /// <summary>
    /// F-G-13b: re-evaluate every condition-blocked process whose
    /// sensitivity set intersects <paramref name="changedSignals"/>. Any
    /// process whose condition now holds is dequeued from
    /// <see cref="_conditionBlocked"/> and rescheduled at the current tick
    /// via <see cref="ScheduleProcessWake"/>.
    /// </summary>
    private void WakeConditionBlockedProcesses(HashSet<Variable> changedSignals)
    {
        if (_conditionBlocked.Count == 0 || changedSignals.Count == 0) return;

        List<(Process, Interpreter.Repositories.WaitForStatement)>? toWake = null;
        foreach (var sig in changedSignals)
        {
            if (!_conditionBlocked.TryGetValue(sig, out var list)) continue;
            foreach (var entry in list)
            {
                if (entry.process.IsTerminated) continue;
                if (!entry.wait.EvaluateCondition()) continue;
                toWake ??= new List<(Process, Interpreter.Repositories.WaitForStatement)>();
                if (!toWake.Any(t => ReferenceEquals(t.Item1, entry.process)
                                  && ReferenceEquals(t.Item2, entry.wait)))
                    toWake.Add((entry.process, entry.wait));
            }
        }
        if (toWake == null) return;
        foreach (var (p, w) in toWake)
        {
            UnregisterConditionBlocked(p, w);
            ScheduleProcessWake(p, _currentTick);
        }
    }

    /// <summary>
    /// F-G-08 pass 3 diag (2026-07-16): when true, hitting
    /// <see cref="MAX_DELTA_CYCLES"/> in a time step breaks the delta loop
    /// instead of throwing — so a run past the first cap can be observed
    /// end-to-end for stats. Opt-in via <c>KMILA_SIM_TRUNCATE_DELTA_CAP=1</c>.
    /// Diagnostic only: real combinational loops would silently truncate,
    /// producing outputs that may diverge from a stable simulation.
    /// </summary>
    private bool _truncateOnDeltaCap = false;

    /// <summary>F-G-08 pass 3 diag: total <see cref="RunTimeStepAsync"/> calls (== ticks driven).</summary>
    public long TotalTimeSteps { get; private set; } = 0;
    /// <summary>F-G-08 pass 3 diag: cumulative delta cycles across all ticks.</summary>
    public long TotalDeltaCycles { get; private set; } = 0;
    /// <summary>F-G-08 pass 3 diag: ticks that hit <see cref="MAX_DELTA_CYCLES"/> (truncated or threw).</summary>
    public long CapHitTicks { get; private set; } = 0;
    /// <summary>F-G-08 pass 3 diag: high-water mark of deltas observed in a single tick.</summary>
    public int MaxDeltasInAnyTick { get; private set; } = 0;
    /// <summary>F-G-08 pass 3 diag: 1-based tick index of the first cap hit (0 = no cap hit yet).</summary>
    public ulong FirstCapHitTick { get; private set; } = 0;
    /// <summary>F-G-08 pass 3 diag: elaboration-time size of <see cref="_concurrentTasks"/>.</summary>
    public int InitialConcurrentTaskCount { get; private set; } = 0;
    /// <summary>F-G-08 pass 3 diag: elaboration-time size of <see cref="_alwaysRunOps"/>.</summary>
    public int InitialAlwaysRunOpsCount { get; private set; } = 0;
    /// <summary>F-G-08 pass 3 diag: elaboration-time count of unique Variable keys in <see cref="_signalToOps"/>.</summary>
    public int InitialSignalToOpsKeys { get; private set; } = 0;
    /// <summary>F-G-08 pass 3 diag: elaboration-time signal count (from <see cref="_allSignals"/>).</summary>
    public int InitialSignalCount { get; private set; } = 0;

    /// <summary>
    /// F-G-08 pass 3 diag: if &gt; 0, emit an stderr progress line every N
    /// deltas from inside <see cref="RunDeltaCycleAsync"/>. Set via
    /// <c>KMILA_SIM_PROGRESS_EVERY_N_DELTAS</c>. 0 = disabled.
    /// </summary>
    private int _progressEveryNDeltas = 0;
    private System.Diagnostics.Stopwatch _diagStopwatch = new();

    /// <summary>
    /// F-G-08 pass 3 diag (2026-07-16): one-line summary of the engine's
    /// perf-relevant counters. Consumed by the headless-simulate API to
    /// attach a diagnostic to every response.
    /// </summary>
    public string GetDiagnosticSummary()
    {
        double avgDeltas = TotalTimeSteps > 0 ? (double)TotalDeltaCycles / TotalTimeSteps : 0.0;
        return $"ticks={TotalTimeSteps} deltas={TotalDeltaCycles} " +
               $"avgDeltas/tick={avgDeltas:F1} maxDeltas/tick={MaxDeltasInAnyTick} " +
               $"capHitTicks={CapHitTicks} firstCapHit={FirstCapHitTick} " +
               $"initSignals={InitialSignalCount} initTasks={InitialConcurrentTaskCount} " +
               $"initAlwaysRunOps={InitialAlwaysRunOpsCount} initReverseIdxKeys={InitialSignalToOpsKeys} " +
               $"truncateMode={_truncateOnDeltaCap}";
    }

    /// <summary>
    /// Current simulation tick.
    /// </summary>
    private ulong _currentTick = 0;

    /// <summary>
    /// Current delta cycle within the current tick.
    /// </summary>
    private int _currentDelta = 0;

    /// <summary>
    /// Maximum delta cycles before aborting (prevents infinite loops).
    /// </summary>
    private const int MAX_DELTA_CYCLES = 1000;

    /// <summary>
    /// Pause/resume control.
    /// </summary>
    private volatile bool _isRunning = true;

    /// <summary>
    /// Cancellation token source for stopping simulation.
    /// </summary>
    private CancellationTokenSource _cts = new();

    /// <summary>
    /// Cancellation token source for pause handling.
    /// </summary>
    private CancellationTokenSource _pauseCts = new();

    /// <summary>
    /// Whether the engine has been initialized.
    /// </summary>
    private bool _isInitialized = false;

    /// <summary>
    /// Event fired when a delta cycle completes.
    /// Parameters: tick, delta.
    /// </summary>
    public event Action<ulong, int>? OnDeltaCycleComplete;

    /// <summary>
    /// Event fired when a signal changes value.
    /// Parameters: signal, oldValue, newValue.
    /// </summary>
    public event Action<Variable, object, object>? OnSignalChange;

    /// <summary>
    /// Event fired when simulation stabilizes at a time point.
    /// </summary>
    public event Action? OnSimulationStable;

    /// <summary>
    /// Event fired when time advances.
    /// Parameter: new tick.
    /// </summary>
    public event Action<ulong>? OnTimeAdvance;

    /// <summary>
    /// Phase B (2026-05-19): live-replay frame event. Fires once per delta
    /// cycle with the tick, delta number, and the set of signals whose value
    /// just settled. Distinct from <see cref="OnDeltaCycleComplete"/> in that
    /// it carries the changed-signal payload directly — the UI side doesn't
    /// have to re-query the scheduler. Subscribers should treat the
    /// collection as immutable and snapshot what they need immediately.
    /// </summary>
    public event Action<FrameEmittedArgs>? OnFrameEmitted;

    /// <summary>
    /// #5 (2026-05-27): fires when a non-sequential branch is taken at
    /// runtime — an <c>if</c>/<c>elsif</c> condition that evaluated true, or a
    /// <c>case</c> <c>when</c> arm that matched — carrying the 1-based source
    /// line of that condition/arm. Lets the live-replay layer pause on a
    /// breakpoint set on a condition line, which the signal-transition path
    /// (<see cref="OnFrameEmitted"/>) can't catch because no signal changes on
    /// the condition itself. Zero line means the parser didn't capture it.
    /// </summary>
    public event Action<int>? OnConditionEvaluated;

    /// <summary>
    /// True while the engine is in cooperative-paused state. Distinct from
    /// <see cref="IsRunning"/>'s legacy semantics — exposed so the UI can
    /// reflect pause state without re-deriving it from the timer.
    /// </summary>
    public bool IsPaused => !_isRunning;

    /// <summary>
    /// True while the simulation loop is alive (engine constructed + not yet
    /// stopped). Mainly useful for UI bindings that want to gray out the
    /// transport when no run is active.
    /// </summary>
    public bool IsRunning => _isRunning && !_cts.IsCancellationRequested;

    /// <summary>
    /// Gets the signal scheduler for external access.
    /// </summary>
    public SignalScheduler SignalScheduler => _signalScheduler;

    /// <summary>
    /// Tick resolution in nanoseconds. Forwarded to `SignalScheduler` so
    /// `after N ns` assignments convert to the right future-tick offset.
    /// Default 10 (matching `TimeClock.NS_PER_TICK`); update before
    /// running for sub-10ns sims. Bench bug #8.
    /// </summary>
    public int TickResolutionNs
    {
        get => _signalScheduler.TickResolutionNs;
        set
        {
            _signalScheduler.TickResolutionNs = value;
            // Keep the attribute handler's copy in step so `'stable(T ns)`
            // scales correctly regardless of when the caller sets tickRes.
            SignalAttributeHandler.Instance.TickResolutionNs = value;
        }
    }

    /// <summary>
    /// Gets the process scheduler for external access.
    /// </summary>
    public ProcessScheduler ProcessScheduler => _processScheduler;

    /// <summary>
    /// Gets the signal history for waveform analysis.
    /// </summary>
    public SignalHistory History => _signalHistory;

    /// <summary>
    /// Gets the current simulation tick.
    /// </summary>
    public ulong CurrentTick => _currentTick;

    /// <summary>
    /// Gets the current delta cycle.
    /// </summary>
    public int CurrentDelta => _currentDelta;

    /// <summary>
    /// v0.17.0: Gets or creates a cached ScheduledOperation wrapper for an Operation.
    /// This avoids repeated allocations during delta cycles.
    /// </summary>
    private ScheduledOperation GetScheduledOperation(Operation operation)
    {
        if (!_scheduledOpCache.TryGetValue(operation, out var scheduledOp))
        {
            scheduledOp = new ScheduledOperation(operation, _signalScheduler);
            _scheduledOpCache[operation] = scheduledOp;
        }
        return scheduledOp;
    }

    /// <summary>
    /// Phase H.3: returns the set of Variables an Operation reads, computed
    /// once and cached. A null return means "inputs aren't statically
    /// known" (e.g. the Operation holds a DeferredFunction that may invoke
    /// a user function reading arbitrary signals) — caller must run the
    /// operation unconditionally in that case.
    /// </summary>
    private HashSet<Variable>? GetOperationInputs(Operation op)
    {
        if (_operationInputs.TryGetValue(op, out var cached)) return cached;
        var inputs = new HashSet<Variable>();
        bool dynamic = false;
        foreach (var item in op.Items)
        {
            switch (item)
            {
                case Variable v:
                    if (!string.IsNullOrEmpty(v.Name)) inputs.Add(v);
                    break;
                case Models.DeferredIndex di:
                    if (di.Source is not null) inputs.Add(di.Source);
                    break;
                case Models.DynamicIndex dyni:
                    if (dyni.Source is not null) inputs.Add(dyni.Source);
                    if (dyni.Index.Source is not null) inputs.Add(dyni.Index.Source);
                    // Expression-mode DynamicIndex may read any signal in
                    // its scope. Mark dynamic so we run unconditionally.
                    if (dyni.Index.Expression != null) dynamic = true;
                    break;
                case Models.IDeferredOperand deferred:
                    if (deferred.Inputs is { } reads) inputs.UnionWith(reads);
                    else dynamic = true;
                    break;
                case Models.DeferredFunction:
                    // User / deferred function args reach into call-time
                    // state. Treat as dynamic to be safe.
                    dynamic = true;
                    break;
                case Models.DeferredUserFunction:
                    // Tier 4.3 exposure (2026-07-28): concurrent assigns of
                    // shape `y <= userFn(a,b);` had no _signalToOps entry
                    // because the DeferredUserFunction was silently skipped
                    // by this switch — `y` never updated when a/b changed
                    // (the arg-string signals are hidden inside the wrapper
                    // and aren't emitted as Variable items). Treat as dynamic;
                    // args are re-parsed against live scope on every Evaluate.
                    dynamic = true;
                    break;
                case Models.DeferredAttribute:
                    // Tier 4.1 close-out companion: same shape — the attr's
                    // handler reads live SignalAttributeHandler state on every
                    // Execute, so the enclosing op must run every delta.
                    dynamic = true;
                    break;
            }
            if (dynamic) break;
        }
        HashSet<Variable>? result = dynamic ? null : inputs;
        _operationInputs[op] = result;
        return result;
    }

    /// <summary>
    /// Initializes the engine with signals and concurrent tasks.
    /// </summary>
    /// <param name="signals">All signals in the design.</param>
    /// <param name="tasks">All concurrent tasks (processes, assignments).</param>
    public void Initialize(HashSet<Variable> signals, List<object> tasks)
    {
        _allSignals = signals;
        _concurrentTasks = tasks;

        // Register all signals with the attribute handler
        var handler = SignalAttributeHandler.Instance;
        // Tier 4.1 close-out: propagate tick resolution so `'stable(T ns)`
        // scales correctly at non-1 ns tickResNs (bench default is 10 ns).
        handler.TickResolutionNs = _signalScheduler.TickResolutionNs;
        foreach (var signal in signals)
        {
            handler.RegisterSignal(signal);
        }

        // F-G-08 (2026-07-15): opt-out of history recording via env var to
        // dodge lock-per-signal-change overhead on large designs. Behavioural
        // verification via `report` doesn't need waveform history; VCD
        // consumers do. Default = record (backward compat).
        var disableHistory = Environment.GetEnvironmentVariable("KMILA_SIM_NO_HISTORY") == "1";
        _signalHistory.IsRecording = !disableHistory;

        // F-G-08 pass 3 diag (2026-07-16): opt-in truncate-on-cap mode for
        // measuring delta-cap behaviour past the first hit.
        _truncateOnDeltaCap = Environment.GetEnvironmentVariable("KMILA_SIM_TRUNCATE_DELTA_CAP") == "1";

        var progEnv = Environment.GetEnvironmentVariable("KMILA_SIM_PROGRESS_EVERY_N_DELTAS");
        if (int.TryParse(progEnv, out int n) && n > 0)
        {
            _progressEveryNDeltas = n;
            _diagStopwatch.Restart();
        }

        // Register all signals with the history recorder
        _signalHistory.RegisterSignals(signals, initialTime: 0, recordInitial: true);

        // Register processes. F-G-13 MVP (2026-07-18): time-driven
        // processes (with any `wait` statement) skip the sensitivity-list
        // scheduler and instead get an initial resume queued for tick 0
        // via _pendingProcessWakes. Combinational / sensitivity-driven
        // processes stay on the legacy path via ProcessScheduler.
        foreach (var task in tasks)
        {
            if (task is Process process)
            {
                if (process.HasWait)
                {
                    ScheduleProcessWake(process, atTick: 0);
                }
                else
                {
                    _processScheduler.RegisterProcess(process);
                }
            }
        }

        // F-G-08 pass 2 (2026-07-16): pre-build the reverse signal→ops index
        // so RunDeltaCycleAsync can walk from the delta's changed set to the
        // dependent Operations directly, instead of intersecting the changed
        // set against every task's inputs. Also warms _operationInputs.
        _signalToOps.Clear();
        _alwaysRunOps.Clear();
        foreach (var task in tasks)
        {
            if (task is not Operation op) continue;
            var inputs = GetOperationInputs(op);
            if (inputs == null)
            {
                // Dynamic — must run every delta.
                _alwaysRunOps.Add(op);
                continue;
            }
            if (inputs.Count == 0)
            {
                // Constant RHS — only the first-delta runAll pass fires it.
                // Matches the pre-index `inputs.Count == 0 → continue` short-circuit.
                continue;
            }
            foreach (var v in inputs)
            {
                if (!_signalToOps.TryGetValue(v, out var list))
                {
                    list = new List<Operation>();
                    _signalToOps[v] = list;
                }
                list.Add(op);
            }
        }

        // F-G-08 pass 3 diag: snapshot elaboration-time stats for the response log.
        InitialConcurrentTaskCount = tasks.Count;
        InitialAlwaysRunOpsCount   = _alwaysRunOps.Count;
        InitialSignalToOpsKeys     = _signalToOps.Count;
        InitialSignalCount         = signals.Count;

        _isInitialized = true;
    }

    /// <summary>
    /// Runs simulation for one time step (all delta cycles until stable).
    /// </summary>
    public async Task RunTimeStepAsync()
    {
        if (!_isInitialized)
        {
            throw new InvalidOperationException("DeltaCycleEngine must be initialized before running");
        }

        _currentDelta = 0;

        // F-G-13 MVP (2026-07-18): drain any wait-driven processes ready
        // to wake at this tick. Their signal writes enter SignalScheduler
        // and propagate through the delta cycles below just like any
        // other write.
        await DrainReadyProcessWakesAsync();

        // For the first time step, initialize all processes
        if (_currentTick == 0 && _currentDelta == 0)
        {
            _processScheduler.InitializeAllProcesses();
            // Phase H.3: clean slate — run every concurrent task on the
            // first delta so initial values propagate.
            _lastDeltaChangedSignals = null;
        }
        else
        {
            // v0.17.0: Detect signals that had events from external changes (e.g., clock toggling)
            // and trigger processes sensitive to those signals
            var handler = SignalAttributeHandler.Instance;
            var changedSignals = new HashSet<Variable>();

            foreach (var signal in _allSignals)
            {
                if (handler.GetEvent(signal))
                {
                    changedSignals.Add(signal);
                }
            }

            if (changedSignals.Count > 0)
            {
                _processScheduler.NotifySignalChanges(changedSignals);
                // Tier 4.1 close-out: stamp `_lastChangeMoments` for
                // stimulus-driven writes so `'stable(T)` reads correctly.
                // OnDeltaCommit only covers scheduler-driven writes; stimulus
                // signals (clocks, external ports) bypass that path but flip
                // `_activeFlags` via OnValueChange. Attribute the change to
                // the tick just entered (delta 0).
                long stimulusTimeMoment = (long)_currentTick * 1000;
                handler.StampStimulusChanges(changedSignals, stimulusTimeMoment);
            }

            // Phase H.3: external-stimulus changes count toward the next
            // delta's gating set so concurrent tasks sensitive to them
            // fire. Union with whatever was pending from the last tick's
            // final delta.
            if (_lastDeltaChangedSignals == null)
            {
                _lastDeltaChangedSignals = changedSignals;
            }
            else
            {
                foreach (var s in changedSignals) _lastDeltaChangedSignals.Add(s);
            }
        }

        // Run delta cycles until stable
        bool hasMoreWork;
        bool capHitThisTick = false;
        do
        {
            hasMoreWork = await RunDeltaCycleAsync();
            _currentDelta++;

            // Safety check for infinite loops
            if (_currentDelta > MAX_DELTA_CYCLES)
            {
                capHitThisTick = true;
                if (_truncateOnDeltaCap)
                {
                    // F-G-08 pass 3 diag: silently break so subsequent ticks still run.
                    break;
                }
                throw new InvalidOperationException(
                    $"Delta cycle limit ({MAX_DELTA_CYCLES}) exceeded at tick {_currentTick}. " +
                    "Possible combinational loop in design.");
            }
        }
        while (hasMoreWork && _isRunning && !_cts.IsCancellationRequested);

        // F-G-08 pass 3 diag (2026-07-16): bump counters after the tick settles.
        TotalTimeSteps++;
        TotalDeltaCycles += _currentDelta;
        if (_currentDelta > MaxDeltasInAnyTick) MaxDeltasInAnyTick = _currentDelta;
        if (capHitThisTick)
        {
            CapHitTicks++;
            if (FirstCapHitTick == 0) FirstCapHitTick = _currentTick + 1;
        }

        // Simulation is stable at this time point
        OnSimulationStable?.Invoke();

        // Tier 4.1: previous-value / last-change-moment updates moved into
        // SignalAttributeHandler.OnDeltaCommit (per-delta, in RunDeltaCycleAsync).
        // The tick boundary still needs to clear _activeFlags so the next
        // tick's OnValueChange-driven events start from a clean slate.
        SignalAttributeHandler.Instance.ClearActiveFlagsAtTickBoundary(_allSignals);
    }

    /// <summary>
    /// Runs a single delta cycle.
    /// </summary>
    /// <returns>True if more delta cycles are needed.</returns>
    private async Task<bool> RunDeltaCycleAsync()
    {
        // F-G-08 pass 3 diag (2026-07-16): emit a live-progress line to stderr
        // every N deltas so a stuck first-tick sim is still observable.
        if (_progressEveryNDeltas > 0 &&
            (TotalDeltaCycles + _currentDelta) % _progressEveryNDeltas == 0)
        {
            Console.Error.WriteLine(
                $"[kmila-diag t={_diagStopwatch.ElapsedMilliseconds}ms] " +
                $"tick={_currentTick} delta={_currentDelta} " +
                $"cumDeltas={TotalDeltaCycles + _currentDelta} " +
                $"maxDeltas/tick={MaxDeltasInAnyTick} capHits={CapHitTicks}");
        }

        // Tier 4.1 close-out: clear per-delta `'event` counters at the top of
        // every delta so `HadEventThisDelta` reads reflect only this delta's
        // transitions. Tick-scoped `_activeFlags` are untouched — end-of-tick
        // clear stays in `ClearActiveFlagsAtTickBoundary`.
        SignalAttributeHandler.Instance.BeginDelta();

        // 1. Get pending processes
        var pendingProcesses = _processScheduler.GetPendingProcesses().ToList();

        // 2. Execute all pending processes with scheduled assignments
        foreach (var process in pendingProcesses)
        {
            if (!_isRunning)
            {
                await WaitForResumeAsync();
            }

            if (_cts.IsCancellationRequested)
                break;

            await ExecuteProcessWithSchedulingAsync(process);
        }

        // 3. Execute concurrent assignments (non-process tasks).
        // Phase H.3: gate by sensitivity — skip Operations whose static
        // input-signal set didn't overlap with the previous delta's
        // changed-signal set. Reduces per-delta cost on signal-heavy
        // designs (P3 was ~57s/2µs, P22 was ~130s/3µs). On the first
        // delta after a time-step boundary _lastDeltaChangedSignals is
        // null → run everything (initialization / external stimulus
        // propagation).
        //
        // F-G-08 pass 2 (2026-07-16): the gated path now consults the
        // reverse signal→ops index built in Initialize instead of calling
        // inputs.Overlaps(changed) per task. Iteration still walks the
        // original _concurrentTasks list so execution order stays exactly
        // equal to elaboration order (deterministic waveform/VCD), and so
        // ISControl entries keep running unconditionally as before.
        if (_lastDeltaChangedSignals == null)
        {
            // runAll: first delta of the time step. Fire everything so
            // initial values and external-stimulus edges propagate.
            foreach (var task in _concurrentTasks)
            {
                if (!_isRunning)
                {
                    await WaitForResumeAsync();
                }

                if (_cts.IsCancellationRequested)
                    break;

                if (task is Operation operation)
                {
                    var scheduledOp = GetScheduledOperation(operation);
                    scheduledOp.Execute();
                }
                else if (task is ISControl control && !(task is Process))
                {
                    await ExecuteControlWithSchedulingAsync(control);
                }
            }
        }
        else
        {
            // Gated path: build the pending set from the reverse index.
            _pendingOpsScratch.Clear();
            foreach (var v in _lastDeltaChangedSignals)
            {
                if (_signalToOps.TryGetValue(v, out var ops))
                {
                    for (int i = 0; i < ops.Count; i++) _pendingOpsScratch.Add(ops[i]);
                }
            }
            for (int i = 0; i < _alwaysRunOps.Count; i++) _pendingOpsScratch.Add(_alwaysRunOps[i]);

            foreach (var task in _concurrentTasks)
            {
                if (!_isRunning)
                {
                    await WaitForResumeAsync();
                }

                if (_cts.IsCancellationRequested)
                    break;

                if (task is Operation operation)
                {
                    if (!_pendingOpsScratch.Contains(operation)) continue;
                    var scheduledOp = GetScheduledOperation(operation);
                    scheduledOp.Execute();
                }
                else if (task is ISControl control && !(task is Process))
                {
                    await ExecuteControlWithSchedulingAsync(control);
                }
            }
        }

        // 4. Apply all scheduled updates. Phase B+ Deep AST: use the
        //    line-aware variant so each change carries the 1-based source
        //    line of the operation that drove it (0 = unknown).
        // Tier 4.1: pull old-values alongside so SignalAttributeHandler can
        //    populate per-delta `'event` / `'last_value` / `'stable(T)` state.
        var (changedWithLines, oldValuesThisDelta) =
            _signalScheduler.ApplyCurrentUpdatesWithLinesAndOldValues();
        var changedSignals = new HashSet<Variable>(changedWithLines.Keys);

        // 5. Record signal changes in history with source-line metadata.
        // Time moment = tick * 1000 + delta (allows for up to 1000 delta cycles per tick).
        //
        // Both the history AND the attribute path use a `long` timestamp: on
        // seconds-scale runs `tick*1000` overflows a 32-bit int (~21 ms at
        // 10 ns/tick). History was widened 2026-09-13; the attribute path is
        // widened now (2026-09-13 follow-up) so `'stable(T)` / `'last_value` /
        // `'event` timing stays correct past 21 ms instead of aliasing on the
        // wrapped int. Runs under 21 ms are unaffected (identical values).
        long timeMoment = (long)_currentTick * 1000 + _currentDelta;
        _signalHistory.RecordChangesWithLines(changedWithLines, timeMoment);

        // 5b. Tier 4.1: refresh signal-attribute state at delta granularity.
        //     Replaces the tick-boundary UpdateAllPreviousValues call (deleted
        //     from RunTimeStepAsync). 0→1→0-within-a-tick transitions now
        //     register two distinct `'event` deltas instead of collapsing.
        SignalAttributeHandler.Instance.OnDeltaCommit(oldValuesThisDelta, timeMoment);

        // 6. Fire signal change events
        foreach (var signal in changedSignals)
        {
            OnSignalChange?.Invoke(signal, SignalAttributeHandler.Instance.GetLastValue(signal), signal.Value);
        }

        // 7. Fire delta cycle complete event
        OnDeltaCycleComplete?.Invoke(_currentTick, _currentDelta);

        // Phase B: emit a single per-frame event the UI can subscribe to for
        // live replay. Fires after the signal-change burst so subscribers see
        // the post-delta state. Snapshots the changed-signals collection so
        // the caller can safely retain it across awaits. Phase B+ Deep AST:
        // also carries the per-signal source-line map.
        if (OnFrameEmitted != null)
        {
            var snapshot = new List<Variable>(changedSignals);
            var linesCopy = new Dictionary<Variable, int>(changedWithLines);
            OnFrameEmitted.Invoke(new FrameEmittedArgs(_currentTick, _currentDelta, snapshot, linesCopy));
        }

        // 8. Notify process scheduler of changed signals
        if (changedSignals.Count > 0)
        {
            _processScheduler.NotifySignalChanges(changedSignals);
        }

        // 8b. F-G-13b (Session 23, 2026-07-18): wake any wait-until process
        // whose sensitivity set intersects the changed signals AND whose
        // stored condition now evaluates true. Newly-woken processes are
        // queued at _currentTick; drain them now so their signal writes
        // enter SignalScheduler and get applied on the NEXT delta cycle
        // (same tick). Runs AFTER the sensitivity-driven notify so
        // combinational fanout is already visible; if the condition holds
        // the process executes its next segment before the tick advances.
        WakeConditionBlockedProcesses(changedSignals);
        await DrainReadyProcessWakesAsync();

        // Phase H.3: snapshot the changed-signal set so the NEXT delta
        // cycle can gate concurrent tasks on it.
        _lastDeltaChangedSignals = changedSignals;

        // Return true if there's more work to do.
        //
        // Including `changedSignals.Count > 0` is load-bearing: concurrent
        // signal assignments (the items in `_concurrentTasks`) re-evaluate
        // unconditionally each delta cycle, but the previous exit condition
        // only checked pending processes/schedule. That meant `cout <= cnt`
        // computed against the OLD cnt at delta 0 and never got a chance to
        // re-fire with the new cnt within the same tick — Kmila ended up
        // emitting the cout update one whole tick later than GHDL.
        // Counter case 003 exposed this; MAX_DELTA_CYCLES still caps
        // combinational-loop runaways.
        // Bench Phase E bug #7 fix (2026-06-02).
        return _processScheduler.HasPendingProcesses
            || _signalScheduler.HasPendingUpdates
            || changedSignals.Count > 0;
    }

    /// <summary>
    /// Executes a process with signal scheduling.
    /// </summary>
    private async Task ExecuteProcessWithSchedulingAsync(Process process)
    {
        // Execute process tasks with scheduling
        foreach (var task in process.Tasks)
        {
            if (task is Operation operation)
            {
                // v0.17.0: Use cached scheduled operation
                var scheduledOp = GetScheduledOperation(operation);
                scheduledOp.Execute();
            }
            else if (task is ISControl control)
            {
                await ExecuteControlWithSchedulingAsync(control);
            }
        }
    }

    /// <summary>
    /// F-G-13 MVP (2026-07-18): executes ONE segment of a wait-driven
    /// process — from its current <see cref="Process.Pc"/> up to (and
    /// yielding at) the next <see cref="Interpreter.Repositories.WaitForStatement"/>
    /// or the end of the body. If the wait is timed, reschedules the
    /// process for <c>current_tick + delayTicks</c>. If the wait is
    /// terminal (bare <c>wait;</c> or unsupported forms) or the body
    /// ends, the process finishes with no further schedule.
    ///
    /// Runs alongside the legacy per-tick concurrent-task + delta-cycle
    /// loop; time-driven processes' signal writes enter the same
    /// <see cref="SignalScheduler"/> so downstream deltas propagate them
    /// correctly.
    /// </summary>
    private async Task ExecuteProcessSegmentAsync(Process process)
    {
        // F-G-13c (Session 23, 2026-07-18): resume any suspended loop
        // frames first. If the process paused mid-loop on a wait, the
        // stack still holds its iteration state; drain it deepest-first
        // by asking the top frame to continue. Any nested-frame return
        // that reports "still suspended" bubbles out immediately.
        while (process.Frames.Count > 0)
        {
            var top = process.Frames.Peek();
            bool stillSuspended = top switch
            {
                Interpreter.Models.LoopFrame lf => lf.LoopRef switch
                {
                    Interpreter.Repositories.ForLoopStructure f
                        => await RunForLoopFromFrameAsync(process, f, lf),
                    Interpreter.Repositories.WhileLoopStructure w
                        => await RunWhileLoopFromFrameAsync(process, w, lf),
                    Interpreter.Repositories.SimpleLoopStructure s
                        => await RunSimpleLoopFromFrameAsync(process, s, lf),
                    _ => false,
                },
                // F-G-13d (Phase 2): drain if/case branch frames the same
                // way. The runner returns "still suspended" if a wait
                // fired inside its branch body.
                Interpreter.Models.BranchFrame bf => bf.StructureRef switch
                {
                    Interpreter.Repositories.IfStructure ifs
                        => await RunIfBranchFromFrameAsync(process, ifs, bf),
                    Interpreter.Repositories.CaseStructure cs
                        => await RunCaseBranchFromFrameAsync(process, cs, bf),
                    _ => false,
                },
                // Phase 3 (2026-07-22): drain procedure frames the same
                // way. A wait anywhere inside the procedure body kept the
                // frame on the stack; resume from the saved InnerPc.
                Interpreter.Models.ProcedureFrame pf
                    => await RunProcedureFromFrameAsync(process, pf),
                _ => false, // unknown frame type — drop it.
            };
            if (stillSuspended) return;
            process.Frames.Pop();
            if (process.IsTerminated) return;
        }

        // F-G-13d: outer implicit-loop for VHDL processes without a
        // trailing wait. When we fall off end below with HasWait &&
        // YieldedSinceLoopStart, we continue this outer loop so the
        // body re-enters from Pc=0. If we fall off without yielding,
        // we terminate — same semantics as before Phase 2.
        int loopSafetyCounter = 0;
        while (true)
        {
            if (++loopSafetyCounter > 1_000_000)
            {
                // Runaway detector — a process that spun a million
                // implicit iterations without yielding is definitely
                // wrong. Terminate defensively.
                process.IsTerminated = true;
                return;
            }
        while (process.Pc < process.Tasks.Count)
        {
            var task = process.Tasks[process.Pc];

            if (task is Interpreter.Repositories.WaitForStatement waitStmt)
            {
                process.Pc++; // resume past this wait next time
                YieldOnWait(process, waitStmt);
                return;
            }

            // F-G-13c/d: loops and if/case at process top level enter the
            // resumable executor so their body-level waits can suspend
            // the process. Pc is bumped BEFORE running so that a resume
            // via the frame-drain path lands on the NEXT top-level task
            // after the body completes — see the Pc++ line inside each
            // block. On suspend we `return` with Pc already past the
            // task; on completion we fall through and `continue` to the
            // next top-level task.
            if (task is Interpreter.Repositories.ForLoopStructure fs)
            {
                var frame = new Interpreter.Models.LoopFrame
                {
                    LoopRef = fs,
                    IterationVal = fs.RangeStart,
                    InnerPc = 0,
                };
                process.Pc++;
                process.Frames.Push(frame);
                bool suspended = await RunForLoopFromFrameAsync(process, fs, frame);
                if (suspended) return;
                process.Frames.Pop();
                if (process.IsTerminated) return;
                continue;
            }
            if (task is Interpreter.Repositories.WhileLoopStructure ws)
            {
                var frame = new Interpreter.Models.LoopFrame
                {
                    LoopRef = ws,
                    IterationVal = 0,
                    InnerPc = 0,
                };
                process.Pc++;
                process.Frames.Push(frame);
                bool suspended = await RunWhileLoopFromFrameAsync(process, ws, frame);
                if (suspended) return;
                process.Frames.Pop();
                if (process.IsTerminated) return;
                continue;
            }
            if (task is Interpreter.Repositories.SimpleLoopStructure ss)
            {
                var frame = new Interpreter.Models.LoopFrame
                {
                    LoopRef = ss,
                    IterationVal = 0,
                    InnerPc = 0,
                };
                process.Pc++;
                process.Frames.Push(frame);
                bool suspended = await RunSimpleLoopFromFrameAsync(process, ss, frame);
                if (suspended) return;
                process.Frames.Pop();
                if (process.IsTerminated) return;
                continue;
            }

            if (task is Operation op)
            {
                GetScheduledOperation(op).Execute();
            }
            // F-G-13d (Phase 2): route top-level if/case through the
            // process-aware runners so a wait inside a branch can suspend
            // the process. Non-process paths (concurrent tasks) continue
            // to use ExecuteControlWithSchedulingAsync, which walks the
            // branches synchronously and can't yield. Pc is bumped BEFORE
            // pushing the frame so a wait-driven resume lands on the
            // NEXT top-level task after the branch completes.
            else if (task is IfStructure topIf)
            {
                process.Pc++;
                bool suspended = await RunTopIfFromEntryAsync(process, topIf);
                if (suspended) return;
                if (process.IsTerminated) return;
                continue;
            }
            else if (task is CaseStructure topCase)
            {
                process.Pc++;
                bool suspended = await RunTopCaseFromEntryAsync(process, topCase);
                if (suspended) return;
                if (process.IsTerminated) return;
                continue;
            }
            // Phase 3 (2026-07-22): top-level procedure call routes through
            // the process-aware runner so a wait anywhere in the procedure
            // body (top, if/case branch, nested loop, nested call) can
            // suspend the whole process. Concurrent-context calls without
            // a Process stay on the ISControl fallback below.
            else if (task is Interpreter.Repositories.ProcedureCallStatement topCall)
            {
                process.Pc++;
                bool suspended = await RunTopProcedureFromEntryAsync(process, topCall);
                if (suspended) return;
                if (process.IsTerminated) return;
                continue;
            }
            else if (task is ISControl ctrl)
            {
                await ExecuteControlWithSchedulingAsync(ctrl);
            }
            process.Pc++;
        }

        // F-G-13d (Phase 2, 2026-07-22): implicit VHDL-style loop for
        // wait-driven processes without an explicit terminal wait.
        // If the body yielded at least once this pass, time advanced —
        // re-enter from Pc=0 (per VHDL LRM: process without top-level
        // `wait` is equivalent to `while true loop <body> end loop`).
        // If we fell off end WITHOUT yielding, don't loop — that would
        // be an unbounded zero-time spin.
        if (process.HasWait && process.YieldedSinceLoopStart)
        {
            process.Pc = 0;
            process.YieldedSinceLoopStart = false;
            continue; // outer while(true): re-enter body from Pc=0
        }
        process.IsTerminated = true;
        return;
        } // end outer while(true)
    }

    /// <summary>
    /// F-G-13c: shared wait-yield handling — used by both the top-level
    /// process segment runner and the resumable loop executors.
    /// Registers condition-blocked, schedules a timed wake, or marks
    /// terminal, based on the wait's shape.
    /// </summary>
    private void YieldOnWait(Process process, Interpreter.Repositories.WaitForStatement waitStmt)
    {
        // F-G-13d (Phase 2): mark that this process yielded at least once
        // since it last started from Pc=0. Consulted by the fall-off path
        // below to decide whether an implicit VHDL-style loop-back is safe.
        process.YieldedSinceLoopStart = true;
        if (waitStmt.IsCondition)
        {
            RegisterConditionBlocked(process, waitStmt);
        }
        else if (waitStmt.IsTimed && !waitStmt.IsTerminal)
        {
            int tickResNs = _signalScheduler.TickResolutionNs;
            ulong delayTicks = (ulong)Math.Max(1L, waitStmt.DelayNs / Math.Max(1, tickResNs));
            ScheduleProcessWake(process, _currentTick + delayTicks);
        }
        else
        {
            process.IsTerminated = true;
        }
    }

    /// <summary>
    /// F-G-13c: iterate a for loop from the state captured in
    /// <paramref name="frame"/>. Returns true if the process suspended on
    /// a wait (frame stays on the stack); false when the loop ran to
    /// completion. Called both on initial entry (frame at RangeStart, PC 0)
    /// and on resume (frame at saved iteration, PC past the yielding wait).
    /// Exit/next are handled via the existing exception protocol.
    /// </summary>
    private async Task<bool> RunForLoopFromFrameAsync(
        Process process,
        Interpreter.Repositories.ForLoopStructure fs,
        Interpreter.Models.LoopFrame frame)
    {
        int end = fs.RangeEnd;
        bool isDescending = !fs.IsAscending;
        int step = isDescending ? -1 : 1;
        var loopVar = fs.LoopVar;

        int i = frame.IterationVal;
        while (isDescending ? i >= end : i <= end)
        {
            frame.IterationVal = i;
            if (loopVar is not null) loopVar.Value = i.ToString();

            try
            {
                for (int idx = frame.InnerPc; idx < fs.Tasks.Count; idx++)
                {
                    var task = fs.Tasks[idx];
                    if (task is Interpreter.Repositories.WaitForStatement w)
                    {
                        frame.InnerPc = idx + 1;
                        YieldOnWait(process, w);
                        return true;
                    }
                    if (task is Operation op)
                    {
                        GetScheduledOperation(op).Execute();
                    }
                    // F-G-13d (Phase 2): nested if/case/for/while/simple-loop
                    // inside a loop body routes through the process-aware
                    // runner so a wait inside the nested body can suspend
                    // the whole process stack.
                    else if (task is IfStructure innerIf)
                    {
                        bool suspended = await RunTopIfFromEntryAsync(process, innerIf);
                        if (suspended) { frame.InnerPc = idx + 1; return true; }
                    }
                    else if (task is CaseStructure innerCase)
                    {
                        bool suspended = await RunTopCaseFromEntryAsync(process, innerCase);
                        if (suspended) { frame.InnerPc = idx + 1; return true; }
                    }
                    else if (task is Interpreter.Repositories.ForLoopStructure innerFor)
                    {
                        var nf = new Interpreter.Models.LoopFrame
                        { LoopRef = innerFor, IterationVal = innerFor.RangeStart, InnerPc = 0 };
                        process.Frames.Push(nf);
                        bool suspended = await RunForLoopFromFrameAsync(process, innerFor, nf);
                        if (suspended) { frame.InnerPc = idx + 1; return true; }
                        process.Frames.Pop();
                    }
                    else if (task is Interpreter.Repositories.WhileLoopStructure innerWhile)
                    {
                        var nf = new Interpreter.Models.LoopFrame
                        { LoopRef = innerWhile, IterationVal = 0, InnerPc = 0 };
                        process.Frames.Push(nf);
                        bool suspended = await RunWhileLoopFromFrameAsync(process, innerWhile, nf);
                        if (suspended) { frame.InnerPc = idx + 1; return true; }
                        process.Frames.Pop();
                    }
                    else if (task is Interpreter.Repositories.SimpleLoopStructure innerSimple)
                    {
                        var nf = new Interpreter.Models.LoopFrame
                        { LoopRef = innerSimple, IterationVal = 0, InnerPc = 0 };
                        process.Frames.Push(nf);
                        bool suspended = await RunSimpleLoopFromFrameAsync(process, innerSimple, nf);
                        if (suspended) { frame.InnerPc = idx + 1; return true; }
                        process.Frames.Pop();
                    }
                    // Phase 3 (2026-07-22): nested procedure call inside a
                    // loop body — recurse via the process-aware runner so a
                    // wait inside the callee suspends the whole process.
                    else if (task is Interpreter.Repositories.ProcedureCallStatement innerCall)
                    {
                        bool suspended = await RunTopProcedureFromEntryAsync(process, innerCall);
                        if (suspended) { frame.InnerPc = idx + 1; return true; }
                    }
                    else if (task is ISControl ctrl)
                    {
                        await ExecuteControlWithSchedulingAsync(ctrl);
                    }
                }
            }
            catch (Interpreter.Models.ExitException ex)
            {
                if (string.IsNullOrEmpty(ex.Label) ||
                    string.Equals(ex.Label, fs.LoopLabel, StringComparison.OrdinalIgnoreCase))
                {
                    return false; // loop completes
                }
                throw; // outer label — propagate to next frame level
            }
            catch (Interpreter.Models.NextException ex)
            {
                if (!string.IsNullOrEmpty(ex.Label) &&
                    !string.Equals(ex.Label, fs.LoopLabel, StringComparison.OrdinalIgnoreCase))
                {
                    throw;
                }
                // fall through: advance iteration
            }

            frame.InnerPc = 0;
            i += step;
        }
        return false;
    }

    /// <summary>
    /// F-G-13c: iterate a while loop from the state captured in the frame.
    /// Re-evaluates the condition each iteration.
    /// </summary>
    private async Task<bool> RunWhileLoopFromFrameAsync(
        Process process,
        Interpreter.Repositories.WhileLoopStructure ws,
        Interpreter.Models.LoopFrame frame)
    {
        const int MAX_ITERATIONS = 100_000;

        while (frame.IterationVal < MAX_ITERATIONS)
        {
            // Skip condition re-check on the first task of a mid-iteration
            // resume (InnerPc > 0 means we suspended mid-iteration).
            if (frame.InnerPc == 0)
            {
                bool condition = false;
                if (ws.Condition != null)
                {
                    var result = ws.Condition.Execute();
                    condition = result is Variable v
                        ? Convert.ToBoolean(v.Value)
                        : Convert.ToBoolean(result);
                }
                if (!condition) return false;
            }

            try
            {
                for (int idx = frame.InnerPc; idx < ws.Tasks.Count; idx++)
                {
                    var task = ws.Tasks[idx];
                    if (task is Interpreter.Repositories.WaitForStatement w)
                    {
                        frame.InnerPc = idx + 1;
                        YieldOnWait(process, w);
                        return true;
                    }
                    if (task is Operation op)
                    {
                        GetScheduledOperation(op).Execute();
                    }
                    // F-G-13d (Phase 2): nested if/case/for/while/simple-loop
                    // inside a loop body routes through the process-aware
                    // runner so a wait inside the nested body can suspend
                    // the whole process stack.
                    else if (task is IfStructure innerIf)
                    {
                        bool suspended = await RunTopIfFromEntryAsync(process, innerIf);
                        if (suspended) { frame.InnerPc = idx + 1; return true; }
                    }
                    else if (task is CaseStructure innerCase)
                    {
                        bool suspended = await RunTopCaseFromEntryAsync(process, innerCase);
                        if (suspended) { frame.InnerPc = idx + 1; return true; }
                    }
                    else if (task is Interpreter.Repositories.ForLoopStructure innerFor)
                    {
                        var nf = new Interpreter.Models.LoopFrame
                        { LoopRef = innerFor, IterationVal = innerFor.RangeStart, InnerPc = 0 };
                        process.Frames.Push(nf);
                        bool suspended = await RunForLoopFromFrameAsync(process, innerFor, nf);
                        if (suspended) { frame.InnerPc = idx + 1; return true; }
                        process.Frames.Pop();
                    }
                    else if (task is Interpreter.Repositories.WhileLoopStructure innerWhile)
                    {
                        var nf = new Interpreter.Models.LoopFrame
                        { LoopRef = innerWhile, IterationVal = 0, InnerPc = 0 };
                        process.Frames.Push(nf);
                        bool suspended = await RunWhileLoopFromFrameAsync(process, innerWhile, nf);
                        if (suspended) { frame.InnerPc = idx + 1; return true; }
                        process.Frames.Pop();
                    }
                    else if (task is Interpreter.Repositories.SimpleLoopStructure innerSimple)
                    {
                        var nf = new Interpreter.Models.LoopFrame
                        { LoopRef = innerSimple, IterationVal = 0, InnerPc = 0 };
                        process.Frames.Push(nf);
                        bool suspended = await RunSimpleLoopFromFrameAsync(process, innerSimple, nf);
                        if (suspended) { frame.InnerPc = idx + 1; return true; }
                        process.Frames.Pop();
                    }
                    // Phase 3 (2026-07-22): nested procedure call inside a
                    // loop body — recurse via the process-aware runner so a
                    // wait inside the callee suspends the whole process.
                    else if (task is Interpreter.Repositories.ProcedureCallStatement innerCall)
                    {
                        bool suspended = await RunTopProcedureFromEntryAsync(process, innerCall);
                        if (suspended) { frame.InnerPc = idx + 1; return true; }
                    }
                    else if (task is ISControl ctrl)
                    {
                        await ExecuteControlWithSchedulingAsync(ctrl);
                    }
                }
            }
            catch (Interpreter.Models.ExitException ex)
            {
                if (string.IsNullOrEmpty(ex.Label) ||
                    string.Equals(ex.Label, ws.LoopLabel, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                throw;
            }
            catch (Interpreter.Models.NextException ex)
            {
                if (!string.IsNullOrEmpty(ex.Label) &&
                    !string.Equals(ex.Label, ws.LoopLabel, StringComparison.OrdinalIgnoreCase))
                {
                    throw;
                }
            }

            frame.InnerPc = 0;
            frame.IterationVal++;
        }
        return false;
    }

    /// <summary>
    /// F-G-13c: iterate an unbounded (`loop … end loop`) from the frame.
    /// Exit statements terminate; without one and without an internal wait,
    /// the safety cap bails.
    /// </summary>
    private async Task<bool> RunSimpleLoopFromFrameAsync(
        Process process,
        Interpreter.Repositories.SimpleLoopStructure ss,
        Interpreter.Models.LoopFrame frame)
    {
        const int MAX_ITERATIONS = 100_000;

        while (frame.IterationVal < MAX_ITERATIONS)
        {
            try
            {
                for (int idx = frame.InnerPc; idx < ss.Tasks.Count; idx++)
                {
                    var task = ss.Tasks[idx];
                    if (task is Interpreter.Repositories.WaitForStatement w)
                    {
                        frame.InnerPc = idx + 1;
                        YieldOnWait(process, w);
                        return true;
                    }
                    if (task is Operation op)
                    {
                        GetScheduledOperation(op).Execute();
                    }
                    // F-G-13d (Phase 2): nested if/case/for/while/simple-loop
                    // inside a loop body routes through the process-aware
                    // runner so a wait inside the nested body can suspend
                    // the whole process stack.
                    else if (task is IfStructure innerIf)
                    {
                        bool suspended = await RunTopIfFromEntryAsync(process, innerIf);
                        if (suspended) { frame.InnerPc = idx + 1; return true; }
                    }
                    else if (task is CaseStructure innerCase)
                    {
                        bool suspended = await RunTopCaseFromEntryAsync(process, innerCase);
                        if (suspended) { frame.InnerPc = idx + 1; return true; }
                    }
                    else if (task is Interpreter.Repositories.ForLoopStructure innerFor)
                    {
                        var nf = new Interpreter.Models.LoopFrame
                        { LoopRef = innerFor, IterationVal = innerFor.RangeStart, InnerPc = 0 };
                        process.Frames.Push(nf);
                        bool suspended = await RunForLoopFromFrameAsync(process, innerFor, nf);
                        if (suspended) { frame.InnerPc = idx + 1; return true; }
                        process.Frames.Pop();
                    }
                    else if (task is Interpreter.Repositories.WhileLoopStructure innerWhile)
                    {
                        var nf = new Interpreter.Models.LoopFrame
                        { LoopRef = innerWhile, IterationVal = 0, InnerPc = 0 };
                        process.Frames.Push(nf);
                        bool suspended = await RunWhileLoopFromFrameAsync(process, innerWhile, nf);
                        if (suspended) { frame.InnerPc = idx + 1; return true; }
                        process.Frames.Pop();
                    }
                    else if (task is Interpreter.Repositories.SimpleLoopStructure innerSimple)
                    {
                        var nf = new Interpreter.Models.LoopFrame
                        { LoopRef = innerSimple, IterationVal = 0, InnerPc = 0 };
                        process.Frames.Push(nf);
                        bool suspended = await RunSimpleLoopFromFrameAsync(process, innerSimple, nf);
                        if (suspended) { frame.InnerPc = idx + 1; return true; }
                        process.Frames.Pop();
                    }
                    // Phase 3 (2026-07-22): nested procedure call inside a
                    // loop body — recurse via the process-aware runner so a
                    // wait inside the callee suspends the whole process.
                    else if (task is Interpreter.Repositories.ProcedureCallStatement innerCall)
                    {
                        bool suspended = await RunTopProcedureFromEntryAsync(process, innerCall);
                        if (suspended) { frame.InnerPc = idx + 1; return true; }
                    }
                    else if (task is ISControl ctrl)
                    {
                        await ExecuteControlWithSchedulingAsync(ctrl);
                    }
                }
            }
            catch (Interpreter.Models.ExitException ex)
            {
                if (string.IsNullOrEmpty(ex.Label) ||
                    string.Equals(ex.Label, ss.LoopLabel, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                throw;
            }
            catch (Interpreter.Models.NextException ex)
            {
                if (!string.IsNullOrEmpty(ex.Label) &&
                    !string.Equals(ex.Label, ss.LoopLabel, StringComparison.OrdinalIgnoreCase))
                {
                    throw;
                }
            }

            frame.InnerPc = 0;
            frame.IterationVal++;
        }
        return false;
    }

    // =========================================================================
    // F-G-13d (Phase 2, 2026-07-22): process-aware if/case runners.
    //
    // These mirror the loop runners' shape:
    //   * Top-level entry (RunTopIfFromEntryAsync / RunTopCaseFromEntryAsync)
    //     picks the branch, pushes a BranchFrame, calls the frame runner,
    //     and pops on completion.
    //   * Frame runner (RunIfBranchFromFrameAsync / RunCaseBranchFromFrameAsync)
    //     walks the selected branch's Tasks from frame.InnerPc, yielding
    //     when a wait is hit. Returns true when still suspended.
    //
    // The existing ExecuteIfStructureWithSchedulingAsync /
    // ExecuteCaseStructureWithSchedulingAsync stay in place for callers
    // outside process bodies (concurrent-context ISControl dispatch via
    // ExecuteControlWithSchedulingAsync). Those callers never have a
    // process to suspend, so the old branchless walk is still correct.
    // =========================================================================

    private async Task<bool> RunTopIfFromEntryAsync(Process process, IfStructure ifStruct)
    {
        // Pick the matching branch. Same rules as the legacy
        // ExecuteIfStructureWithSchedulingAsync above but capture the index
        // instead of walking the tasks inline.
        int matchIdx = -1;
        for (int i = 0; i < ifStruct.Branches.Count; i++)
        {
            var b = ifStruct.Branches[i];
            if (b.IsElse) { matchIdx = i; break; } // else is last per parse
            if (b.Condition != null)
            {
                var result = b.Condition.Execute() as Variable;
                if (result?.Value is bool cond && cond)
                {
                    matchIdx = i;
                    break;
                }
            }
        }
        if (matchIdx < 0) return false; // no branch fired

        var pickedBranch = ifStruct.Branches[matchIdx];
        OnConditionEvaluated?.Invoke(pickedBranch.ConditionLine);

        var frame = new Interpreter.Models.BranchFrame
        {
            StructureRef = ifStruct,
            BranchIdx = matchIdx,
            InnerPc = 0,
        };
        process.Frames.Push(frame);
        bool suspended = await RunIfBranchFromFrameAsync(process, ifStruct, frame);
        if (suspended) return true;
        process.Frames.Pop();
        return false;
    }

    private async Task<bool> RunIfBranchFromFrameAsync(
        Process process,
        IfStructure ifStruct,
        Interpreter.Models.BranchFrame frame)
    {
        if (frame.BranchIdx < 0 || frame.BranchIdx >= ifStruct.Branches.Count) return false;
        var branch = ifStruct.Branches[frame.BranchIdx];
        for (int idx = frame.InnerPc; idx < branch.Tasks.Count; idx++)
        {
            var task = branch.Tasks[idx];
            if (task is Interpreter.Repositories.WaitForStatement w)
            {
                frame.InnerPc = idx + 1;
                YieldOnWait(process, w);
                return true;
            }
            if (task is Operation op)
            {
                GetScheduledOperation(op).Execute();
            }
            // Nested if/case/loop inside an if branch — recurse via the
            // process-aware runner. If it suspends, we suspend at the
            // SAME task (not idx + 1) so the resume re-enters the same
            // structure; the pushed frame for the nested one keeps its
            // own InnerPc.
            else if (task is IfStructure nestedIf)
            {
                bool suspended = await RunTopIfFromEntryAsync(process, nestedIf);
                if (suspended) { frame.InnerPc = idx + 1; return true; }
            }
            else if (task is CaseStructure nestedCase)
            {
                bool suspended = await RunTopCaseFromEntryAsync(process, nestedCase);
                if (suspended) { frame.InnerPc = idx + 1; return true; }
            }
            else if (task is Interpreter.Repositories.ForLoopStructure nestedFor)
            {
                var nf = new Interpreter.Models.LoopFrame
                { LoopRef = nestedFor, IterationVal = nestedFor.RangeStart, InnerPc = 0 };
                process.Frames.Push(nf);
                bool suspended = await RunForLoopFromFrameAsync(process, nestedFor, nf);
                if (suspended) { frame.InnerPc = idx + 1; return true; }
                process.Frames.Pop();
            }
            else if (task is Interpreter.Repositories.WhileLoopStructure nestedWhile)
            {
                var nf = new Interpreter.Models.LoopFrame
                { LoopRef = nestedWhile, IterationVal = 0, InnerPc = 0 };
                process.Frames.Push(nf);
                bool suspended = await RunWhileLoopFromFrameAsync(process, nestedWhile, nf);
                if (suspended) { frame.InnerPc = idx + 1; return true; }
                process.Frames.Pop();
            }
            else if (task is Interpreter.Repositories.SimpleLoopStructure nestedSimple)
            {
                var nf = new Interpreter.Models.LoopFrame
                { LoopRef = nestedSimple, IterationVal = 0, InnerPc = 0 };
                process.Frames.Push(nf);
                bool suspended = await RunSimpleLoopFromFrameAsync(process, nestedSimple, nf);
                if (suspended) { frame.InnerPc = idx + 1; return true; }
                process.Frames.Pop();
            }
            // Phase 3 (2026-07-22): nested procedure call inside an
            // if/case branch — recurse via the process-aware runner.
            else if (task is Interpreter.Repositories.ProcedureCallStatement nestedCall)
            {
                bool suspended = await RunTopProcedureFromEntryAsync(process, nestedCall);
                if (suspended) { frame.InnerPc = idx + 1; return true; }
            }
            else if (task is ISControl ctrl)
            {
                await ExecuteControlWithSchedulingAsync(ctrl);
            }
        }
        return false;
    }

    private async Task<bool> RunTopCaseFromEntryAsync(Process process, CaseStructure caseStruct)
    {
        // Expression selector (e.g. `case to_integer(cnt_q) is`): no single
        // signal resolves, so evaluate the expression at run time — mirrors the
        // concurrent `with <expr> select` path. Plain/sliced-signal selectors
        // keep their existing behaviour.
        bool exprSelector = caseStruct.HasExpressionSelector;
        if (!exprSelector && caseStruct.Selector is null) return false;

        // Mirror the selector-slice + choice-resolution logic from
        // ExecuteCaseStructureWithSchedulingAsync so behaviour is
        // identical on the non-wait path.
        string selectorValue;
        if (exprSelector)
        {
            selectorValue = caseStruct.EvaluateSelectorExpression();
        }
        else
        {
            selectorValue = caseStruct.Selector!.Value?.ToString()?.Replace("\"", "").Replace("'", "").Trim() ?? "";
            if (caseStruct.HasSelectorSlice && selectorValue.Length > 0)
            {
                int hi = caseStruct.SelectorSliceHigh;
                int lo = caseStruct.SelectorSliceLow;
                // Range-aware selector slice: honour the selector's declared range.
                var (startPos, width) = caseStruct.Selector.SliceToSpan(hi, lo, selectorValue.Length);
                if (startPos >= 0 && width > 0 && startPos + width <= selectorValue.Length)
                {
                    selectorValue = selectorValue.Substring(startPos, width);
                }
            }
        }

        int matchIdx = -1;
        int fallbackOthersIdx = -1;
        for (int i = 0; i < caseStruct.Branches.Count; i++)
        {
            var b = caseStruct.Branches[i];
            if (b.IsOthers) { if (fallbackOthersIdx < 0) fallbackOthersIdx = i; continue; }
            foreach (var choice in b.Choices)
            {
                string cleanChoice = ResolveChoiceForCase(choice);
                bool hit = caseStruct.IsMatching
                    ? MatchesWithWildcards(cleanChoice, selectorValue)
                    : exprSelector
                        ? CaseStructure.ChoiceMatchesNumeric(cleanChoice, selectorValue)
                        : cleanChoice.Equals(selectorValue, StringComparison.OrdinalIgnoreCase);
                if (hit)
                {
                    matchIdx = i;
                    break;
                }
            }
            if (matchIdx == i) break;
        }
        if (matchIdx < 0) matchIdx = fallbackOthersIdx;
        if (matchIdx < 0) return false;

        var pickedBranch = caseStruct.Branches[matchIdx];
        OnConditionEvaluated?.Invoke(pickedBranch.ConditionLine);

        var frame = new Interpreter.Models.BranchFrame
        {
            StructureRef = caseStruct,
            BranchIdx = matchIdx,
            InnerPc = 0,
        };
        process.Frames.Push(frame);
        bool suspended = await RunCaseBranchFromFrameAsync(process, caseStruct, frame);
        if (suspended) return true;
        process.Frames.Pop();
        return false;
    }

    private async Task<bool> RunCaseBranchFromFrameAsync(
        Process process,
        CaseStructure caseStruct,
        Interpreter.Models.BranchFrame frame)
    {
        if (frame.BranchIdx < 0 || frame.BranchIdx >= caseStruct.Branches.Count) return false;
        var branch = caseStruct.Branches[frame.BranchIdx];
        for (int idx = frame.InnerPc; idx < branch.Tasks.Count; idx++)
        {
            var task = branch.Tasks[idx];
            if (task is Interpreter.Repositories.WaitForStatement w)
            {
                frame.InnerPc = idx + 1;
                YieldOnWait(process, w);
                return true;
            }
            if (task is Operation op)
            {
                GetScheduledOperation(op).Execute();
            }
            else if (task is IfStructure nestedIf)
            {
                bool suspended = await RunTopIfFromEntryAsync(process, nestedIf);
                if (suspended) { frame.InnerPc = idx + 1; return true; }
            }
            else if (task is CaseStructure nestedCase)
            {
                bool suspended = await RunTopCaseFromEntryAsync(process, nestedCase);
                if (suspended) { frame.InnerPc = idx + 1; return true; }
            }
            else if (task is Interpreter.Repositories.ForLoopStructure nestedFor)
            {
                var nf = new Interpreter.Models.LoopFrame
                { LoopRef = nestedFor, IterationVal = nestedFor.RangeStart, InnerPc = 0 };
                process.Frames.Push(nf);
                bool suspended = await RunForLoopFromFrameAsync(process, nestedFor, nf);
                if (suspended) { frame.InnerPc = idx + 1; return true; }
                process.Frames.Pop();
            }
            else if (task is Interpreter.Repositories.WhileLoopStructure nestedWhile)
            {
                var nf = new Interpreter.Models.LoopFrame
                { LoopRef = nestedWhile, IterationVal = 0, InnerPc = 0 };
                process.Frames.Push(nf);
                bool suspended = await RunWhileLoopFromFrameAsync(process, nestedWhile, nf);
                if (suspended) { frame.InnerPc = idx + 1; return true; }
                process.Frames.Pop();
            }
            else if (task is Interpreter.Repositories.SimpleLoopStructure nestedSimple)
            {
                var nf = new Interpreter.Models.LoopFrame
                { LoopRef = nestedSimple, IterationVal = 0, InnerPc = 0 };
                process.Frames.Push(nf);
                bool suspended = await RunSimpleLoopFromFrameAsync(process, nestedSimple, nf);
                if (suspended) { frame.InnerPc = idx + 1; return true; }
                process.Frames.Pop();
            }
            // Phase 3 (2026-07-22): nested procedure call inside an
            // if/case branch — recurse via the process-aware runner.
            else if (task is Interpreter.Repositories.ProcedureCallStatement nestedCall)
            {
                bool suspended = await RunTopProcedureFromEntryAsync(process, nestedCall);
                if (suspended) { frame.InnerPc = idx + 1; return true; }
            }
            else if (task is ISControl ctrl)
            {
                await ExecuteControlWithSchedulingAsync(ctrl);
            }
        }
        return false;
    }

    // =========================================================================
    // Phase 3 (Session 26, 2026-07-22): process-aware procedure runners.
    //
    // Entry: RunTopProcedureFromEntryAsync resolves the per-call bound body
    // via ProcedureCallStatement.ResolveBoundBody, pushes a ProcedureFrame,
    // and delegates to RunProcedureFromFrameAsync. The frame-drain switch
    // in ExecuteProcessSegmentAsync resumes the same runner when the
    // process wakes from a wait fired inside the procedure body.
    //
    // Empty-body path (procedure not in FunctionRegistry, e.g.
    // PackageRegistry-only ProcedureDefinition): falls back to the legacy
    // ProcedureCallStatement.ExecuteAsync — preserves F-G-12 report + F-G-14
    // signal-assign semantics for procedures that predate Phase 3's synth.
    // =========================================================================

    private async Task<bool> RunTopProcedureFromEntryAsync(
        Process process,
        Interpreter.Repositories.ProcedureCallStatement call)
    {
        var tasks = call.ResolveBoundBody(_signalScheduler, out var boundScope);
        if (tasks.Count == 0)
        {
            // Legacy fallback — procedure isn't registered in FunctionRegistry
            // (only in PackageRegistry) OR has an empty BodyRaw. The
            // pre-Phase-3 walker handles report + signal-assign textually.
            await call.ExecuteAsync(_signalScheduler);
            return false;
        }

        var frame = new Interpreter.Models.ProcedureFrame
        {
            Tasks = tasks,
            InnerPc = 0,
            CallRef = call,
            BoundScope = boundScope,
        };
        process.Frames.Push(frame);
        bool suspended = await RunProcedureFromFrameAsync(process, frame);
        if (suspended) return true;
        process.Frames.Pop();
        return false;
    }

    private async Task<bool> RunProcedureFromFrameAsync(
        Process process,
        Interpreter.Models.ProcedureFrame frame)
    {
        try
        {
            for (int idx = frame.InnerPc; idx < frame.Tasks.Count; idx++)
            {
                var task = frame.Tasks[idx];
                if (task is Interpreter.Repositories.WaitForStatement w)
                {
                    frame.InnerPc = idx + 1;
                    YieldOnWait(process, w);
                    return true;
                }
                if (task is Operation op)
                {
                    GetScheduledOperation(op).Execute();
                }
                else if (task is IfStructure nestedIf)
                {
                    bool suspended = await RunTopIfFromEntryAsync(process, nestedIf);
                    if (suspended) { frame.InnerPc = idx + 1; return true; }
                }
                else if (task is CaseStructure nestedCase)
                {
                    bool suspended = await RunTopCaseFromEntryAsync(process, nestedCase);
                    if (suspended) { frame.InnerPc = idx + 1; return true; }
                }
                else if (task is Interpreter.Repositories.ForLoopStructure nestedFor)
                {
                    var nf = new Interpreter.Models.LoopFrame
                    { LoopRef = nestedFor, IterationVal = nestedFor.RangeStart, InnerPc = 0 };
                    process.Frames.Push(nf);
                    bool suspended = await RunForLoopFromFrameAsync(process, nestedFor, nf);
                    if (suspended) { frame.InnerPc = idx + 1; return true; }
                    process.Frames.Pop();
                }
                else if (task is Interpreter.Repositories.WhileLoopStructure nestedWhile)
                {
                    var nf = new Interpreter.Models.LoopFrame
                    { LoopRef = nestedWhile, IterationVal = 0, InnerPc = 0 };
                    process.Frames.Push(nf);
                    bool suspended = await RunWhileLoopFromFrameAsync(process, nestedWhile, nf);
                    if (suspended) { frame.InnerPc = idx + 1; return true; }
                    process.Frames.Pop();
                }
                else if (task is Interpreter.Repositories.SimpleLoopStructure nestedSimple)
                {
                    var nf = new Interpreter.Models.LoopFrame
                    { LoopRef = nestedSimple, IterationVal = 0, InnerPc = 0 };
                    process.Frames.Push(nf);
                    bool suspended = await RunSimpleLoopFromFrameAsync(process, nestedSimple, nf);
                    if (suspended) { frame.InnerPc = idx + 1; return true; }
                    process.Frames.Pop();
                }
                // Nested procedure calls inside a procedure body — recurse.
                // Reentrant + recursive calls each get their own frame via
                // ResolveBoundBody, so state doesn't collide across levels.
                else if (task is Interpreter.Repositories.ProcedureCallStatement nestedCall)
                {
                    bool suspended = await RunTopProcedureFromEntryAsync(process, nestedCall);
                    if (suspended) { frame.InnerPc = idx + 1; return true; }
                }
                else if (task is ISControl ctrl)
                {
                    await ExecuteControlWithSchedulingAsync(ctrl);
                }
            }
        }
        catch (Interpreter.Models.ReturnException)
        {
            // Early `return;` inside the procedure — treat as normal
            // completion so the frame pops.
        }

        return false;
    }

    /// <summary>
    /// F-G-13 MVP (2026-07-18): pop and run all wait-driven processes
    /// scheduled to wake at ticks &lt;= <see cref="_currentTick"/>. Called
    /// at the top of each time step, BEFORE the sensitivity-driven
    /// process pass and the concurrent-task pass, so any signal writes
    /// from the newly-executed segments propagate through the same
    /// delta-cycle loop as everything else.
    /// </summary>
    private async Task DrainReadyProcessWakesAsync()
    {
        while (true)
        {
            ulong? readyTick = null;
            foreach (var kv in _pendingProcessWakes)
            {
                if (kv.Key <= _currentTick) { readyTick = kv.Key; break; }
                else break; // SortedDictionary — no later key would qualify.
            }
            if (readyTick == null) return;

            var list = _pendingProcessWakes[readyTick.Value];
            _pendingProcessWakes.Remove(readyTick.Value);
            foreach (var process in list)
            {
                if (process.IsTerminated) continue;
                await ExecuteProcessSegmentAsync(process);
            }
        }
    }

    /// <summary>
    /// Executes a control structure with signal scheduling.
    /// </summary>
    private async Task ExecuteControlWithSchedulingAsync(ISControl control)
    {
        // For control structures, we need to execute them with scheduling
        // This is a recursive operation for nested structures
        if (control is IfStructure ifStruct)
        {
            await ExecuteIfStructureWithSchedulingAsync(ifStruct);
        }
        else if (control is CaseStructure caseStruct)
        {
            await ExecuteCaseStructureWithSchedulingAsync(caseStruct);
        }
        else if (control is ForLoopStructure forLoop)
        {
            await ExecuteForLoopWithSchedulingAsync(forLoop);
        }
        else if (control is WhileLoopStructure whileLoop)
        {
            await ExecuteWhileLoopWithSchedulingAsync(whileLoop);
        }
        else if (control is SimpleLoopStructure simpleLoop)
        {
            await ExecuteSimpleLoopWithSchedulingAsync(simpleLoop);
        }
        else if (control is Interpreter.Repositories.SelectedAssignment selAssign)
        {
            // `with…select` writes to its target through the scheduler so
            // the change lands in SignalHistory and respects delta cycles.
            // Bench Phase E bug #4 fix.
            await selAssign.ExecuteAsync(_signalScheduler);
        }
        else if (control is Interpreter.Repositories.ConditionalAssignment condAssign)
        {
            // `value when cond else …` routes through the scheduler too.
            // Bench Phase E bug #10 fix.
            await condAssign.ExecuteAsync(_signalScheduler);
        }
        else if (control is Interpreter.Repositories.ProcedureCallStatement procCall)
        {
            // F-G-14 (Session 23, 2026-07-18): procedure body may contain
            // `S <= …` on signal-class formals — route through the
            // scheduler-aware overload so writes enter the delta cycle
            // rather than a local shadow Variable. Reports still emit
            // via ReportBus per F-G-12/12a/15.
            await procCall.ExecuteAsync(_signalScheduler);
        }
        else
        {
            // Fallback: execute normally (may not have proper scheduling)
            await control.ExecuteAsync();
        }
    }

    /// <summary>
    /// Executes an if structure with signal scheduling.
    /// </summary>
    private async Task ExecuteIfStructureWithSchedulingAsync(IfStructure ifStruct)
    {
        // Iterate through branches and execute the first one whose condition is true
        foreach (var branch in ifStruct.Branches)
        {
            // Else branch - always execute if we reach it
            if (branch.IsElse)
            {
                foreach (var task in branch.Tasks)
                {
                    if (task is Operation op)
                    {
                        var scheduledOp = GetScheduledOperation(op);
                        scheduledOp.Execute();
                    }
                    else if (task is ISControl ctrl)
                    {
                        await ExecuteControlWithSchedulingAsync(ctrl);
                    }
                }
                return;
            }

            // Evaluate condition
            if (branch.Condition != null)
            {
                var result = branch.Condition.Execute() as Variable;
                if (result?.Value is bool condition && condition)
                {
                    // #5: branch taken — let breakpoint consumers pause on the
                    // condition's source line.
                    OnConditionEvaluated?.Invoke(branch.ConditionLine);
                    foreach (var task in branch.Tasks)
                    {
                        if (task is Operation op)
                        {
                            var scheduledOp = GetScheduledOperation(op);
                            scheduledOp.Execute();
                        }
                        else if (task is ISControl ctrl)
                        {
                            await ExecuteControlWithSchedulingAsync(ctrl);
                        }
                    }
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Executes a case structure with signal scheduling.
    /// </summary>
    private async Task ExecuteCaseStructureWithSchedulingAsync(CaseStructure caseStruct)
    {
        // Expression selector (e.g. `case to_integer(cnt_q) is`): evaluate at run
        // time, mirroring the concurrent `with <expr> select` path. Plain/sliced
        // signal selectors keep their existing behaviour.
        bool exprSelector = caseStruct.HasExpressionSelector;
        if (!exprSelector && caseStruct.Selector is null) return;

        string selectorValue = exprSelector
            ? caseStruct.EvaluateSelectorExpression()
            : (caseStruct.Selector!.Value?.ToString()?.Replace("\"", "").Replace("'", "").Trim() ?? "");

        // Bench bug #19 follow-up (P2A CPU dispatch): when the selector is
        // written as a slice (`case word(23 downto 16) is`), the engine was
        // reading the whole vector. Extract the slice bits so the choice
        // comparison sees the right value. Storage convention: MSB-first
        // (vector position 0 holds the high bit).
        if (!exprSelector && caseStruct.HasSelectorSlice && selectorValue.Length > 0)
        {
            int hi = caseStruct.SelectorSliceHigh;
            int lo = caseStruct.SelectorSliceLow;
            // Range-aware selector slice: honour the selector's declared range.
            var (startPos, width) = caseStruct.Selector!.SliceToSpan(hi, lo, selectorValue.Length);
            if (startPos >= 0 && width > 0 && startPos + width <= selectorValue.Length)
            {
                selectorValue = selectorValue.Substring(startPos, width);
            }
        }

        CaseWhenBranch? matchingBranch = null;

        // Find matching branch
        foreach (var branch in caseStruct.Branches)
        {
            if (branch.IsOthers)
            {
                // Save others as fallback
                if (matchingBranch == null)
                {
                    matchingBranch = branch;
                }
                continue;
            }

            // Check if any choice matches the selector.
            //
            // Bench bug #19 follow-up (P2A FSM): choices captured during
            // synthesis are raw tokens — string literals (`"00"`), hex
            // literals (`x"0B"`), integer literals (`5`), AND identifier
            // references to constants (`OP_LOAD`). The legacy code only
            // string-compared the raw token to the selector's binary value,
            // so identifier choices never matched. ResolveChoiceForCase
            // walks the resolution rules so a case `when OP_LOAD =>` fires
            // when the selector slice equals the constant's binary value.
            foreach (var choice in branch.Choices)
            {
                string cleanChoice = ResolveChoiceForCase(choice);
                bool hit = caseStruct.IsMatching
                    ? MatchesWithWildcards(cleanChoice, selectorValue)
                    : exprSelector
                        ? CaseStructure.ChoiceMatchesNumeric(cleanChoice, selectorValue)
                        : cleanChoice.Equals(selectorValue, StringComparison.OrdinalIgnoreCase);
                if (hit)
                {
                    matchingBranch = branch;
                    break;
                }
            }

            if (matchingBranch == branch)
                break;
        }

        if (matchingBranch != null)
        {
            // #5: arm selected — let breakpoint consumers pause on the
            // when-arm's source line.
            OnConditionEvaluated?.Invoke(matchingBranch.ConditionLine);
            foreach (var task in matchingBranch.Tasks)
            {
                if (task is Operation op)
                {
                    var scheduledOp = GetScheduledOperation(op);
                    scheduledOp.Execute();
                }
                else if (task is ISControl ctrl)
                {
                    await ExecuteControlWithSchedulingAsync(ctrl);
                }
            }
        }
    }

    /// <summary>
    /// Normalises a raw case-choice token into the same shape as the
    /// selector's runtime value so the equality test can fire when the
    /// choice was written as a constant identifier, hex literal, integer
    /// literal, or quoted bit string. Returns the original (cleaned) text
    /// when the token doesn't resolve to anything recognisable so the
    /// legacy string match still has a chance.
    /// </summary>
    /// <summary>
    /// VHDL-2008 matching-case comparison (Tier 4.2, 2026-07-28): `-` in the
    /// choice literal matches any bit of the selector at that position;
    /// everything else is a bit-for-bit exact compare (case-insensitive).
    /// A length mismatch is a non-match rather than an error so the loop
    /// continues to the next arm or falls through to `others`. Kept as a
    /// static helper local to DeltaCycleEngine to avoid exposing another
    /// case-matching entry point outside this file.
    /// </summary>
    private static bool MatchesWithWildcards(string choice, string selector)
    {
        if (choice.Length != selector.Length) return false;
        for (int i = 0; i < choice.Length; i++)
        {
            char c = choice[i];
            if (c == '-') continue;
            if (char.ToUpperInvariant(c) != char.ToUpperInvariant(selector[i])) return false;
        }
        return true;
    }

    private string ResolveChoiceForCase(string choice)
    {
        string clean = choice?.Trim() ?? string.Empty;
        if (clean.Length == 0) return clean;

        // `x"NN"`, `"0101"`, `'1'` → their bits.
        if (Models.HdlLiteral.TryBits(clean, out string literalBits))
            return literalBits;

        // Identifier reference to a known signal/constant in scope: use the
        // current value of that signal. Constants don't change so this is
        // safe; for non-constant signals we capture whatever the latest
        // value happens to be (an unlikely case-arm pattern, but accepted).
        try
        {
            var match = _allSignals?.FirstOrDefault(v =>
                v is not null && !string.IsNullOrEmpty(v.Name) &&
                v.Name.Equals(clean, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                object? matchValue = match.Value;
                string v = matchValue?.ToString() ?? string.Empty;
                // The Variable's Value already has quotes stripped, so it
                // should be a clean bit string when type is STD_LOGIC_VECTOR.
                return v.Replace("\"", "").Replace("'", "").Trim();
            }
        }
        catch
        {
            // F-G-04 follow-up (2026-07-09): a subtype-typed signal whose
            // Value hasn't been fully initialised can throw NRE from its
            // wrapper's ToString. Falling through to the raw-string
            // fallback below still lets literal-string case arms match,
            // and package-constant case arms fall through to `when others`
            // — matches pre-scanner behaviour for those patterns.
        }

        // Constants from the PackageRegistry (loaded by PackageSubtypeScanner
        // at project scan time). Package constants aren't part of the arch
        // signal set but are visible to case-arm matchers via `use
        // work.<pkg>.all;`.
        try
        {
            var pkgConst = PackageRegistry.Instance.GetConstant(clean);
            if (pkgConst?.Value != null)
            {
                string cv = pkgConst.Value.ToString() ?? string.Empty;
                return cv.Replace("\"", "").Replace("'", "").Trim();
            }
        }
        catch { /* fall through */ }

        // Plain integer literal — last-resort fallback if the selector
        // happens to be decimal-shaped too. Not converted to binary here
        // because the selector's width is unknown at this layer; falling
        // back to a string match preserves the legacy behaviour.
        return clean.Replace("\"", string.Empty).Replace("'", string.Empty);
    }

    /// <summary>
    /// Executes a for loop with signal scheduling.
    /// </summary>
    private async Task ExecuteForLoopWithSchedulingAsync(ForLoopStructure forLoop)
    {
        int start = forLoop.RangeStart;
        int end = forLoop.RangeEnd;
        bool isDescending = !forLoop.IsAscending;
        int step = isDescending ? -1 : 1;

        // Bug #14: reuse the synth-time loop Variable instance (the same
        // one DynamicIndex captured as IndexSource). A fresh copy here
        // would leave body operations reading a stale "0" while we update
        // a different Variable.
        Variable loopVar = forLoop.LoopVar ??
            new Variable(forLoop.LoopVariableName, start.ToString(), Parser.Models.Constants.DATATYPES.INTEGER);

        for (int i = start; isDescending ? i >= end : i <= end; i += step)
        {
            // Update loop variable
            loopVar.Value = i.ToString();

            foreach (var task in forLoop.Tasks)
            {
                if (task is Operation op)
                {
                    var scheduledOp = GetScheduledOperation(op);
                    scheduledOp.Execute();
                }
                else if (task is ISControl ctrl)
                {
                    await ExecuteControlWithSchedulingAsync(ctrl);
                }
            }
        }
    }

    /// <summary>
    /// Executes a while loop with signal scheduling.
    /// </summary>
    private async Task ExecuteWhileLoopWithSchedulingAsync(WhileLoopStructure whileLoop)
    {
        const int MAX_ITERATIONS = 10000;
        int iterations = 0;

        while (iterations < MAX_ITERATIONS)
        {
            // Evaluate condition
            bool condition = false;
            if (whileLoop.Condition != null)
            {
                var result = whileLoop.Condition.Execute();
                if (result is Variable v)
                {
                    condition = Convert.ToBoolean(v.Value);
                }
                else
                {
                    condition = Convert.ToBoolean(result);
                }
            }

            if (!condition)
                break;

            foreach (var task in whileLoop.Tasks)
            {
                if (task is Operation op)
                {
                    var scheduledOp = GetScheduledOperation(op);
                    scheduledOp.Execute();
                }
                else if (task is ISControl ctrl)
                {
                    await ExecuteControlWithSchedulingAsync(ctrl);
                }
            }

            iterations++;
        }
    }

    /// <summary>
    /// Executes a simple loop with signal scheduling.
    /// </summary>
    private async Task ExecuteSimpleLoopWithSchedulingAsync(SimpleLoopStructure simpleLoop)
    {
        const int MAX_ITERATIONS = 10000;
        int iterations = 0;

        while (iterations < MAX_ITERATIONS)
        {
            bool shouldExit = false;

            foreach (var task in simpleLoop.Tasks)
            {
                if (task is Operation op)
                {
                    var scheduledOp = GetScheduledOperation(op);
                    scheduledOp.Execute();
                }
                else if (task is ExitStatement exit)
                {
                    // Check exit condition
                    if (exit.IsUnconditional)
                    {
                        shouldExit = true;
                        break;
                    }
                    else if (!string.IsNullOrEmpty(exit.ConditionString))
                    {
                        // Evaluate condition
                        Operation conditionOp = new(exit.ConditionString, _allSignals);
                        var result = conditionOp.Execute();
                        bool shouldBreak = false;
                        if (result is Variable v)
                        {
                            shouldBreak = v.Value?.ToString() == "1" ||
                                         v.Value?.ToString()?.ToLower() == "true";
                        }
                        else if (result is bool b)
                        {
                            shouldBreak = b;
                        }

                        if (shouldBreak)
                        {
                            shouldExit = true;
                            break;
                        }
                    }
                }
                else if (task is ISControl ctrl)
                {
                    await ExecuteControlWithSchedulingAsync(ctrl);
                }
            }

            if (shouldExit)
                break;

            iterations++;
        }
    }

    /// <summary>
    /// Connects to TimeMachine for time-driven simulation.
    /// </summary>
    public void ConnectToTimeMachine()
    {
        // Subscribe to timer events
        TimeMachine.Repositories.Timer.OnPaused += OnPaused;
    }

    /// <summary>
    /// Handles pause/resume events from TimeMachine.
    /// </summary>
    private void OnPaused(bool isPaused)
    {
        _isRunning = !isPaused;
        if (_isRunning)
        {
            _pauseCts.Cancel();
            _pauseCts = new CancellationTokenSource();
        }
    }

    /// <summary>
    /// Waits for simulation to resume after pause.
    /// </summary>
    private async Task WaitForResumeAsync()
    {
        try
        {
            await Task.Delay(Timeout.Infinite, _pauseCts.Token);
        }
        catch (TaskCanceledException)
        {
            // Expected when resuming
        }
    }

    /// <summary>
    /// Advances simulation time.
    /// </summary>
    /// <param name="newTick">The new tick value.</param>
    public void AdvanceTime(ulong newTick)
    {
        _currentTick = newTick;
        _signalScheduler.AdvanceTime(newTick);
        OnTimeAdvance?.Invoke(newTick);
    }

    /// <summary>
    /// Runs the full simulation loop, driven by TimeMachine ticks.
    /// </summary>
    public async Task RunSimulationAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            if (_isRunning)
            {
                await RunTimeStepAsync();
                // Wait for next tick (handled by caller)
                await Task.Yield();
            }
            else
            {
                await WaitForResumeAsync();
            }
        }
    }

    /// <summary>
    /// Stops the simulation.
    /// </summary>
    public void Stop()
    {
        _cts.Cancel();
        _pauseCts.Cancel();
    }

    /// <summary>
    /// Phase B (2026-05-19): cooperative pause. Blocks the engine before its
    /// next process / scheduled-op execution until <see cref="Resume"/> is
    /// called. Independent of the TimeMachine wiring (still honoured) so
    /// non-timer consumers can drive pause directly — used by the Editor's
    /// live-replay tab so the user can scrub mid-run.
    /// </summary>
    public void Pause() => _isRunning = false;

    /// <summary>
    /// Phase B (2026-05-19): cooperative resume — cancels the inner pause
    /// gate so any <see cref="WaitForResumeAsync"/> in flight returns and
    /// the engine continues from the next process step.
    /// </summary>
    public void Resume()
    {
        _isRunning = true;
        _pauseCts.Cancel();
        _pauseCts = new CancellationTokenSource();
    }

    /// <summary>
    /// Phase B (2026-05-19): step-one-delta helper. Briefly resumes the
    /// engine, lets exactly one frame fire (subscribed via
    /// <see cref="OnFrameEmitted"/>), then re-pauses. Intended for use from
    /// a paused state — callers should already have invoked <see cref="Pause"/>.
    /// </summary>
    public void StepOneDelta()
    {
        if (!_isRunning)
        {
            // The on-frame-emitted callback below re-pauses after one delta.
            void OnceHandler(FrameEmittedArgs _)
            {
                OnFrameEmitted -= OnceHandler;
                _isRunning = false;
            }
            OnFrameEmitted += OnceHandler;
            Resume();
        }
    }

    /// <summary>
    /// Resets the engine for a new simulation.
    /// </summary>
    public void Reset()
    {
        Stop();

        _cts = new CancellationTokenSource();
        _pauseCts = new CancellationTokenSource();
        _currentTick = 0;
        _currentDelta = 0;
        _isRunning = true;

        _signalScheduler.Clear();
        _processScheduler.Clear();
        _signalHistory.Clear();
        _scheduledOpCache.Clear();  // v0.17.0: Clear operation cache
        SignalAttributeHandler.Instance.Reset();

        _isInitialized = false;
    }

    /// <summary>
    /// Disposes of resources.
    /// </summary>
    public void Dispose()
    {
        Stop();

        TimeMachine.Repositories.Timer.OnPaused -= OnPaused;

        _cts.Dispose();
        _pauseCts.Dispose();
    }
}

/// <summary>
/// Phase B (2026-05-19): payload carried by <see cref="DeltaCycleEngine.OnFrameEmitted"/>.
/// Snapshot of one delta cycle for live-replay consumers. The
/// <see cref="ChangedSignals"/> list is a defensive copy — safe to retain
/// across awaits even while the engine pushes more frames.
/// </summary>
public sealed class FrameEmittedArgs
{
    /// <summary>Simulation tick this frame was emitted at.</summary>
    public ulong Tick { get; }
    /// <summary>Delta-cycle index within the tick.</summary>
    public int Delta { get; }
    /// <summary>Signals that changed during this delta cycle (defensive copy).</summary>
    public IReadOnlyList<Variable> ChangedSignals { get; }

    /// <summary>
    /// Phase B+ Deep AST (2026-05-19): per-signal 1-based source line of the
    /// operation that produced each change in this delta. Keys are the same
    /// references as <see cref="ChangedSignals"/>; values are 0 when the
    /// driving operation has no captured source line (synthetic ops, legacy
    /// construction paths). Subscribers can use this to highlight only the
    /// firing branch instead of every assignment statement.
    /// </summary>
    public IReadOnlyDictionary<Variable, int> SourceLines { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="FrameEmittedArgs"/> class.
    /// </summary>
    /// <param name="tick">Simulation tick this frame was emitted at.</param>
    /// <param name="delta">Delta-cycle index within the tick.</param>
    /// <param name="changed">Signals that changed during this delta cycle.</param>
    /// <param name="sourceLines">Optional per-signal source lines; empty when omitted.</param>
    public FrameEmittedArgs(ulong tick, int delta, IReadOnlyList<Variable> changed,
        IReadOnlyDictionary<Variable, int>? sourceLines = null)
    {
        Tick = tick;
        Delta = delta;
        ChangedSignals = changed;
        SourceLines = sourceLines ?? new Dictionary<Variable, int>();
    }
}
