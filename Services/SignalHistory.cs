using Interpreter.Models;
using Parser.Models;
using static Parser.Models.Constants;

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
    /// Per-signal count of entries FIFO-evicted by the <see cref="MaxSamplesPerSignal"/>
    /// cap. The "absolute" index of the i-th retained entry is
    /// <c>_evicted[signal] + i</c>. <see cref="ReadSince"/> uses this so the
    /// live-waveform watermark stays valid after eviction. Empty / zero on short
    /// runs (nothing evicted).
    /// </summary>
    private readonly Dictionary<Variable, long> _evicted = new();

    /// <summary>
    /// Lock for thread safety.
    /// </summary>
    private readonly object _lock = new();

    /// <summary>
    /// Whether recording is enabled.
    /// </summary>
    public bool IsRecording { get; set; } = true;

    /// <summary>
    /// Maximum (time, value) samples retained per signal while recording. Once a
    /// signal exceeds this, the oldest samples are FIFO-evicted in amortized-O(1)
    /// batches so a long / free-running simulation keeps BOUNDED memory — the
    /// root-cause fix for the long-running freeze + crash (unbounded history was
    /// the primary heap-growth source). Set to 0 or negative to opt into
    /// UNBOUNDED retention (full-fidelity VCD export over an entire run).
    ///
    /// Default 5000 ≫ what any teaching waveform needs to scrub; a short run
    /// never reaches it, so eviction never fires and every exported value and
    /// timestamp stays byte-identical to before this cap existed. Eviction
    /// advances <see cref="_evicted"/> so <see cref="ReadSince"/>'s watermark
    /// arithmetic stays correct.
    /// </summary>
    public int MaxSamplesPerSignal { get; set; } = 5000;

    /// <summary>
    /// Optional spill sink for the <see cref="MaxSamplesPerSignal"/> cap (long-run
    /// Phase 2, 2026-11-08). When set, every batch of samples about to be FIFO-evicted
    /// is handed to this callback FIRST — in time order, per signal, as
    /// (timeMoment, value, sourceLine) — so a host can persist it (e.g. a per-run
    /// on-disk trace store) instead of losing it. Invoked on the recording thread
    /// while the history lock is held: the sink must be fast and non-blocking
    /// (enqueue and return) and must not call back into this instance. Null (the
    /// default) = eviction simply drops, exactly as before.
    /// </summary>
    public Action<Variable, IReadOnlyList<(long TimeMoment, object Value, int SourceLine)>>? OnEvicting { get; set; }

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

            // Long-run safety cap (2026-11-06): bound per-signal retention so a
            // free-running / very long sim cannot grow the heap without limit —
            // the primary driver of the long-running freeze + crash. Evict the
            // oldest in amortized-O(1) batches (RemoveRange of a chunk, not one
            // per add) and track the dropped count so ReadSince's watermark stays
            // valid. MaxSamplesPerSignal <= 0 means unbounded (opt-in full VCD).
            if (MaxSamplesPerSignal > 0)
            {
                var list = _history[signal];
                if (list.Count > MaxSamplesPerSignal)
                {
                    int remove = Math.Max(1, MaxSamplesPerSignal / 8);
                    if (remove > list.Count) remove = list.Count;
                    _sourceLines.TryGetValue(signal, out var sl);
                    var spill = OnEvicting;
                    if (spill != null)
                    {
                        // Hand the batch to the spill sink before it is dropped.
                        var batch = new (long, object, int)[remove];
                        for (int i = 0; i < remove; i++)
                            batch[i] = (list[i].Item1, list[i].Item2,
                                        sl != null && i < sl.Count ? sl[i] : 0);
                        spill(signal, batch);
                    }
                    list.RemoveRange(0, remove);
                    if (sl != null)
                    {
                        int slRemove = Math.Min(remove, sl.Count);
                        if (slRemove > 0) sl.RemoveRange(0, slRemove);
                    }
                    _evicted[signal] = (_evicted.TryGetValue(signal, out var ev) ? ev : 0L) + remove;
                }
            }
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
            long baseOff = _evicted.TryGetValue(signal, out var ev) ? ev : 0L;
            // fromIndex is an ABSOLUTE entry index (count of entries the caller has
            // already consumed). Translate to a local index into the retained
            // window. A watermark that fell into the evicted region clamps to the
            // window start — those entries are gone from the bounded live view.
            long localStart = fromIndex - baseOff;
            if (localStart < 0) localStart = 0;
            if (localStart > h.Count) localStart = h.Count;
            for (int i = (int)localStart; i < h.Count; i++)
            {
                var e = h[i];
                onEntry(e.Item1, e.Item2);
            }
            return (int)(baseOff + h.Count);
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
            _evicted.Clear();
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
            sb.AppendLine($"$scope module {moduleName} $end");

            // One column per dumped value: a signal, or each field of a record
            // signal (inside a `$scope` named after it).
            var columns = new List<VcdColumn>();
            foreach (var signal in signals)
                AppendColumns(sb, columns, signal);

            sb.AppendLine("$upscope $end");
            sb.AppendLine("$enddefinitions $end");

            // One sample per real instant: the value settled after its last delta
            // (internal time is tick*1000 + delta), like GHDL's dump.
            var allTimes = _history.Values
                .SelectMany(h => h.Select(e => SettledMoment(e.Item1 / 1000)))
                .Distinct()
                .OrderBy(t => t)
                .ToList();

            if (allTimes.Count == 0)
            {
                return sb.ToString();
            }

            // A record's columns are consecutive: read each signal's history once per instant.
            Variable? lastSignal = null;
            long lastTime = -1;
            object? lastRaw = null;
            object? ValueOnce(Variable signal, long time)
            {
                if (!ReferenceEquals(signal, lastSignal) || time != lastTime)
                    (lastSignal, lastTime, lastRaw) = (signal, time, GetValueAtTime(signal, time));
                return lastRaw;
            }

            // Initial values ($dumpvars)
            sb.AppendLine("$dumpvars");
            var lastValues = new object?[columns.Count];
            for (int c = 0; c < columns.Count; c++)
            {
                var column = columns[c];
                lastValues[c] = column.Project(ValueOnce(column.Signal, allTimes[0]));
                sb.AppendLine($"{column.Format(lastValues[c])}{column.Id}");
            }
            sb.AppendLine("$end");

            // Value changes at each timestamp
            foreach (var time in allTimes)
            {
                bool hasChanges = false;
                var changes = new System.Text.StringBuilder();

                for (int c = 0; c < columns.Count; c++)
                {
                    var column = columns[c];
                    var currentValue = column.Project(ValueOnce(column.Signal, time));
                    var lastValue = lastValues[c];

                    // Only output if value changed
                    if (!Equals(currentValue, lastValue))
                    {
                        if (!hasChanges)
                        {
                            // Convert internal time to VCD time
                            // Our time format: tick * 1000 + delta
                            // VCD time: in units of timescale
                            long vcdTime = time / 1000 * 1000 * timescaleNs;
                            sb.AppendLine($"#{vcdTime}");
                            hasChanges = true;
                        }

                        changes.AppendLine($"{column.Format(currentValue)}{column.Id}");
                        lastValues[c] = currentValue;
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
    /// One dumped value: which signal it reads, how its part of the signal's
    /// value is taken (<see cref="Project"/>) and written (<see cref="Format"/>).
    /// </summary>
    private sealed record VcdColumn(Variable Signal, string Id, Func<object?, object?> Project, Func<object?, string> Format);

    private static object? Whole(object? value) => value;

    /// <summary>
    /// Declares the column(s) of <paramref name="signal"/>: a record becomes a
    /// <c>$scope</c> with one column per field (nested records as nested
    /// scopes), integers are <c>integer 32</c> like GHDL, enumerations the
    /// literal index, anything else keeps its width.
    /// </summary>
    private static void AppendColumns(System.Text.StringBuilder sb, List<VcdColumn> columns, Variable signal)
    {
        if (signal.Layout is { } layout)
        {
            var open = new List<string>();
            sb.AppendLine($"$scope module {signal.Name} $end");
            foreach (var (path, offset, field) in layout.Leaves())
            {
                var scopes = path.Split('.')[..^1];
                int keep = 0;
                while (keep < open.Count && keep < scopes.Length && open[keep] == scopes[keep]) keep++;
                for (int c = open.Count; c > keep; c--) sb.AppendLine("$upscope $end");
                open.RemoveRange(keep, open.Count - keep);
                for (int c = keep; c < scopes.Length; c++)
                {
                    sb.AppendLine($"$scope module {scopes[c]} $end");
                    open.Add(scopes[c]);
                }
                var f = field;
                int at = offset;
                object? Bits(object? v) => FieldCodec.Slice(v, at, f.Width);
                if (f.Kind == FieldKind.Integer)
                    AddColumn(sb, columns, signal, "integer", 32, f.Name, Bits,
                        v => IntegerVcdValue(v is string bits ? FieldCodec.DecodeValue(f, bits) : null));
                else
                    AddColumn(sb, columns, signal, f.Width == 1 ? "wire" : "reg", f.Width, f.Name, Bits,
                        v => FormatVcdValue(v, f.Width));
            }
            for (int c = open.Count; c >= 0; c--) sb.AppendLine("$upscope $end");
            return;
        }
        if (signal.IsInteger)
        {
            AddColumn(sb, columns, signal, "integer", 32, signal.Name, Whole, IntegerVcdValue);
            return;
        }
        if (signal.EnumLiterals is { } literals)
        {
            // Enumeration: the literal's index in binary (GHDL does not dump enums).
            int bits = Math.Max(signal.Size, 1);
            AddColumn(sb, columns, signal, bits == 1 ? "wire" : "reg", bits, signal.Name, Whole,
                v => FormatVcdValue(FieldCodec.IntegerBits(FieldCodec.EnumIndex(literals, v), bits), bits));
            return;
        }
        int width = GetSignalWidth(signal);
        AddColumn(sb, columns, signal, width == 1 ? "wire" : "reg", width, signal.Name, Whole, v => FormatVcdValue(v, width));
    }

    private static void AddColumn(System.Text.StringBuilder sb, List<VcdColumn> columns, Variable signal,
        string kind, int width, string name, Func<object?, object?> project, Func<object?, string> format)
    {
        string id = GenerateVcdId(columns.Count);
        sb.AppendLine($"$var {kind} {width} {id} {name} $end");
        columns.Add(new VcdColumn(signal, id, project, format));
    }

    /// <summary>An integer value as GHDL dumps it: shortest binary, 32-bit two's complement when negative.</summary>
    private static string IntegerVcdValue(object? value)
    {
        long n = FieldCodec.IntegerOf(value);
        string bits = n < 0 ? FieldCodec.IntegerBits(n, 32) : Convert.ToString(n, 2);
        return $"b{bits} ";
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
    /// <summary>
    /// The internal time (tick*1000 + delta) at which tick <paramref name="tick"/>'s values have
    /// settled after all its deltas: the moment a VCD sample or a waveform probe reads.
    /// </summary>
    public static long SettledMoment(long tick) => tick * 1000 + 999;

    private static string FormatVcdValue(object? value, int width)
    {
        if (value == null)
            return width == 1 ? "x" : $"b{new string('x', width)} ";
        if (value is bool b)
            return b ? "1" : "0";

        // The nine std_logic values are written as they are (upper case, like GHDL);
        // anything else is unknown.
        string valueStr = value.ToString() ?? "";
        static char Bit(char c) => StdLogic.IsValue(c) ? StdLogic.Norm(c) : 'X';
        if (width == 1)
            return valueStr.Length == 1 ? Bit(valueStr[0]).ToString()
                // A boolean is dumped as one bit, like GHDL (false → 0, true → 1).
                : bool.TryParse(valueStr, out bool flag) ? (flag ? "1" : "0")
                : "X";
        // VCD format: bVALUE IDENTIFIER (note the space before identifier)
        return string.Create(valueStr.Length + 2, valueStr, static (r, src) =>
        {
            r[0] = 'b';
            for (int i = 0; i < src.Length; i++) r[i + 1] = Bit(src[i]);
            r[^1] = ' ';
        });
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
