namespace Interpreter.Services;

/// <summary>
/// Represents a progress update during VHDL parsing/synthesis.
/// </summary>
public readonly struct ProgressUpdate
{
    /// <summary>
    /// Progress percentage from 0.0 to 1.0.
    /// </summary>
    public float Progress { get; init; }

    /// <summary>
    /// Human-readable status message.
    /// </summary>
    public string Message { get; init; }

    /// <summary>
    /// The current phase of processing.
    /// </summary>
    public ProcessingPhase Phase { get; init; }

    /// <summary>
    /// Optional detail about the current item being processed.
    /// </summary>
    public string? Detail { get; init; }

    /// <summary>
    /// Timestamp when this update was created.
    /// </summary>
    public DateTime Timestamp { get; init; }

    /// <summary>
    /// Creates a new progress update with the given fraction, message, phase
    /// and optional detail. <paramref name="progress"/> is clamped to [0, 1]
    /// and <see cref="Timestamp"/> is set to <see cref="DateTime.Now"/>.
    /// </summary>
    /// <param name="progress">Overall progress as a fraction in [0, 1].</param>
    /// <param name="message">Short status message shown to the user.</param>
    /// <param name="phase">Pipeline phase this update belongs to.</param>
    /// <param name="detail">Optional sub-status string (e.g. current file).</param>
    public ProgressUpdate(float progress, string message, ProcessingPhase phase = ProcessingPhase.Unknown, string? detail = null)
    {
        Progress = Math.Clamp(progress, 0f, 1f);
        Message = message;
        Phase = phase;
        Detail = detail;
        Timestamp = DateTime.Now;
    }

    /// <summary>
    /// Returns the progress as a tuple (progress, message).
    /// </summary>
    public (float Progress, string Message) ToTuple() => (Progress, Message);

    /// <summary>
    /// Returns a formatted string representation.
    /// </summary>
    public override string ToString()
    {
        string progressBar = GetProgressBar(20);
        string percentage = $"{Progress * 100:F1}%".PadLeft(6);
        return $"{progressBar} {percentage} | {Message}{(Detail != null ? $" [{Detail}]" : "")}";
    }

    /// <summary>
    /// Gets an ASCII progress bar representation.
    /// </summary>
    public string GetProgressBar(int width = 20)
    {
        int filled = (int)(Progress * width);
        int empty = width - filled;
        return $"[{"█".PadRight(filled, '█')}{"░".PadRight(empty, '░')}]";
    }
}

/// <summary>
/// Processing phases during VHDL interpretation.
/// </summary>
public enum ProcessingPhase
{
    /// <summary>No phase has been established yet.</summary>
    Unknown,
    /// <summary>Interpreter setup and state preparation.</summary>
    Initializing,
    /// <summary>Breaking the source text into tokens.</summary>
    Tokenizing,
    /// <summary>Parsing the entity declaration.</summary>
    ParsingEntity,
    /// <summary>Parsing entity port declarations.</summary>
    ParsingPorts,
    /// <summary>Parsing entity generic declarations.</summary>
    ParsingGenerics,
    /// <summary>Parsing the architecture body.</summary>
    ParsingArchitecture,
    /// <summary>Parsing signal declarations.</summary>
    ParsingSignals,
    /// <summary>Parsing type declarations.</summary>
    ParsingTypes,
    /// <summary>Synthesizing behavioral logic.</summary>
    SynthesizingBehavior,
    /// <summary>Parsing a process statement.</summary>
    ParsingProcess,
    /// <summary>Parsing an if structure.</summary>
    ParsingIfStructure,
    /// <summary>Parsing a case structure.</summary>
    ParsingCaseStructure,
    /// <summary>Parsing a loop structure.</summary>
    ParsingLoop,
    /// <summary>Evaluating an expression.</summary>
    EvaluatingExpression,
    /// <summary>Calculating resource usage.</summary>
    CalculatingResources,
    /// <summary>Processing has completed successfully.</summary>
    Completed,
    /// <summary>Processing stopped due to an error.</summary>
    Error
}

/// <summary>
/// Event arguments for progress updates.
/// </summary>
public class ProgressEventArgs : EventArgs
{
    /// <summary>The progress payload carried by this event.</summary>
    public ProgressUpdate Update { get; }

    /// <summary>
    /// Wraps a <see cref="ProgressUpdate"/> for delivery via the
    /// <see cref="ProgressReporter.ProgressChanged"/> event.
    /// </summary>
    /// <param name="update">The update to surface to subscribers.</param>
    public ProgressEventArgs(ProgressUpdate update)
    {
        Update = update;
    }
}

