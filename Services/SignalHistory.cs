using Parser.Models;

namespace Interpreter.Services;

/// <summary>
/// Records signal/variable values over simulation time for waveform analysis.
/// Stores a history of (time, value) pairs for each tracked signal.
/// </summary>
public class SignalHistory
{
    /// <summary>
    /// The raw signal history data.
    /// Key: Variable (signal)
    /// Value: List of (timeMoment, value) tuples
    ///
    /// timeMoment is <c>long</c> (widened from <c>int</c>, 2026-09-13): the
    /// engine encodes it as <c>tick*1000 + delta</c>, which overflows a 32-bit
    /// int past tick ~2.1M (~21 ms at 10 ns/tick), corrupting waveform/VCD
    /// timestamps on long (seconds-scale) runs. This is a storage/observability
    /// width change only — it does NOT touch simulation semantics: the
    /// SignalAttributeHandler path (<c>'stable(T)</c>, <c>'last_value</c>) keeps
    /// its own int timestamp untouched, so on short runs (no overflow) every
    /// exported value and timestamp is byte-identical to before.
    /// </summary>
    private readonly Dictionary<Variable, List<Tuple<long, object>>> _history = new();

    /// <summary>
    /// Phase B+ Deep AST (2026-05-19): parallel list of 1-based source
    /// lines for each transition in <see cref="_history"/>. Indexed
    /// identically: <c>_sourceLines[signal][i]</c> is the line of the
    /// operation that produced <c>_history[signal][i]</c>. Zero means
    /// unknown (synthetic / legacy paths). Kept separate from
    /// <see cref="_history"/> so the existing consumers
    /// (<see cref="GetSignalHistory"/>, <see cref="ReadSince"/>, …) keep
    /// their tuple shape and only the new <see cref="GetSignalHistoryDetailed"/>
    /// reaches in.
    /// </summary>
    private readonly Dictionary<Variable, List<int>> _sourceLines = new();

    /// <summary>
    /// Lock for thread safety.
    /// </summary>
    private readonly object _lock = new();

    /// <summary>
    /// Whether recording is enabled.
    /// </summary>
    public bool IsRecording { get; set; } = true;

    /// <summary>
    /// Read-only view of the raw history dictionary. Returns the live
    /// dictionary as <see cref="IReadOnlyDictionary{TKey,TValue}"/> so
    /// callers can iterate without forcing a copy on every access — the
    /// previous version cloned the entire map (multi-MB on long sims) on
    /// every property read.
    /// </summary>
    public IReadOnlyDictionary<Variable, List<Tuple<long, object>>> RawHistory
    {
        get
        {
            lock (_lock)
            {
                return _history;
            }
        }
    }

    /// <summary>
    /// Registers a signal for tracking. Optionally records its initial value.
    /// </summary>
    /// <param name="signal">The signal to track.</param>
    /// <param name="initialTime">The initial time moment (default 0).</param>
    /// <param name="recordInitial">Whether to record the initial value.</param>
    public void RegisterSignal(Variable signal, long initialTime = 0, bool recordInitial = true)
    {
        lock (_lock)
        {
            if (!_history.ContainsKey(signal))
            {
                _history[signal] = new List<Tuple<long, object>>();
                _sourceLines[signal] = new List<int>();

                if (recordInitial)
                {
                    _history[signal].Add(Tuple.Create(initialTime, signal.Value));
                    _sourceLines[signal].Add(0); // initial value has no source line
                }
            }
        }
    }

    /// <summary>
    /// Registers multiple signals for tracking.
    /// </summary>
    /// <param name="signals">The signals to track.</param>
    /// <param name="initialTime">The initial time moment.</param>
    /// <param name="recordInitial">Whether to record initial values.</param>
    public void RegisterSignals(IEnumerable<Variable> signals, long initialTime = 0, bool recordInitial = true)
    {
        foreach (var signal in signals)
        {
            RegisterSignal(signal, initialTime, recordInitial);
        }
    }

    /// <summary>
    /// Records a value change for a signal at the specified time.
    /// </summary>
    /// <param name="signal">The signal that changed.</param>
    /// <param name="timeMoment">The time moment (tick * 1000 + delta, or custom scheme).</param>
    /// <param name="value">The new value.</param>
    public void RecordChange(Variable signal, long timeMoment, object value)
    {
        RecordChange(signal, timeMoment, value, sourceLine: 0);
    }

