using Interpreter.Interfaces;
using Interpreter.Services;

namespace Interpreter.Repositories;

public class Executer : ISControl, IDisposable
{
    private bool _isRunning = true;
    private readonly List<object> Tasks = new();
    private readonly CancellationTokenSource _cts = new();
    private CancellationTokenSource _pauseCts = new();
    public Executer(List<object> tasks)
    {
        Tasks = tasks;
        TimeMachine.Repositories.Timer.OnPaused += OnPaused;
    }
    public async Task ExecuteAsync()
    {
        int tasks = Tasks.Count;
        for (int i = 0; i < tasks && !_cts.IsCancellationRequested;)
        {
            if (_isRunning)
            {
                var task = Tasks[i];
                if (task is Operation operation) operation.Execute();
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
    private void OnPaused(bool e)
    {
        _isRunning = e;
    }
    public void Syntetize(string[] data) => Syntetize();
    public void Syntetize() => throw new NotImplementedException();
    public void Dispose()
    {
        _cts?.Cancel();
        _pauseCts?.Cancel();
        _cts?.Dispose();
        _pauseCts?.Dispose();   
    }
}