/// <summary>
/// Singleton service for reporting progress during VHDL parsing and synthesis.
/// Provides event-based notifications and progress tracking.
/// </summary>
/// <remarks>
/// <para>Usage example:</para>
/// <code>
/// // Subscribe to progress updates
/// ProgressReporter.Instance.ProgressChanged += (sender, e) =>
/// {
///     var (progress, message) = e.Update.ToTuple();
///     Console.WriteLine($"{progress:P0} - {message}");
/// };
///
/// // Report progress from anywhere
/// ProgressReporter.Instance.Report(0.5f, "Parsing architecture", ProcessingPhase.ParsingArchitecture);
/// </code>
/// </remarks>
public class ProgressReporter
{
    private static readonly Lazy<ProgressReporter> _instance = new(() => new ProgressReporter());

    /// <summary>
    /// Gets the singleton instance of the ProgressReporter.
    /// </summary>
    public static ProgressReporter Instance => _instance.Value;

    /// <summary>
    /// Event raised when progress is updated.
    /// </summary>
    public event EventHandler<ProgressEventArgs>? ProgressChanged;

    /// <summary>
    /// Event raised when an error occurs.
    /// </summary>
    public event EventHandler<ProgressEventArgs>? ErrorOccurred;

    /// <summary>
    /// Event raised when processing completes.
    /// </summary>
    public event EventHandler<ProgressEventArgs>? Completed;

    /// <summary>
    /// Event raised when cancellation is requested.
    /// </summary>
    public event EventHandler<ProgressEventArgs>? CancellationRequested;

    /// <summary>
    /// Gets the current progress update.
    /// </summary>
    public ProgressUpdate Current { get; private set; }

    /// <summary>
    /// Gets or sets whether progress reporting is enabled.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to output progress to console.
    /// </summary>
    public bool ConsoleOutput { get; set; } = false;

    /// <summary>
    /// Gets or sets the current cancellation token source.
    /// </summary>
    public CancellationTokenSource? CancellationSource { get; private set; }

    /// <summary>
    /// Gets the current cancellation token.
    /// </summary>
    public CancellationToken CancellationToken => CancellationSource?.Token ?? CancellationToken.None;

    /// <summary>
    /// Gets whether cancellation has been requested.
    /// </summary>
    public bool IsCancellationRequested => CancellationSource?.IsCancellationRequested ?? false;

    /// <summary>
    /// Gets the history of progress updates (limited to last 100).
    /// </summary>
    public IReadOnlyList<ProgressUpdate> History => _history.AsReadOnly();
    private readonly List<ProgressUpdate> _history = new(100);

    // Phase weights for overall progress calculation
    private readonly Dictionary<ProcessingPhase, (float Start, float End)> _phaseWeights = new()
    {
        { ProcessingPhase.Initializing,         (0.00f, 0.05f) },
        { ProcessingPhase.Tokenizing,           (0.05f, 0.15f) },
        { ProcessingPhase.ParsingEntity,        (0.15f, 0.25f) },
        { ProcessingPhase.ParsingPorts,         (0.20f, 0.25f) },
        { ProcessingPhase.ParsingGenerics,      (0.25f, 0.30f) },
        { ProcessingPhase.ParsingArchitecture,  (0.30f, 0.40f) },
        { ProcessingPhase.ParsingSignals,       (0.35f, 0.40f) },
        { ProcessingPhase.ParsingTypes,         (0.40f, 0.45f) },
        { ProcessingPhase.SynthesizingBehavior, (0.45f, 0.80f) },
        { ProcessingPhase.ParsingProcess,       (0.50f, 0.70f) },
        { ProcessingPhase.ParsingIfStructure,   (0.55f, 0.65f) },
        { ProcessingPhase.ParsingCaseStructure, (0.55f, 0.65f) },
        { ProcessingPhase.ParsingLoop,          (0.55f, 0.65f) },
        { ProcessingPhase.EvaluatingExpression, (0.70f, 0.80f) },
        { ProcessingPhase.CalculatingResources, (0.80f, 0.95f) },
        { ProcessingPhase.Completed,            (0.95f, 1.00f) },
    };

    private ProgressReporter() { }

