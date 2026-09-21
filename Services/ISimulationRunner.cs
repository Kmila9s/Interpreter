using Interpreter.Models;

namespace Interpreter.Services;

/// <summary>
/// Shared surface of the two interchangeable run drivers —
/// <see cref="SimulationRunner"/> (Visual/Slow mode: visits every tick) and
/// <see cref="FastSimulationRunner"/> (Fast mode: jumps over idle ticks). Both
/// drive the same <see cref="DeltaCycleEngine"/> and, by design + test
/// (<c>FastRunnerEquivalenceTests</c>), produce byte-identical waveforms; this
/// interface lets callers (e.g. <c>ProgramBuilder</c>) pick one at run time
/// without branching on the concrete type. Declaring it on the two runners is
/// structural only — it adds no members and changes no behaviour.
/// </summary>
public interface ISimulationRunner : IDisposable
{
    /// <summary>The delta cycle engine used for the current run.</summary>
    DeltaCycleEngine? Engine { get; }

    /// <summary>Whether the simulation is currently running.</summary>
    bool IsRunning { get; }

    /// <summary>Whether the simulation is currently paused.</summary>
    bool IsPaused { get; }

    /// <summary>Fired when progress changes (0.0 to 100.0).</summary>
    event Action<double>? OnProgressChanged;

    /// <summary>Fired when simulation ends (true = completed, false = stopped).</summary>
    event Action<bool>? OnSimulationEnded;

    /// <summary>Fired on error during simulation.</summary>
    event Action<string>? OnError;

    /// <summary>Fired once the engine is constructed + initialised.</summary>
    event Action<DeltaCycleEngine>? OnEngineReady;

    /// <summary>Fired for each VHDL <c>report</c> statement.</summary>
    event Action<string>? OnReport;

    /// <summary>Runs the simulation to completion for the given config.</summary>
    Task RunAsync(SimulationConfig config);

    /// <summary>Pauses the simulation.</summary>
    void Pause();

    /// <summary>Resumes the simulation after a pause.</summary>
    void Resume();

    /// <summary>Stops the simulation.</summary>
    void Stop();
}
