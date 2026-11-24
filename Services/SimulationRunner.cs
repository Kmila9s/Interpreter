using Interpreter.Models;
using Parser.Models;

namespace Interpreter.Services;

/// <summary>
/// Drives <see cref="DeltaCycleEngine"/> through a run, event by event: instead of
/// visiting every tick <c>0..TotalTicks</c> it jumps straight to the next tick where
/// something can happen — the next clock edge, the next stimulus, the next scheduled
/// signal update (<see cref="SignalScheduler.NextScheduledTick"/>) or the next timed
/// process wake (<see cref="DeltaCycleEngine.NextProcessWakeTick"/>).
///
/// <para>Skipping an idle tick changes nothing observable: both engine queues drain
/// everything due at <c>&lt;= currentTick</c> (<see cref="SignalScheduler.AdvanceTime"/>
/// and the process-wake drain), an idle tick produces no delta, no history entry and
/// no signal change, and the attribute state (<c>'stable(T)</c>, <c>'last_value</c>,
/// <c>'event</c>) is timestamp-based, not visit-count based. So the run costs the
/// number of events, not the length of the window — which also lets the tick (a whole
/// number of femtoseconds) be as fine as the design's delays need (G103).</para>
/// </summary>
public class SimulationRunner : IDisposable
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
    /// Diagnostic: how many ticks were actually visited in the last run. Compared
    /// against <c>config.TotalTicks</c> it shows how much idle time was skipped.
    /// </summary>
    public int VisitedTicks { get; private set; }

    /// <summary>Fired when progress changes (0.0 to 100.0).</summary>
    public event Action<double>? OnProgressChanged;

    /// <summary>Fired when simulation ends. Parameter: true if completed normally, false if stopped.</summary>
    public event Action<bool>? OnSimulationEnded;

    /// <summary>Fired on error during simulation.</summary>
    public event Action<string>? OnError;

    /// <summary>The error that aborted the last <see cref="RunAsync"/> (the text <see cref="OnError"/> carries), or null when it completed or was stopped.</summary>
    public string? Error { get; private set; }

    /// <summary>
    /// Fires once per <see cref="RunAsync"/> right after the engine is constructed and
    /// initialised, so the UI (Replay tab) can wire pause / resume / step to it. The
    /// reference is invalid once the run completes (pair with <see cref="OnSimulationEnded"/>).
    /// </summary>
    public event Action<DeltaCycleEngine>? OnEngineReady;

    /// <summary>
    /// Fires for each VHDL <c>report</c>, forwarded from <see cref="ReportBus"/> for the
    /// lifetime of the run only (the static bus would otherwise keep stale listeners).
    /// </summary>
    public event Action<string>? OnReport;

    /// <summary>
    /// One-line description of a run failure: type, message and the first stack frame,
    /// so headless callers can pinpoint it rather than see a generic message.
    /// </summary>
    internal static string DescribeFailure(Exception ex, ElaborationLineMap? lines = null)
    {
        var firstFrame = (ex.StackTrace ?? string.Empty).Split('\n').FirstOrDefault()?.Trim() ?? string.Empty;
        // An elaborated design's error names the file and line the user wrote (G82).
        var located = lines?.Locate(ex) ?? ex;
        return $"{located.GetType().Name}: {located.Message} | {firstFrame}";
    }

    /// <summary>
    /// Seals the trace at the last simulated tick — the end of the window
    /// (<paramref name="totalTicks"/>, simulated: the window is <c>0..totalTicks</c>
    /// inclusive, as GHDL's <c>--stop-time</c>), or the tick where the design ended the
    /// run (<c>std.env.stop</c>/<c>finish</c>, <c>severity failure</c>) — so the trace
    /// spans the whole run even for a stable design. Returns the error of a
    /// <c>severity failure</c> end, else null.
    /// </summary>
    private static string? SealRun(DeltaCycleEngine engine, IEnumerable<Variable> allSignals, ulong totalTicks, ulong? endTick)
    {
        engine.History.RecordChanges(allSignals, (long)(endTick ?? totalTicks) * 1000);
        return engine.EndRequest is { Failed: true } failure ? $"severity failure: {failure.Message}" : null;
    }

    /// <summary>Runs the simulation described by <paramref name="config"/> to completion.</summary>
    public async Task RunAsync(SimulationConfig config)
    {
        if (_isRunning)
            throw new InvalidOperationException("Simulation is already running.");

        _isRunning = true;
        Error = null;
        _isPaused = false;
        _cts = new CancellationTokenSource();

        // Opt-in wall-clock cancel: KMILA_SIM_WALL_TIMEOUT_MS=<ms> (0/unset = off).
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

            HashSet<Variable> allSignals = [.. entity.Ports, .. arch.Signals];

            InitialValueSeeder.Prepare(config);

            // Register signals before applying clock initial states / tick-0 stimuli, so
            // the U→initial transition is seen (rising_edge(clk) at tick 0, bug #15).
            _engine = new DeltaCycleEngine { TickFs = config.TickFs };
            _engine.Initialize(allSignals, arch.Tasks);

            // The runner's cancel also breaks a long delta-cycle loop inside the engine.
            _cts.Token.Register(() => _engine?.Stop());

            // Clock initial states (observed as a real transition).
            foreach (var clk in config.Clocks)
            {
                clk.Signal.Value = clk.InitialHigh ? "1" : "0";
            }

            foreach (var stim in config.Stimuli.Where(s => s.Tick == 0))
            {
                stim.Apply(_engine?.SignalScheduler);
            }

            OnEngineReady?.Invoke(_engine);

            var totalTicks = config.TotalTicks;
            double lastReportedPercent = -1;

            var stimuliByTick = config.Stimuli
                .GroupBy(s => s.Tick)
                .ToDictionary(g => g.Key, g => g.ToList());

            // Distinct stimulus ticks in ascending order, walked by a forward pointer
            // since the visited tick only ever increases.
            var stimTicks = stimuliByTick.Keys.OrderBy(t => t).ToList();
            int stimIdx = 0;

            // Tick 0 is always visited (initial process wakes and combinational
            // settling live there); then the loop jumps from event to event.
            VisitedTicks = 0;
            ulong? endTick = null;
            // Signals written directly in a tick (clocks, stimuli) bypass the
            // scheduler, so they are recorded in the history here.
            var directlyChanged = new HashSet<Variable>();
            for (ulong tick = 0; tick <= totalTicks; )
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

                directlyChanged.Clear();

                foreach (var clk in config.Clocks)
                {
                    if (tick > 0 && clk.HalfPeriodTicks > 0 && tick % clk.HalfPeriodTicks == 0)
                    {
                        clk.Signal.Value = clk.Signal.Value?.ToString() == "1" ? "0" : "1";
                        directlyChanged.Add(clk.Signal);
                    }
                    else if (tick == 0)
                    {
                        directlyChanged.Add(clk.Signal); // its initial level, set before the loop
                    }
                }

                if (stimuliByTick.TryGetValue(tick, out var events))
                {
                    foreach (var stim in events)
                    {
                        stim.Apply(_engine?.SignalScheduler);
                        directlyChanged.Add(stim.Signal);
                    }
                }

                if (directlyChanged.Count > 0)
                {
                    _engine.History.RecordChanges(directlyChanged, (long)tick * 1000);
                }

                _engine.AdvanceTime(tick);
                await _engine.RunTimeStepAsync();
                if (_engine.EndRequest is not null) { endTick = tick; break; }

                // Progress by simulated-time position (it advances in jumps).
                double percent = (double)(tick + 1) / (totalTicks + 1) * 100.0;
                double rounded = Math.Floor(percent);
                if (rounded > lastReportedPercent)
                {
                    lastReportedPercent = rounded;
                    OnProgressChanged?.Invoke(percent);
                }

                if (++VisitedTicks % 100 == 0)
                {
                    await Task.Yield();
                }

                ulong? next = NextEventTick(tick, config, stimTicks, ref stimIdx);
                if (next is null || next.Value > totalTicks)
                    break;
                tick = next.Value;
            }

            if (SealRun(_engine, allSignals, totalTicks, endTick) is { } failure)
            {
                Error = failure;
                OnError?.Invoke(Error);
                OnSimulationEnded?.Invoke(false);
                return;
            }

            OnProgressChanged?.Invoke(100.0);
            OnSimulationEnded?.Invoke(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error = DescribeFailure(ex, config.LineMap);
            OnError?.Invoke(Error);
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
    /// The smallest tick strictly greater than <paramref name="current"/> at which
    /// anything can happen: the next edge of any clock, the next stimulus tick, the
    /// next scheduled signal update, or the next timed process wake; null when no
    /// future event exists. An engine-queued tick <c>&lt;= current</c> (a same-tick
    /// deferral) is clamped to <c>current + 1</c>, so the run always progresses and
    /// never jumps past a pending wake.
    /// </summary>
    private ulong? NextEventTick(ulong current, SimulationConfig config, List<ulong> stimTicks, ref int stimIdx)
    {
        ulong? best = null;
        void Consider(ulong candidate)
        {
            if (candidate <= current) candidate = current + 1;
            if (best is null || candidate < best.Value) best = candidate;
        }

        foreach (var clk in config.Clocks)
        {
            ulong half = clk.HalfPeriodTicks;
            if (half == 0) continue;
            Consider(((current / half) + 1) * half);
        }

        while (stimIdx < stimTicks.Count && stimTicks[stimIdx] <= current) stimIdx++;
        if (stimIdx < stimTicks.Count) Consider(stimTicks[stimIdx]);

        if (_engine?.SignalScheduler?.NextScheduledTick is { } sched) Consider(sched);
        if (_engine?.NextProcessWakeTick is { } wake) Consider(wake);

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
