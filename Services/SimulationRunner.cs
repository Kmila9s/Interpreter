using Interpreter.Models;
using Parser.Models;

namespace Interpreter.Services;

/// <summary>
/// Drives DeltaCycleEngine at full CPU speed for cycle-accurate simulation.
/// Bypasses TimeMachine's 10ms-per-tick delay for fast batch simulation.
/// </summary>
public class SimulationRunner : IDisposable, ISimulationRunner
{
    private DeltaCycleEngine? _engine;
    private CancellationTokenSource _cts = new();
    private TaskCompletionSource? _pauseTcs;
    private volatile bool _isPaused;
    private volatile bool _isRunning;

    /// <summary>
    /// The delta cycle engine used for this simulation run.
    /// </summary>
    public DeltaCycleEngine? Engine => _engine;

    /// <summary>
    /// Whether the simulation is currently running.
    /// </summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// Whether the simulation is currently paused.
    /// </summary>
    public bool IsPaused => _isPaused;

    /// <summary>
    /// Fired when progress changes (0.0 to 100.0).
    /// </summary>
    public event Action<double>? OnProgressChanged;

    /// <summary>
    /// Fired when simulation ends. Parameter: true if completed normally, false if stopped.
    /// </summary>
    public event Action<bool>? OnSimulationEnded;

    /// <summary>
    /// Fired on error during simulation.
    /// </summary>
    public event Action<string>? OnError;

    /// <inheritdoc />
    public string? Error { get; private set; }

    /// <summary>
    /// One-line description of a run failure: type, message and the first
    /// stack frame, so headless callers can pinpoint it rather than just see
    /// a generic "Object reference not set". Shared by both runners.
    /// </summary>
    internal static string DescribeFailure(Exception ex)
    {
        var firstFrame = (ex.StackTrace ?? string.Empty).Split('\n').FirstOrDefault()?.Trim() ?? string.Empty;
        return $"{ex.GetType().Name}: {ex.Message} | {firstFrame}";
    }

    /// <summary>
    /// Phase B (2026-05-19): fires once per <see cref="RunAsync"/> right
    /// after <see cref="_engine"/> is constructed + initialised. Lets the UI
    /// (Editor's Replay tab) grab a reference to the engine for the live
    /// pause/resume controls. The reference becomes invalid once the run
    /// completes — pair this with <see cref="OnSimulationEnded"/> for the
    /// detach.
    /// </summary>
    public event Action<DeltaCycleEngine>? OnEngineReady;

    /// <summary>
    /// F-G-11 (2026-07-18): fires each time a VHDL <c>report</c> statement
    /// executes in a process body. Forwarded from <see cref="ReportBus"/>,
    /// which <see cref="Interpreter.Repositories.ReportStatement.ExecuteAsync"/>
    /// publishes to. Subscribe here (rather than to <see cref="ReportBus"/>
    /// directly) so the subscription automatically clears when the run
    /// completes — a static bus keeps subscribers alive across runs
    /// otherwise, which pollutes downstream logs.
    /// </summary>
    public event Action<string>? OnReport;