    /// <summary>
    /// Reports a progress update.
    /// </summary>
    /// <param name="progress">Progress from 0.0 to 1.0.</param>
    /// <param name="message">Status message.</param>
    /// <param name="phase">Current processing phase.</param>
    /// <param name="detail">Optional detail about current item.</param>
    public void Report(float progress, string message, ProcessingPhase phase = ProcessingPhase.Unknown, string? detail = null)
    {
        if (!IsEnabled) return;

        var update = new ProgressUpdate(progress, message, phase, detail);
        Current = update;

        // Add to history (keep last 100)
        if (_history.Count >= 100)
            _history.RemoveAt(0);
        _history.Add(update);

        // Console output if enabled
        if (ConsoleOutput)
            Console.WriteLine(update.ToString());

        // Raise event
        ProgressChanged?.Invoke(this, new ProgressEventArgs(update));
    }

    /// <summary>
    /// Reports progress within a specific phase using phase-relative progress.
    /// </summary>
    /// <param name="phaseProgress">Progress within the phase (0.0 to 1.0).</param>
    /// <param name="message">Status message.</param>
    /// <param name="phase">Current processing phase.</param>
    /// <param name="detail">Optional detail.</param>
    public void ReportPhase(float phaseProgress, string message, ProcessingPhase phase, string? detail = null)
    {
        if (!IsEnabled) return;

        float overallProgress = phaseProgress;
        if (_phaseWeights.TryGetValue(phase, out var weights))
        {
            overallProgress = weights.Start + (phaseProgress * (weights.End - weights.Start));
        }

        Report(overallProgress, message, phase, detail);
    }

    /// <summary>
    /// Reports an error.
    /// </summary>
    /// <param name="message">Error message.</param>
    /// <param name="detail">Error details.</param>
    public void ReportError(string message, string? detail = null)
    {
        if (!IsEnabled) return;

        var update = new ProgressUpdate(Current.Progress, message, ProcessingPhase.Error, detail);
        Current = update;
        _history.Add(update);

        if (ConsoleOutput)
            Console.WriteLine($"❌ ERROR: {update}");

        ErrorOccurred?.Invoke(this, new ProgressEventArgs(update));
    }

    /// <summary>
    /// Reports completion.
    /// </summary>
    /// <param name="message">Completion message.</param>
    public void ReportCompleted(string message = "Processing completed")
    {
        if (!IsEnabled) return;

        var update = new ProgressUpdate(1.0f, message, ProcessingPhase.Completed);
        Current = update;
        _history.Add(update);

        if (ConsoleOutput)
            Console.WriteLine($"✓ {update}");

        Completed?.Invoke(this, new ProgressEventArgs(update));
    }

    /// <summary>
    /// Starts a new cancellable operation with a fresh CancellationTokenSource.
    /// </summary>
    /// <returns>The CancellationToken for this operation.</returns>
    public CancellationToken StartCancellableOperation()
    {
        // Dispose previous source if exists
        CancellationSource?.Dispose();
        CancellationSource = new CancellationTokenSource();
        return CancellationSource.Token;
    }

    /// <summary>
    /// Starts a new cancellable operation with the provided CancellationTokenSource.
    /// </summary>
    /// <param name="cts">The CancellationTokenSource to use.</param>
    /// <returns>The CancellationToken for this operation.</returns>
    public CancellationToken StartCancellableOperation(CancellationTokenSource cts)
    {
        CancellationSource?.Dispose();
        CancellationSource = cts;
        return CancellationSource.Token;
    }

    /// <summary>
    /// Requests cancellation of the current operation.
    /// </summary>
    public void RequestCancellation()
    {
        if (CancellationSource == null || CancellationSource.IsCancellationRequested)
            return;

        var update = new ProgressUpdate(Current.Progress, "Cancellation requested", ProcessingPhase.Error, "User requested cancellation");
        Current = update;
        _history.Add(update);

        if (ConsoleOutput)
            Console.WriteLine($"⚠️ CANCELLED: {update}");

        CancellationRequested?.Invoke(this, new ProgressEventArgs(update));
        CancellationSource.Cancel();
    }

