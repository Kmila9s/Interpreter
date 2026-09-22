using Interpreter.Models;
using Parser.Models;

namespace Interpreter.Services;

/// <summary>
/// Event-driven ("Fast mode") sibling of <see cref="SimulationRunner"/>. Drives
/// the SAME <see cref="DeltaCycleEngine"/> / <see cref="SignalScheduler"/> but,
/// instead of visiting every tick <c>0..totalTicks</c>, jumps straight to the
/// next tick where something can happen — the next clock edge, the next
/// stimulus, the next scheduled signal update
/// (<see cref="SignalScheduler.NextScheduledTick"/>) or the next timed process
/// wake (<see cref="DeltaCycleEngine.NextProcessWakeTick"/>). Idle ticks are
/// skipped, collapsing a 10^8-tick seconds-scale run to the number of actual
/// events.
///
/// <para><b>Why this is output-identical to <see cref="SimulationRunner"/>.</b>
/// The engine's two event queues both drain everything due at
/// <c>&lt;= currentTick</c> (<see cref="SignalScheduler.AdvanceTime"/> and
/// <c>DeltaCycleEngine.DrainReadyProcessWakesAsync</c>), so jumping the tick
/// forward is safe. An idle tick produces no clock/stimulus/scheduled/wake
/// activity, hence no delta and no history or signal change — so not visiting it
/// changes nothing observable (values, transition times, VCD/CSV). The attribute
/// state (`'stable(T)` / `'last_value` / `'event`) is timestamp-based
/// (<c>_currentTimeMoment</c> / <c>_lastChangeMoments</c>), not visit-count
/// based, so skipping idle ticks leaves it unchanged. This runner is a NEW file:
/// it modifies neither <see cref="SimulationRunner"/> nor the engine core (only
/// consumes the read-only <see cref="DeltaCycleEngine.NextProcessWakeTick"/>
/// accessor). Its byte-identical equivalence to the reference runner is verified
/// by VCD diff across a corpus before Fast mode is enabled.</para>
///
/// The public surface (events, Pause/Resume/Stop, Dispose) mirrors
/// <see cref="SimulationRunner"/> exactly so callers can swap the two.
/// </summary>
public class FastSimulationRunner : IDisposable, ISimulationRunner
{
    private DeltaCycleEngine? _engine;
    private CancellationTokenSource _cts = new();
    private TaskCompletionSource? _pauseTcs;
    private volatile bool _isPaused;
    private volatile bool _isRunning;

    /// <summary>The delta cycle engine used for this simulation run.</summary>
    public DeltaCycleEngine? Engine => _engine;

    /// <summary>Whether the simulation is currently running.</summary>
    public bool IsRunning => _isRunning;

    /// <summary>Whether the simulation is currently paused.</summary>
    public bool IsPaused => _isPaused;

    /// <summary>
    /// Diagnostic: how many ticks were actually visited (RunTimeStepAsync'd) in
    /// the last run. Compared against <c>config.TotalTicks</c> this shows how
    /// much idle time the event-driven loop skipped — a small ratio means Fast
    /// mode is paying off, a ratio near 1.0 means the design has activity almost
    /// every tick (e.g. a clock at the tick resolution) so there is nothing to
    /// skip and Fast's only win is the reduced live-UI overhead.
    /// </summary>
    public int VisitedTicks { get; private set; }

    /// <summary>Fired when progress changes (0.0 to 100.0).</summary>
    public event Action<double>? OnProgressChanged;

    /// <summary>Fired when simulation ends. Parameter: true if completed normally, false if stopped.</summary>
    public event Action<bool>? OnSimulationEnded;

    /// <summary>Fired on error during simulation.</summary>
    public event Action<string>? OnError;

    /// <summary>
    /// Fires once per <see cref="RunAsync"/> right after the engine is
    /// constructed + initialised (mirror of
    /// <see cref="SimulationRunner.OnEngineReady"/>).
    /// </summary>
    public event Action<DeltaCycleEngine>? OnEngineReady;

    /// <summary>Fires for each VHDL <c>report</c> statement (mirror of <see cref="SimulationRunner.OnReport"/>).</summary>
    public event Action<string>? OnReport;

