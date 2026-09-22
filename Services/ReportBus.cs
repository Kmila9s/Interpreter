namespace Interpreter.Services;

/// <summary>
/// F-G-11 (2026-07-18): singleton event sink for VHDL <c>report</c>
/// statements executed at runtime. Prior to F-G-11, Kmila's parser silently
/// dropped every <c>report</c> via <c>SkipToSemicolonInclusive</c>, so
/// testbench sweeps that emit their case markers as
/// <c>report "======== A1 begin ========";</c> produced no observable
/// output beyond the VCD. This bus lets <see cref="Interpreter.Repositories.ReportStatement"/>
/// broadcast each report to any listener — typically
/// <see cref="SimulationRunner"/> — which forwards it to the response log.
///
/// Scope: only reports at process-body TOP LEVEL fire today. Reports nested
/// inside procedures don't (Kmila doesn't yet inline procedure calls), and
/// without <c>wait</c> semantics all reports in a process fire in code order
/// at delta 0. Ordered / time-stamped reports need F-G-12 (procedure inlining)
/// and F-G-13 (wait modelling) — both scoped for follow-up sessions.
/// </summary>
public sealed class ReportBus
{
    private static readonly ReportBus _instance = new();

    /// <summary>Shared singleton.</summary>
    public static ReportBus Instance => _instance;

    /// <summary>
    /// Fired each time a <c>report</c> statement executes. Payload is the
    /// literal expression text captured at parse time (best-effort — inline
    /// string concat with runtime parameters is preserved as source text).
    /// Subscribers should treat the string as opaque.
    /// </summary>
    public event Action<string>? OnReport;

    /// <summary>Publish a report message to all subscribers.</summary>
    public void Emit(string message) => OnReport?.Invoke(message);
}