    /// <summary>
    /// Runs the simulation to completion based on the provided config.
    /// Follows the proven pattern from Interpreter/Program.cs:RunSimulation.
    /// </summary>
    public async Task RunAsync(SimulationConfig config)
    {
        if (_isRunning)
            throw new InvalidOperationException("Simulation is already running.");

        _isRunning = true;
        Error = null;
        _isPaused = false;
        _cts = new CancellationTokenSource();

        // F-G-08 pass 3 diag (2026-07-16): opt-in wall-clock cancel. Set
        // KMILA_SIM_WALL_TIMEOUT_MS=<ms> to auto-cancel the run after N ms
        // — the diagnostic summary the engine emits will then reflect
        // however many ticks/deltas managed to run in that window. Off by
        // default (0 = disabled).
        var wallEnv = Environment.GetEnvironmentVariable("KMILA_SIM_WALL_TIMEOUT_MS");
        if (int.TryParse(wallEnv, out int wallMs) && wallMs > 0)
        {
            _cts.CancelAfter(TimeSpan.FromMilliseconds(wallMs));
        }

        // F-G-11 (2026-07-18): bridge ReportBus → this runner's OnReport for
        // the lifetime of the run. Unsubscribe in finally so the static bus
        // doesn't accumulate stale listeners across simulate calls.
        void ReportForwarder(string msg) => OnReport?.Invoke(msg);
        ReportBus.Instance.OnReport += ReportForwarder;

        try
        {
            var entity = config.Entity;
            var arch = entity.Architecture
                ?? throw new InvalidOperationException($"Entity '{entity.Name}' has no architecture.");

            // Collect all signals
            HashSet<Variable> allSignals = [.. entity.Ports, .. arch.Signals];

            // Initialize ALL signals to safe defaults to avoid 'U' meta-value parsing errors.
            // In real VHDL, uninitialized signals are 'U', but the interpreter's expression
            // evaluation needs numeric values for arithmetic/comparisons.
            var configuredSignals = new HashSet<Variable>(
                config.Clocks.Select(c => c.Signal)
                    .Concat(config.Stimuli.Select(s => s.Signal))
            );

            foreach (var signal in allSignals)
            {
                // Skip signals that will be set by clock or stimulus configs
                if (configuredSignals.Contains(signal))
                    continue;

                // Bug #5E: respect declared initial values. If
                // VariableSyntetizer populated a non-trivial Value (e.g.
                // `signal r : std_logic_vector(3 downto 0) := "0001";` or
                // `signal state : state_t := sA;`), don't clobber it. Only
                // seed signals that arrived here uninitialized.
                string? existing = signal.Value?.ToString();
                bool needsDefault =
                    string.IsNullOrEmpty(existing) ||
                    existing.All(c => c == 'U' || c == 'u') ||
                    existing == "System.Object";
                if (!needsDefault && !signal.IsPort)
                    continue;

                string defaultVal = signal.Size > 1 ? new string('0', signal.Size) : "0";

                // Smart defaults for common signal names
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

            // Bug #15 fix: register signals BEFORE applying clock initial
            // states / tick-0 stim. Otherwise the U→initial transition is
            // masked at registration time and `rising_edge(clk)` returns
            // false at tick 0, so process bodies that depend on it (e.g.
            // a 052-style PISO with `Q<=r(3)` outside the edge gate) miss
            // their first load.
            _engine = new DeltaCycleEngine();
            _engine.Initialize(allSignals, arch.Tasks);

            // F-G-08 pass 3 diag (2026-07-16): propagate the runner's cancel
            // into the engine so KMILA_SIM_WALL_TIMEOUT_MS actually breaks
            // out of a long delta-cycle loop (RunTimeStepAsync's loop condition
            // checks its own _cts, not the runner's).
            _cts.Token.Register(() => _engine?.Stop());

            // Apply clock initial states (observed as a real transition)
            foreach (var clk in config.Clocks)
            {
                clk.Signal.Value = clk.InitialHigh ? "1" : "0";
            }

            // Apply stimuli at tick 0
            foreach (var stim in config.Stimuli.Where(s => s.Tick == 0))
            {
                stim.Signal.Value = stim.Value;
            }

            // Phase B: expose the engine to subscribers (Editor's Replay tab
            // uses this to wire pause / resume / step buttons to the engine).
            // Fired AFTER Initialize so subscribers see a fully-formed engine.
            OnEngineReady?.Invoke(_engine);

            // (t=0 is already recorded by DeltaCycleEngine.Initialize via
            // SignalHistory.RegisterSignals with recordInitial:true; no need
            // to double-seed here. The end-of-sim seal is further down.)

            var totalTicks = config.TotalTicks;
            double lastReportedPercent = -1;

            // Sort stimuli by tick for efficient lookup. Tick-0 events were
            // previously filtered out here — that left inputs at 'U' through
            // the entire sim for purely-combinational designs (gates, MUX,
            // adders…) because the engine never saw a defined starting
            // value. Kmila's StimulusBuilder has been pre-seeding signals as
            // a workaround, but letting the legitimate events through is
            // the right long-term fix; tick-0 stimulus simply means "this
            // signal starts at this value at t=0", and that's perfectly
            // sensible for the main loop to apply.
            var stimuliByTick = config.Stimuli
                .GroupBy(s => s.Tick)
                .ToDictionary(g => g.Key, g => g.ToList());

            // Main simulation loop
            for (ulong tick = 0; tick < totalTicks; tick++)
            {
                // Check cancellation
                if (_cts.IsCancellationRequested)
                {
                    OnSimulationEnded?.Invoke(false);
                    return;
                }

                // Handle pause
                if (_isPaused)
                {
                    _pauseTcs = new TaskCompletionSource();
                    try
                    {
                        await _pauseTcs.Task;
                    }
                    catch (TaskCanceledException)
                    {
                        OnSimulationEnded?.Invoke(false);
                        return;
                    }
                }

                // Collect every signal whose .Value we write directly this
                // tick (clocks + external stimuli) so we can record them in
                // SignalHistory at the tick*1000 moment. Direct writes bypass
                // the scheduler and therefore wouldn't be recorded by the
                // engine's ApplyCurrentUpdates path — previously Kmila
                // synthesized these post-hoc; now they land in history
                // naturally the moment they happen.
                var directlyChanged = new HashSet<Parser.Models.Variable>();

                foreach (var clk in config.Clocks)
                {
                    if (tick > 0 && tick % clk.HalfPeriodTicks == 0)
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
                    // long: tick*1000 overflows int past ~21 ms (tick ≈ 2.147M).
                    _engine.History.RecordChanges(directlyChanged, (long)tick * 1000);
                }

                // Advance time and run delta cycles
                _engine.AdvanceTime(tick);
                await _engine.RunTimeStepAsync();

                // Report progress every percent change
                double percent = (double)(tick + 1) / totalTicks * 100.0;
                double rounded = Math.Floor(percent);
                if (rounded > lastReportedPercent)
                {
                    lastReportedPercent = rounded;
                    OnProgressChanged?.Invoke(percent);
                }

                // Yield every 100 ticks for UI responsiveness
                if (tick % 100 == 0)
                {
                    await Task.Yield();
                }
            }

            // Seal the end of the simulation window in history. Without this,
            // WaveformData.FromSignalHistory computes `maxTick` from the last
            // recorded change — for a stable combinational design with no
            // stimulus and no clock, that's tick 0, so the rendered waveform
            // collapses to a single-tick strip even if totalTicks was 100k.
            // Recording every signal at the final tick makes the trace span
            // the full run duration as the student expects.
            if (totalTicks > 0 && _engine?.History != null)
            {
                _engine.History.RecordChanges(allSignals, (long)(totalTicks - 1) * 1000);
            }

            OnProgressChanged?.Invoke(100.0);
            OnSimulationEnded?.Invoke(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error = DescribeFailure(ex);
            OnError?.Invoke(Error);
            OnSimulationEnded?.Invoke(false);
        }
        finally
        {
            // F-G-11: tear down the ReportBus bridge so the static event
            // doesn't retain this closure across runs.
            ReportBus.Instance.OnReport -= ReportForwarder;
            _isRunning = false;
            _isPaused = false;
        }
    }

    /// <summary>
    /// Pauses the simulation.
    /// </summary>
    public void Pause()
    {
        if (_isRunning && !_isPaused)
        {
            _isPaused = true;
        }
    }

    /// <summary>
    /// Resumes the simulation after pause.
    /// </summary>
    public void Resume()
    {
        if (_isRunning && _isPaused)
        {
            _isPaused = false;
            _pauseTcs?.TrySetResult();
        }
    }

    /// <summary>
    /// Stops the simulation.
    /// </summary>
    public void Stop()
    {
        _cts.Cancel();
        _isPaused = false;
        _pauseTcs?.TrySetCanceled();
    }

    /// <summary>
    /// Disposes resources.
    /// </summary>
    public void Dispose()
    {
        Stop();
        _engine?.Dispose();
        _cts.Dispose();
    }
}