    /// <summary>
    /// Runs the simulation to completion, jumping over idle ticks. Setup is a
    /// faithful copy of <see cref="SimulationRunner.RunAsync"/>; only the main
    /// loop differs (event-driven jump vs. <c>tick++</c>).
    /// </summary>
    public async Task RunAsync(SimulationConfig config)
    {
        if (_isRunning)
            throw new InvalidOperationException("Simulation is already running.");

        _isRunning = true;
        _isPaused = false;
        _cts = new CancellationTokenSource();

        // Opt-in wall-clock cancel (parity with SimulationRunner).
        var wallEnv = Environment.GetEnvironmentVariable("KMILA_SIM_WALL_TIMEOUT_MS");
        if (int.TryParse(wallEnv, out int wallMs) && wallMs > 0)
        {
            _cts.CancelAfter(TimeSpan.FromMilliseconds(wallMs));
        }

        void ReportForwarder(string msg) => OnReport?.Invoke(msg);
        ReportBus.Instance.OnReport += ReportForwarder;

        try
        {
            var entity = config.Entity;
            var arch = entity.Architecture
                ?? throw new InvalidOperationException($"Entity '{entity.Name}' has no architecture.");

            // Collect all signals.
            HashSet<Variable> allSignals = [.. entity.Ports, .. arch.Signals];

            // Seed uninitialized signals to safe defaults (identical policy to
            // SimulationRunner — see its comments for the rationale).
            var configuredSignals = new HashSet<Variable>(
                config.Clocks.Select(c => c.Signal)
                    .Concat(config.Stimuli.Select(s => s.Signal))
            );

            foreach (var signal in allSignals)
            {
                if (configuredSignals.Contains(signal))
                    continue;

                string? existing = signal.Value?.ToString();
                bool needsDefault =
                    string.IsNullOrEmpty(existing) ||
                    existing.All(c => c == 'U' || c == 'u') ||
                    existing == "System.Object";
                if (!needsDefault && !signal.IsPort)
                    continue;

                string defaultVal = signal.Size > 1 ? new string('0', signal.Size) : "0";

                if (signal.Name.Equals("reset", StringComparison.OrdinalIgnoreCase) ||
                    signal.Name.Equals("rst", StringComparison.OrdinalIgnoreCase))
                {
                    defaultVal = "0";
                }
                else if (signal.Name.Equals("enable", StringComparison.OrdinalIgnoreCase) ||
                         signal.Name.Equals("en", StringComparison.OrdinalIgnoreCase))
                {
                    defaultVal = "1";
                }

                signal.Value = defaultVal;
            }

            _engine = new DeltaCycleEngine();
            _engine.Initialize(allSignals, arch.Tasks);

            _cts.Token.Register(() => _engine?.Stop());

            // Apply clock initial states (observed as a real transition).
            foreach (var clk in config.Clocks)
            {
                clk.Signal.Value = clk.InitialHigh ? "1" : "0";
            }

            // Apply stimuli at tick 0.
            foreach (var stim in config.Stimuli.Where(s => s.Tick == 0))
            {
                stim.Signal.Value = stim.Value;
            }

            OnEngineReady?.Invoke(_engine);

            var totalTicks = config.TotalTicks;
            double lastReportedPercent = -1;

            var stimuliByTick = config.Stimuli
                .GroupBy(s => s.Tick)
                .ToDictionary(g => g.Key, g => g.ToList());

            // Distinct stimulus ticks in ascending order, walked by a forward
            // pointer since the visited tick only ever increases.
            var stimTicks = stimuliByTick.Keys.OrderBy(t => t).ToList();
            int stimIdx = 0;

            // ── Event-driven main loop ───────────────────────────────────────
            // Always visit tick 0 first (initial process wakes + combinational
            // settling live there), then jump to each subsequent event tick.
            int visited = 0;
            for (ulong tick = 0; tick < totalTicks; )
            {
                if (_cts.IsCancellationRequested)
                {
                    OnSimulationEnded?.Invoke(false);
                    return;
                }

                if (_isPaused)
                {
                    _pauseTcs = new TaskCompletionSource();
                    try { await _pauseTcs.Task; }
                    catch (TaskCanceledException)
                    {
                        OnSimulationEnded?.Invoke(false);
                        return;
                    }
                }

                // Per-tick body — identical to SimulationRunner's, so a visited
                // tick behaves exactly as it would in the reference runner.
                var directlyChanged = new HashSet<Variable>();

                foreach (var clk in config.Clocks)
                {
                    if (tick > 0 && clk.HalfPeriodTicks > 0 && tick % clk.HalfPeriodTicks == 0)
                    {
                        clk.Signal.Value = clk.Signal.Value?.ToString() == "1" ? "0" : "1";
                        directlyChanged.Add(clk.Signal);
                    }
                }

                if (stimuliByTick.TryGetValue(tick, out var events))
                {
                    foreach (var stim in events)
                    {
                        stim.Signal.Value = stim.Value;
                        directlyChanged.Add(stim.Signal);
                    }
                }

                if (directlyChanged.Count > 0)
                {
                    _engine.History.RecordChanges(directlyChanged, (long)tick * 1000);
                }

                _engine.AdvanceTime(tick);
                await _engine.RunTimeStepAsync();

                // Progress by sim-time position (event ticks are sparse, so the
                // bar advances in jumps — fine for a headless "fast" run).
                double percent = (double)(tick + 1) / totalTicks * 100.0;
                double rounded = Math.Floor(percent);
                if (rounded > lastReportedPercent)
                {
                    lastReportedPercent = rounded;
                    OnProgressChanged?.Invoke(percent);
                }

                if (++visited % 100 == 0)
                {
                    await Task.Yield();
                }
                VisitedTicks = visited;

                // Compute the next interesting tick strictly greater than the
                // current one; break when nothing remains before totalTicks.
                ulong? next = NextEventTick(tick, config, stimTicks, ref stimIdx);
                if (next is null || next.Value >= totalTicks)
                    break;
                tick = next.Value;
            }

            // Seal the end of the window so the trace spans the full duration
            // even for stable designs (identical to SimulationRunner).
            if (totalTicks > 0 && _engine?.History != null)
            {
                _engine.History.RecordChanges(allSignals, (long)(totalTicks - 1) * 1000);
            }

            OnProgressChanged?.Invoke(100.0);
            OnSimulationEnded?.Invoke(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var st = ex.StackTrace ?? string.Empty;
            var firstFrame = st.Split('\n').FirstOrDefault()?.Trim() ?? string.Empty;
            OnError?.Invoke($"{ex.GetType().Name}: {ex.Message} | {firstFrame}");
            OnSimulationEnded?.Invoke(false);
        }
        finally
        {
            ReportBus.Instance.OnReport -= ReportForwarder;
            _isRunning = false;
            _isPaused = false;
        }
    }

    /// <summary>
    /// The smallest tick strictly greater than <paramref name="current"/> at
    /// which anything can happen: the next edge of any clock, the next stimulus
    /// tick, the next scheduled signal update, or the next timed process wake.
    /// Returns <c>null</c> when no future event exists (the run can stop). Any
    /// engine-queued tick that is somehow <c>&lt;= current</c> (e.g. a same-tick
    /// deferral) is clamped to <c>current + 1</c> so the run always progresses
    /// and never jumps PAST a pending wake — matching how the reference runner
    /// would drain it on the very next tick.
    /// </summary>
    private ulong? NextEventTick(ulong current, SimulationConfig config,
        List<ulong> stimTicks, ref int stimIdx)
    {
        ulong? best = null;
        void Consider(ulong candidate)
        {
            if (candidate <= current) candidate = current + 1;
            if (best is null || candidate < best.Value) best = candidate;
        }

        // Next clock edge for each clock: the next multiple of HalfPeriodTicks
        // strictly greater than the current tick.
        foreach (var clk in config.Clocks)
        {
            ulong half = clk.HalfPeriodTicks;
            if (half == 0) continue;
            ulong nextEdge = ((current / half) + 1) * half;
            Consider(nextEdge);
        }

        // Next stimulus tick (forward pointer over the ascending distinct set).
        while (stimIdx < stimTicks.Count && stimTicks[stimIdx] <= current) stimIdx++;
        if (stimIdx < stimTicks.Count) Consider(stimTicks[stimIdx]);

        // Next scheduled signal update (`after N ns`) and next timed process
        // wake (`wait for N ns`) — both absolute ticks queued in the engine.
        var sched = _engine?.SignalScheduler?.NextScheduledTick;
        if (sched.HasValue) Consider(sched.Value);

        var wake = _engine?.NextProcessWakeTick;
        if (wake.HasValue) Consider(wake.Value);

        return best;
    }

    /// <summary>Pauses the simulation.</summary>
    public void Pause()
    {
        if (_isRunning && !_isPaused)
        {
            _isPaused = true;
        }
    }

    /// <summary>Resumes the simulation after pause.</summary>
    public void Resume()
    {
        if (_isRunning && _isPaused)
        {
            _isPaused = false;
            _pauseTcs?.TrySetResult();
        }
    }

    /// <summary>Stops the simulation.</summary>
    public void Stop()
    {
        _cts.Cancel();
        _isPaused = false;
        _pauseTcs?.TrySetCanceled();
    }

    /// <summary>Disposes resources.</summary>
    public void Dispose()
    {
        Stop();
        _engine?.Dispose();
        _cts.Dispose();
    }
}
