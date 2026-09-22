using Parser.Models;

namespace Interpreter.Services;

/// <summary>
/// Manages process execution based on sensitivity lists.
/// Tracks which processes should execute based on signal changes.
/// </summary>
public class ProcessScheduler
{
    /// <summary>
    /// Map from signal to processes sensitive to it.
    /// </summary>
    private readonly Dictionary<Variable, List<Process>> _sensitivityMap = new();

    /// <summary>
    /// Processes that need to execute in current delta cycle.
    /// </summary>
    private readonly HashSet<Process> _pendingProcesses = new();

    /// <summary>
    /// All registered processes.
    /// </summary>
    private readonly List<Process> _allProcesses = new();

    /// <summary>
    /// Lock object for thread safety.
    /// </summary>
    private readonly object _lock = new();

    /// <summary>
    /// Registers a process and builds sensitivity mapping.
    /// </summary>
    /// <param name="process">The process to register.</param>
    public void RegisterProcess(Process process)
    {
        lock (_lock)
        {
            if (_allProcesses.Contains(process))
                return;

            _allProcesses.Add(process);

            // Build sensitivity mapping
            foreach (var signal in process.SensitiveList)
            {
                if (!_sensitivityMap.TryGetValue(signal, out var processes))
                {
                    processes = new List<Process>();
                    _sensitivityMap[signal] = processes;
                }

                if (!processes.Contains(process))
                {
                    processes.Add(process);
                }
            }
        }
    }

    /// <summary>
    /// Marks processes sensitive to changed signals as pending.
    /// </summary>
    /// <param name="changedSignals">The set of signals that changed.</param>
    public void NotifySignalChanges(HashSet<Variable> changedSignals)
    {
        lock (_lock)
        {
            foreach (var signal in changedSignals)
            {
                if (_sensitivityMap.TryGetValue(signal, out var processes))
                {
                    foreach (var process in processes)
                    {
                        _pendingProcesses.Add(process);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Gets and clears the set of processes to execute.
    /// </summary>
    /// <returns>Enumerable of pending processes.</returns>
    public IEnumerable<Process> GetPendingProcesses()
    {
        lock (_lock)
        {
            var pending = _pendingProcesses.ToList();
            _pendingProcesses.Clear();
            return pending;
        }
    }

    /// <summary>
    /// Initializes all processes for first delta cycle.
    /// Called at simulation start to execute all processes once.
    /// </summary>
    public void InitializeAllProcesses()
    {
        lock (_lock)
        {
            foreach (var process in _allProcesses)
            {
                _pendingProcesses.Add(process);
            }
        }
    }

    /// <summary>
    /// Returns true if any processes are pending execution.
    /// </summary>
    public bool HasPendingProcesses
    {
        get
        {
            lock (_lock)
            {
                return _pendingProcesses.Count > 0;
            }
        }
    }

    /// <summary>
    /// Gets the count of pending processes.
    /// </summary>
    public int PendingCount
    {
        get
        {
            lock (_lock)
            {
                return _pendingProcesses.Count;
            }
        }
    }

    /// <summary>
    /// Gets the total count of registered processes.
    /// </summary>
    public int TotalProcessCount
    {
        get
        {
            lock (_lock)
            {
                return _allProcesses.Count;
            }
        }
    }

    /// <summary>
    /// Checks if a process should execute based on changed signals.
    /// </summary>
    /// <param name="process">The process to check.</param>
    /// <param name="changedSignals">The set of changed signals.</param>
    /// <returns>True if the process is sensitive to any changed signal.</returns>
    public bool IsSensitiveTo(Process process, HashSet<Variable> changedSignals)
    {
        return process.SensitiveList.Overlaps(changedSignals);
    }

    /// <summary>
    /// Manually adds a process to the pending set.
    /// </summary>
    /// <param name="process">The process to mark as pending.</param>
    public void MarkPending(Process process)
    {
        lock (_lock)
        {
            if (_allProcesses.Contains(process))
            {
                _pendingProcesses.Add(process);
            }
        }
    }

    /// <summary>
    /// Clears all pending processes.
    /// </summary>
    public void ClearPending()
    {
        lock (_lock)
        {
            _pendingProcesses.Clear();
        }
    }

    /// <summary>
    /// Clears all registered processes and sensitivity mappings.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _allProcesses.Clear();
            _sensitivityMap.Clear();
            _pendingProcesses.Clear();
        }
    }

    /// <summary>
    /// Gets processes sensitive to a specific signal.
    /// </summary>
    /// <param name="signal">The signal to check.</param>
    /// <returns>List of processes sensitive to the signal.</returns>
    public IReadOnlyList<Process> GetProcessesSensitiveTo(Variable signal)
    {
        lock (_lock)
        {
            return _sensitivityMap.TryGetValue(signal, out var processes)
                ? processes.AsReadOnly()
                : Array.Empty<Process>();
        }
    }
}
