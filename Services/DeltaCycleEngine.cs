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
        set => _signalScheduler.TickResolutionNs = value;
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
        foreach (var signal in signals)
        {
            handler.RegisterSignal(signal);
        }

        // Register all signals with the history recorder
        _signalHistory.RegisterSignals(signals, initialTime: 0, recordInitial: true);

        // Register all processes with the process scheduler
        foreach (var task in tasks)
        {
            if (task is Process process)
            {
                _processScheduler.RegisterProcess(process);
            }
        }

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

        // For the first time step, initialize all processes
        if (_currentTick == 0 && _currentDelta == 0)
        {
            _processScheduler.InitializeAllProcesses();
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
            }
        }

        // Run delta cycles until stable
        bool hasMoreWork;
        do
        {
            hasMoreWork = await RunDeltaCycleAsync();
            _currentDelta++;

            // Safety check for infinite loops
            if (_currentDelta > MAX_DELTA_CYCLES)
            {
                throw new InvalidOperationException(
                    $"Delta cycle limit ({MAX_DELTA_CYCLES}) exceeded at tick {_currentTick}. " +
                    "Possible combinational loop in design.");
            }
        }
        while (hasMoreWork && _isRunning && !_cts.IsCancellationRequested);

        // Simulation is stable at this time point
        OnSimulationStable?.Invoke();

        // Update previous values for all signals (for 'event detection next cycle)
        SignalAttributeHandler.Instance.UpdateAllPreviousValues(_allSignals);
    }

    /// <summary>
    /// Runs a single delta cycle.
    /// </summary>
    /// <returns>True if more delta cycles are needed.</returns>
    private async Task<bool> RunDeltaCycleAsync()
    {
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

        // 3. Execute concurrent assignments (non-process tasks)
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
                // v0.17.0: Use cached scheduled operation to avoid repeated allocations
                var scheduledOp = GetScheduledOperation(operation);
                scheduledOp.Execute();
            }
            else if (task is ISControl control && !(task is Process))
            {
                // Other control structures (if-generate, etc.) - execute normally
                // These typically contain operations that should also be scheduled
                await ExecuteControlWithSchedulingAsync(control);
            }
        }

        // 4. Apply all scheduled updates. Phase B+ Deep AST: use the
        //    line-aware variant so each change carries the 1-based source
        //    line of the operation that drove it (0 = unknown).
        var changedWithLines = _signalScheduler.ApplyCurrentUpdatesWithLines();
        var changedSignals = new HashSet<Variable>(changedWithLines.Keys);

        // 5. Record signal changes in history with source-line metadata.
        // Time moment = tick * 1000 + delta (allows for up to 1000 delta cycles per tick)
        int timeMoment = (int)(_currentTick * 1000) + _currentDelta;
        _signalHistory.RecordChangesWithLines(changedWithLines, timeMoment);

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
        if (caseStruct.Selector == null) return;

        string selectorValue = caseStruct.Selector.Value?.ToString()?.Replace("\"", "").Replace("'", "").Trim() ?? "";

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

            // Check if any choice matches the selector
            foreach (var choice in branch.Choices)
            {
                string cleanChoice = choice.Replace("\"", "").Replace("'", "").Trim();
                if (cleanChoice.Equals(selectorValue, StringComparison.OrdinalIgnoreCase))
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
    public ulong Tick { get; }
    public int Delta { get; }
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

    public FrameEmittedArgs(ulong tick, int delta, IReadOnlyList<Variable> changed,
        IReadOnlyDictionary<Variable, int>? sourceLines = null)
    {
        Tick = tick;
        Delta = delta;
        ChangedSignals = changed;
        SourceLines = sourceLines ?? new Dictionary<Variable, int>();
    }
}
