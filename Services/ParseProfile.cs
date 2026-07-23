using System.Diagnostics;

namespace Interpreter.Services;

/// <summary>
/// F-G-08 pass 5 (2026-07-22): lightweight per-run counters for the three
/// parse-side hot paths surfaced by the pass-4 probe. Populated in-place
/// by <see cref="Operation"/> constructor, <c>Tokenizer.EvaluateToken</c>,
/// and <see cref="Architecture.SyntetizeAssingations"/>. Emitted by
/// <c>HeadlessSimulator</c> when the <c>KMILA_PARSE_PROFILE</c> env var is
/// set. Off by default — the <c>Record*</c> methods early-return in the
/// hot path when disabled, so cost is one bool read per call site.
/// </summary>
public static class ParseProfile
{
    /// <summary>True if <c>KMILA_PARSE_PROFILE=1</c> was set at process start.</summary>
    public static readonly bool Enabled =
        Environment.GetEnvironmentVariable("KMILA_PARSE_PROFILE") == "1";

    // Counters — plain long fields, updated with Interlocked to stay
    // race-free across parallel test runs. Reset() zeros them for A/B
    // measurement inside a single process.
    private static long _operationCtorCount, _operationCtorTicks;
    private static long _tokenizerEvaluateTokenCount, _tokenizerEvaluateTokenTicks;
    private static long _syntetizeAssignationsCount, _syntetizeAssignationsTicks;

    public static long OperationCtorCount => Interlocked.Read(ref _operationCtorCount);
    public static long OperationCtorMs => TicksToMs(Interlocked.Read(ref _operationCtorTicks));
    public static long TokenizerEvaluateTokenCount => Interlocked.Read(ref _tokenizerEvaluateTokenCount);
    public static long TokenizerEvaluateTokenMs => TicksToMs(Interlocked.Read(ref _tokenizerEvaluateTokenTicks));
    public static long SyntetizeAssignationsCount => Interlocked.Read(ref _syntetizeAssignationsCount);
    public static long SyntetizeAssignationsMs => TicksToMs(Interlocked.Read(ref _syntetizeAssignationsTicks));

    /// <summary>
    /// Record a completed <see cref="Operation"/> constructor invocation.
    /// </summary>
    public static void RecordOperationCtor(long stopwatchTicks)
    {
        if (!Enabled) return;
        Interlocked.Increment(ref _operationCtorCount);
        Interlocked.Add(ref _operationCtorTicks, stopwatchTicks);
    }

    /// <summary>Record a Tokenizer.EvaluateToken invocation.</summary>
    public static void RecordTokenizerEvaluateToken(long stopwatchTicks)
    {
        if (!Enabled) return;
        Interlocked.Increment(ref _tokenizerEvaluateTokenCount);
        Interlocked.Add(ref _tokenizerEvaluateTokenTicks, stopwatchTicks);
    }

    /// <summary>Record an Architecture.SyntetizeAssingations invocation.</summary>
    public static void RecordSyntetizeAssignations(long stopwatchTicks)
    {
        if (!Enabled) return;
        Interlocked.Increment(ref _syntetizeAssignationsCount);
        Interlocked.Add(ref _syntetizeAssignationsTicks, stopwatchTicks);
    }

    /// <summary>Zero the counters. Used for A/B testing within one process.</summary>
    public static void Reset()
    {
        Interlocked.Exchange(ref _operationCtorCount, 0);
        Interlocked.Exchange(ref _operationCtorTicks, 0);
        Interlocked.Exchange(ref _tokenizerEvaluateTokenCount, 0);
        Interlocked.Exchange(ref _tokenizerEvaluateTokenTicks, 0);
        Interlocked.Exchange(ref _syntetizeAssignationsCount, 0);
        Interlocked.Exchange(ref _syntetizeAssignationsTicks, 0);
    }

    /// <summary>
    /// Produce a single-line summary matching the KMILA_SIM_PHASE_TRACE
    /// convention. Callers gate on <see cref="Enabled"/> before emit.
    /// </summary>
    public static string Summarize() =>
        $"[kmila-parse-profile] " +
        $"OperationCtor={OperationCtorCount} in {OperationCtorMs}ms | " +
        $"TokenizerEvaluateToken={TokenizerEvaluateTokenCount} in {TokenizerEvaluateTokenMs}ms | " +
        $"SyntetizeAssignations={SyntetizeAssignationsCount} in {SyntetizeAssignationsMs}ms";

    private static long TicksToMs(long ticks) =>
        (long)(ticks * 1000.0 / Stopwatch.Frequency);
}