    /// <summary>
    /// Checks if cancellation has been requested and throws OperationCanceledException if so.
    /// Call this periodically in long-running operations.
    /// </summary>
    /// <exception cref="OperationCanceledException">Thrown when cancellation is requested.</exception>
    public void ThrowIfCancellationRequested()
    {
        CancellationSource?.Token.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Checks if cancellation has been requested.
    /// </summary>
    /// <returns>True if cancellation was requested.</returns>
    public bool CheckCancellation()
    {
        return CancellationSource?.IsCancellationRequested ?? false;
    }

    /// <summary>
    /// Clears the progress history.
    /// </summary>
    public void Reset()
    {
        _history.Clear();
        Current = new ProgressUpdate(0, "Ready", ProcessingPhase.Initializing);
    }

    /// <summary>
    /// Creates a scoped progress reporter for a sub-operation.
    /// </summary>
    /// <param name="startProgress">Start progress for this scope.</param>
    /// <param name="endProgress">End progress for this scope.</param>
    /// <param name="phase">Phase for this scope.</param>
    /// <returns>A scoped reporter that maps 0-1 progress to the specified range.</returns>
    public ScopedProgressReporter CreateScope(float startProgress, float endProgress, ProcessingPhase phase)
    {
        return new ScopedProgressReporter(this, startProgress, endProgress, phase);
    }

    /// <summary>
    /// Creates a scoped reporter for processing multiple items.
    /// </summary>
    /// <param name="totalItems">Total number of items to process.</param>
    /// <param name="phase">Processing phase.</param>
    /// <param name="itemName">Name of items being processed (e.g., "port", "signal").</param>
    public ItemProgressReporter CreateItemScope(int totalItems, ProcessingPhase phase, string itemName)
    {
        return new ItemProgressReporter(this, totalItems, phase, itemName);
    }
}

/// <summary>
/// Scoped progress reporter that maps 0-1 progress to a specific range.
/// </summary>
public class ScopedProgressReporter
{
    private readonly ProgressReporter _parent;
    private readonly float _start;
    private readonly float _range;
    private readonly ProcessingPhase _phase;

    /// <summary>
    /// Initializes a sub-range reporter that maps local 0..1 progress onto
    /// a fixed slice of the parent's overall progress space.
    /// </summary>
    /// <param name="parent">Owning <see cref="ProgressReporter"/> that receives the mapped reports.</param>
    /// <param name="start">Lower bound of the slice in the parent's progress space (0..1).</param>
    /// <param name="end">Upper bound of the slice in the parent's progress space (0..1).</param>
    /// <param name="phase">Phase tag stamped on every emitted update.</param>
    internal ScopedProgressReporter(ProgressReporter parent, float start, float end, ProcessingPhase phase)
    {
        _parent = parent;
        _start = start;
        _range = end - start;
        _phase = phase;
    }

    /// <summary>
    /// Reports progress within this scope.
    /// </summary>
    /// <param name="progress">Progress from 0.0 to 1.0 within this scope.</param>
    /// <param name="message">Status message.</param>
    /// <param name="detail">Optional detail.</param>
    public void Report(float progress, string message, string? detail = null)
    {
        float mappedProgress = _start + (progress * _range);
        _parent.Report(mappedProgress, message, _phase, detail);
    }
}

/// <summary>
/// Progress reporter for processing a known number of items.
/// </summary>
public class ItemProgressReporter
{
    private readonly ProgressReporter _parent;
    private readonly int _totalItems;
    private readonly ProcessingPhase _phase;
    private readonly string _itemName;
    private int _currentItem = 0;

    /// <summary>
    /// Initializes a per-item progress helper that automatically derives the
    /// fractional progress as <c>currentItem / totalItems</c>.
    /// </summary>
    /// <param name="parent">Owning <see cref="ProgressReporter"/> that receives the reports.</param>
    /// <param name="totalItems">Total number of items to be processed (clamped to ≥ 1).</param>
    /// <param name="phase">Phase tag stamped on every emitted update.</param>
    /// <param name="itemName">Singular noun for the items, used in default messages (e.g. "file", "entity").</param>
    internal ItemProgressReporter(ProgressReporter parent, int totalItems, ProcessingPhase phase, string itemName)
    {
        _parent = parent;
        _totalItems = Math.Max(totalItems, 1);
        _phase = phase;
        _itemName = itemName;
    }

    /// <summary>
    /// Reports the next item being processed.
    /// </summary>
    /// <param name="itemDetail">Detail about the current item.</param>
    public void NextItem(string? itemDetail = null)
    {
        _currentItem++;
        float progress = (float)_currentItem / _totalItems;
        string message = $"Processing {_itemName} {_currentItem}/{_totalItems}";
        _parent.ReportPhase(progress, message, _phase, itemDetail);
    }

    /// <summary>
    /// Reports completion of all items.
    /// </summary>
    public void Complete()
    {
        _parent.ReportPhase(1.0f, $"Completed {_totalItems} {_itemName}(s)", _phase);
    }
}