    /// <summary>
    /// Phase B+ Deep AST (2026-05-19): records a value change with the
    /// 1-based source line of the operation that produced it. <c>sourceLine
    /// = 0</c> means unknown; the back-compat overload above forwards 0.
    /// </summary>
    public void RecordChange(Variable signal, long timeMoment, object value, int sourceLine)
    {
        if (!IsRecording) return;

        lock (_lock)
        {
            if (!_history.ContainsKey(signal))
            {
                _history[signal] = new List<Tuple<long, object>>();
                _sourceLines[signal] = new List<int>();
            }

            _history[signal].Add(Tuple.Create(timeMoment, value));
            _sourceLines[signal].Add(sourceLine);
        }
    }

    /// <summary>
    /// Records the current value of a signal at the specified time.
    /// </summary>
    /// <param name="signal">The signal to record.</param>
    /// <param name="timeMoment">The time moment.</param>
    public void RecordCurrentValue(Variable signal, long timeMoment)
    {
        RecordChange(signal, timeMoment, signal.Value);
    }

    /// <summary>
    /// Records value changes for multiple signals at the specified time.
    /// </summary>
    /// <param name="signals">The signals that changed.</param>
    /// <param name="timeMoment">The time moment.</param>
    public void RecordChanges(IEnumerable<Variable> signals, long timeMoment)
    {
        foreach (var signal in signals)
        {
            RecordCurrentValue(signal, timeMoment);
        }
    }

    /// <summary>
    /// Phase B+ Deep AST: per-signal recording with source-line tagging.
    /// Each entry's value is taken from the live <see cref="Variable.Value"/>
    /// (matching <see cref="RecordCurrentValue"/>); the line comes from the
    /// dictionary value. Used by <see cref="DeltaCycleEngine"/> after each
    /// delta when it knows which operation produced each change.
    /// </summary>
    public void RecordChangesWithLines(IReadOnlyDictionary<Variable, int> signalsWithLines, long timeMoment)
    {
        // F-G-08 pass 2 (2026-07-16): hoist the per-entry IsRecording check
        // out of the loop. Pass 1's KMILA_SIM_NO_HISTORY=1 opt-out flipped
        // IsRecording, but RecordChange's own guard still paid the dict
        // enumeration + N method calls per delta. Free skip now.
        if (!IsRecording) return;

        foreach (var kv in signalsWithLines)
        {
            RecordChange(kv.Key, timeMoment, kv.Key.Value, kv.Value);
        }
    }

    /// <summary>
    /// Phase B+ Deep AST: returns (time, value, sourceLine) triples for the
    /// signal. Tuples share the same indexing as <see cref="GetSignalHistory"/>,
    /// extended with the source-line column. Returns an empty list when the
    /// signal isn't tracked.
    /// </summary>
    public List<(long TimeMoment, object Value, int SourceLine)> GetSignalHistoryDetailed(Variable signal)
    {
        lock (_lock)
        {
            if (!_history.TryGetValue(signal, out var hist)) return new();
            _sourceLines.TryGetValue(signal, out var lines);
            var result = new List<(long, object, int)>(hist.Count);
            for (int i = 0; i < hist.Count; i++)
            {
                int line = (lines != null && i < lines.Count) ? lines[i] : 0;
                result.Add((hist[i].Item1, hist[i].Item2, line));
            }
            return result;
        }
    }

    /// <summary>
    /// Gets the history for a specific signal.
    /// </summary>
    /// <param name="signal">The signal to query.</param>
    /// <returns>List of (time, value) tuples, or empty list if not tracked.</returns>
    public List<Tuple<long, object>> GetSignalHistory(Variable signal)
    {
        lock (_lock)
        {
            if (_history.TryGetValue(signal, out var history))
            {
                return new List<Tuple<long, object>>(history);
            }
            return new List<Tuple<long, object>>();
        }
    }

    /// <summary>
    /// Reads new entries appended to a signal's history since the caller's
    /// last watermark, under the internal lock — used by the live-waveform
    /// path so that progress refreshes never copy the whole history list.
    /// Returns the new high-water index (i.e. the current Count) so the
    /// caller can pass it back next time.
    /// </summary>
    public int ReadSince(Variable signal, int fromIndex, Action<long, object> onEntry)
    {
        lock (_lock)
        {
            if (!_history.TryGetValue(signal, out var h)) return fromIndex;
            int total = h.Count;
            if (fromIndex < 0) fromIndex = 0;
            for (int i = fromIndex; i < total; i++)
            {
                var e = h[i];
                onEntry(e.Item1, e.Item2);
            }
            return total;
        }
    }

