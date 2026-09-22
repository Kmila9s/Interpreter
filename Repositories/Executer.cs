using Interpreter.Interfaces;
using Interpreter.Services;

namespace Interpreter.Repositories;

/// <summary>
/// Orchestrates the execution of a series of tasks, which can be operations or control structures.
/// It supports pausing, resuming, and canceling the execution flow.
/// </summary>
public class Executer : ISControl, IDisposable
{
    /// <summary>Indicates whether the executer is currently running (true) or paused (false).</summary>
    private bool _isRunning = true;
    /// <summary>The list of tasks (operations and control structures) to execute sequentially.</summary>
    private readonly List<object> Tasks = new();
    /// <summary>Cancellation token source used to cancel the entire execution flow.</summary>
    private readonly CancellationTokenSource _cts = new();
    /// <summary>Cancellation token source used to resume execution after a pause.</summary>
    private CancellationTokenSource _pauseCts = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="Executer"/> class.
    /// </summary>
    /// <param name="tasks">A list of tasks (as objects) to be executed.</param>
    public Executer(List<object> tasks)
    {
        Tasks = tasks;
        TimeMachine.Repositories.Timer.OnPaused += OnPaused;
    }

    /// <summary>
    /// Executes the list of tasks asynchronously. It iterates through the tasks, executing them one by one,
    /// and handles the pause state.
    /// </summary>
    /// <returns>A task representing the asynchronous execution of all tasks.</returns>
    public async Task ExecuteAsync()
    {
        int tasks = Tasks.Count;
        for (int i = 0; i < tasks && !_cts.IsCancellationRequested;)
        {
            if (_isRunning)
            {
                var task = Tasks[i];
                if (task is Operation operation) operation.Execute();
                else if (task is ScheduledOperation scheduledOp) scheduledOp.Execute();
                else if (task is ISControl sControl) await sControl.ExecuteAsync();
                i++;
            }
            else
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, _pauseCts.Token);
                }
                finally
                {
                    _pauseCts.TryReset();
                }
            }
        }
    }

    /// <summary>
    /// Event handler for the OnPaused event from the Timer. Updates the running state of the executer.
    /// </summary>
    /// <param name="e">A boolean indicating if the simulation is paused (false) or running (true).</param>
    private void OnPaused(bool e)
    {
        _isRunning = e;
    }

    /// <summary>
    /// Synthesizes the behavior from an array of strings. This method is not implemented.
    /// </summary>
    /// <param name="data">The raw data for synthesis.</param>
    public void Syntetize(string[] data) => Syntetize();

    /// <summary>
    /// Synthesizes the behavior. This method is not implemented and will throw an exception.
    /// </summary>
    public void Syntetize() => throw new NotImplementedException();

    /// <summary>
    /// Releases the resources used by the Executer, canceling any ongoing tasks.
    /// </summary>
    public void Dispose()
    {
        // Timer.OnPaused is a static event — without this -= every Executer
        // instance stays rooted for the lifetime of the process, leaking the
        // executer + everything it transitively references on each new run.
        TimeMachine.Repositories.Timer.OnPaused -= OnPaused;

        _cts?.Cancel();
        _pauseCts?.Cancel();
        _cts?.Dispose();
        _pauseCts?.Dispose();
    }
}