    /// <summary>
    /// Gets the history for a signal by name.
    /// </summary>
    /// <param name="signalName">The signal name.</param>
    /// <returns>List of (time, value) tuples, or empty list if not found.</returns>
    public List<Tuple<long, object>> GetSignalHistoryByName(string signalName)
    {
        lock (_lock)
        {
            var signal = _history.Keys.FirstOrDefault(v =>
                v.Name.Equals(signalName, StringComparison.OrdinalIgnoreCase));

            if (signal is not null)
            {
                return new List<Tuple<long, object>>(_history[signal]);
            }
            return new List<Tuple<long, object>>();
        }
    }

    /// <summary>
    /// Gets the value of a signal at a specific time.
    /// Returns the most recent value at or before the specified time.
    /// </summary>
    /// <param name="signal">The signal to query.</param>
    /// <param name="timeMoment">The time moment.</param>
    /// <returns>The value at that time, or null if not found.</returns>
    public object? GetValueAtTime(Variable signal, long timeMoment)
    {
        lock (_lock)
        {
            if (!_history.TryGetValue(signal, out var history))
                return null;

            // Find the most recent value at or before the specified time
            Tuple<long, object>? result = null;
            foreach (var entry in history)
            {
                if (entry.Item1 <= timeMoment)
                {
                    result = entry;
                }
                else
                {
                    break; // Assuming history is ordered by time
                }
            }

            return result?.Item2;
        }
    }

    /// <summary>
    /// Gets all tracked signal names.
    /// </summary>
    /// <returns>List of signal names.</returns>
    public List<string> GetTrackedSignalNames()
    {
        lock (_lock)
        {
            return _history.Keys.Select(v => v.Name).ToList();
        }
    }

    /// <summary>
    /// Gets all tracked signals.
    /// </summary>
    /// <returns>List of tracked variables.</returns>
    public List<Variable> GetTrackedSignals()
    {
        lock (_lock)
        {
            return _history.Keys.ToList();
        }
    }

    /// <summary>
    /// Gets the time range of recorded data.
    /// </summary>
    /// <returns>Tuple of (minTime, maxTime), or (0, 0) if no data.</returns>
    public (long minTime, long maxTime) GetTimeRange()
    {
        lock (_lock)
        {
            long minTime = long.MaxValue;
            long maxTime = long.MinValue;

            foreach (var history in _history.Values)
            {
                foreach (var entry in history)
                {
                    if (entry.Item1 < minTime) minTime = entry.Item1;
                    if (entry.Item1 > maxTime) maxTime = entry.Item1;
                }
            }

            if (minTime == long.MaxValue)
                return (0, 0);

            return (minTime, maxTime);
        }
    }

    /// <summary>
    /// Gets the total number of recorded samples across all signals.
    /// </summary>
    public int TotalSamples
    {
        get
        {
            lock (_lock)
            {
                return _history.Values.Sum(h => h.Count);
            }
        }
    }

    /// <summary>
    /// Gets the number of tracked signals.
    /// </summary>
    public int SignalCount
    {
        get
        {
            lock (_lock)
            {
                return _history.Count;
            }
        }
    }

    /// <summary>
    /// Clears all recorded history.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _history.Clear();
            _sourceLines.Clear();
        }
    }

    /// <summary>
    /// Clears history but keeps signal registrations.
    /// </summary>
    public void ClearHistory()
    {
        lock (_lock)
        {
            foreach (var key in _history.Keys)
            {
                _history[key].Clear();
                if (_sourceLines.TryGetValue(key, out var lines)) lines.Clear();
            }
        }
    }

    /// <summary>
    /// Exports the history to a simple text format for debugging.
    /// </summary>
    /// <returns>String representation of the history.</returns>
    public string ExportToText()
    {
        lock (_lock)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== Signal History ===");
            sb.AppendLine($"Signals: {_history.Count}, Total Samples: {TotalSamples}");
            sb.AppendLine();

            foreach (var kvp in _history.OrderBy(k => k.Key.Name))
            {
                sb.AppendLine($"Signal: {kvp.Key.Name} ({kvp.Key.Type})");
                foreach (var entry in kvp.Value)
                {
                    sb.AppendLine($"  t={entry.Item1}: {entry.Item2}");
                }
                sb.AppendLine();
            }

            return sb.ToString();
        }
    }

    /// <summary>
    /// Exports history to CSV format for external analysis.
    /// </summary>
    /// <returns>CSV string with time in first column, signals in subsequent columns.</returns>
    public string ExportToCsv()
    {
        lock (_lock)
        {
            var sb = new System.Text.StringBuilder();

            // Get all unique time points
            var allTimes = _history.Values
                .SelectMany(h => h.Select(e => e.Item1))
                .Distinct()
                .OrderBy(t => t)
                .ToList();

            // Header
            var signals = _history.Keys.OrderBy(k => k.Name).ToList();
            sb.Append("Time");
            foreach (var signal in signals)
            {
                sb.Append($",{signal.Name}");
            }
            sb.AppendLine();

            // Data rows
            foreach (var time in allTimes)
            {
                sb.Append(time);
                foreach (var signal in signals)
                {
                    var value = GetValueAtTime(signal, time);
                    sb.Append($",{value ?? ""}");
                }
                sb.AppendLine();
            }

            return sb.ToString();
        }
    }

    /// <summary>
    /// Exports history to VCD (Value Change Dump) format for GTKWave visualization.
    /// </summary>
    /// <param name="moduleName">The name of the module/entity.</param>
    /// <param name="timescaleNs">The timescale in nanoseconds (default 1ns).</param>
    /// <returns>VCD formatted string.</returns>
    public string ExportToVcd(string moduleName = "top", int timescaleNs = 1)
    {
        lock (_lock)
        {
            var sb = new System.Text.StringBuilder();

            // VCD Header
            sb.AppendLine("$date");
            sb.AppendLine($"   {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine("$end");
            sb.AppendLine("$version");
            sb.AppendLine("   VHDL Interpreter v0.17.0");
            sb.AppendLine("$end");
            sb.AppendLine("$timescale");
            // Body times are emitted as `time * timescaleNs` where `time`
            // is the engine's `tick * 1000 + delta` counter, so each
            // `vcdTime` unit is one picosecond regardless of `timescaleNs`.
            // The earlier `{timescaleNs}ns` header was 1000× off and made
            // GHDL-vs-Kmila diffs nonsensical (bench Phase D).
            sb.AppendLine("   1ps");
            sb.AppendLine("$end");

            // Variable definitions. Constants (`constant X : integer := ...;`)
            // never change over sim-time so VCD viewers shouldn't carry them
            // as $var entries — they'd render as flat straight lines and
            // clutter the pane. Audit F-G-03 (2026-07-09).
            var signals = _history.Keys
                .Where(k => !k.IsConstant)
                .OrderBy(k => k.Name)
                .ToList();
            var signalIds = new Dictionary<Variable, string>();

            sb.AppendLine($"$scope module {moduleName} $end");

            int idCounter = 0;
            foreach (var signal in signals)
            {
                // Generate a unique short identifier (!, ", #, $, etc.)
                string id = GenerateVcdId(idCounter++);
                signalIds[signal] = id;

                // Determine signal width
                int width = GetSignalWidth(signal);
                string vcdType = width == 1 ? "wire" : "reg";

                sb.AppendLine($"$var {vcdType} {width} {id} {signal.Name} $end");
            }

            sb.AppendLine("$upscope $end");
            sb.AppendLine("$enddefinitions $end");

            // Get all unique time points sorted
            var allTimes = _history.Values
                .SelectMany(h => h.Select(e => e.Item1))
                .Distinct()
                .OrderBy(t => t)
                .ToList();

            if (allTimes.Count == 0)
            {
                return sb.ToString();
            }

            // Initial values ($dumpvars)
            sb.AppendLine("$dumpvars");
            foreach (var signal in signals)
            {
                var initialValue = GetValueAtTime(signal, allTimes[0]);
                string vcdValue = FormatVcdValue(initialValue, GetSignalWidth(signal));
                sb.AppendLine($"{vcdValue}{signalIds[signal]}");
            }
            sb.AppendLine("$end");

            // Track last known values to only output changes
            var lastValues = new Dictionary<Variable, object?>();
            foreach (var signal in signals)
            {
                lastValues[signal] = GetValueAtTime(signal, allTimes[0]);
            }

            // Value changes at each timestamp
            foreach (var time in allTimes)
            {
                bool hasChanges = false;
                var changes = new System.Text.StringBuilder();

                foreach (var signal in signals)
                {
                    var currentValue = GetValueAtTime(signal, time);
                    var lastValue = lastValues[signal];

                    // Only output if value changed
                    if (!Equals(currentValue, lastValue))
                    {
                        if (!hasChanges)
                        {
                            // Convert internal time to VCD time
                            // Our time format: tick * 1000 + delta
                            // VCD time: in units of timescale
                            long vcdTime = time * timescaleNs;
                            sb.AppendLine($"#{vcdTime}");
                            hasChanges = true;
                        }

                        string vcdValue = FormatVcdValue(currentValue, GetSignalWidth(signal));
                        changes.AppendLine($"{vcdValue}{signalIds[signal]}");
                        lastValues[signal] = currentValue;
                    }
                }

                if (hasChanges)
                {
                    sb.Append(changes);
                }
            }

            return sb.ToString();
        }
    }

    /// <summary>
    /// Generates a VCD identifier from an index.
    /// VCD uses printable ASCII characters (33-126) as identifiers.
    /// </summary>
    private static string GenerateVcdId(int index)
    {
        // Use characters ! through ~ (ASCII 33-126)
        const int baseChar = 33;
        const int range = 94; // 126 - 33 + 1

        if (index < range)
        {
            return ((char)(baseChar + index)).ToString();
        }

        // For larger indices, use multiple characters
        var result = new System.Text.StringBuilder();
        while (index >= 0)
        {
            result.Insert(0, (char)(baseChar + (index % range)));
            index = index / range - 1;
            if (index < 0) break;
        }
        return result.ToString();
    }

    /// <summary>
    /// Gets the width of a signal in bits.
    /// </summary>
    private static int GetSignalWidth(Variable signal)
    {
        if (signal.Size > 0)
            return signal.Size;

        // Infer from value
        string? valueStr = signal.Value?.ToString();
        if (string.IsNullOrEmpty(valueStr))
            return 1;

        // Check if it's a binary string
        if (valueStr.All(c => c == '0' || c == '1' || c == 'X' || c == 'Z' || c == 'U'))
            return valueStr.Length;

        return 1;
    }

    /// <summary>
    /// Formats a value for VCD output.
    /// </summary>
    private static string FormatVcdValue(object? value, int width)
    {
        if (value == null)
            return width == 1 ? "x" : $"b{new string('x', width)} ";

        string valueStr = value.ToString() ?? "";

        // Handle boolean
        if (value is bool b)
            return b ? "1" : "0";

        // Handle single bit (std_logic)
        if (width == 1)
        {
            if (valueStr.Length == 1)
            {
                char c = char.ToLower(valueStr[0]);
                return c switch
                {
                    '0' => "0",
                    '1' => "1",
                    'z' => "z",
                    'x' => "x",
                    'u' => "x",
                    '-' => "x",
                    _ => "x"
                };
            }
            return "x";
        }

        // Handle multi-bit (std_logic_vector)
        // VCD format: bVALUE IDENTIFIER (note the space before identifier)
        var vcdBits = new System.Text.StringBuilder("b");
        foreach (char c in valueStr)
        {
            char lc = char.ToLower(c);
            vcdBits.Append(lc switch
            {
                '0' => '0',
                '1' => '1',
                'z' => 'z',
                'x' => 'x',
                'u' => 'x',
                '-' => 'x',
                _ => 'x'
            });
        }
        vcdBits.Append(' ');
        return vcdBits.ToString();
    }

    /// <summary>
    /// Saves the VCD output to a file.
    /// </summary>
    /// <param name="filePath">The path to save the VCD file.</param>
    /// <param name="moduleName">The name of the module/entity.</param>
    /// <param name="timescaleNs">The timescale in nanoseconds.</param>
    public void SaveToVcd(string filePath, string moduleName = "top", int timescaleNs = 1)
    {
        string vcdContent = ExportToVcd(moduleName, timescaleNs);
        System.IO.File.WriteAllText(filePath, vcdContent);
    }
}
