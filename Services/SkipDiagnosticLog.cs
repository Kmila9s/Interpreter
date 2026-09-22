namespace Interpreter.Services;

/// <summary>
/// Severity of a skip-time diagnostic.
/// </summary>
public enum SkipSeverity
{
    /// <summary>Construct was registered as a placeholder; no synth effect.</summary>
    Info,
    /// <summary>Construct was dropped entirely; logic may be missing from netlist.</summary>
    Warning
}

/// <summary>
/// One entry in the <see cref="SkipDiagnosticLog"/> describing a single
/// silent-skip event the parser performed. Captured at parse time and
/// surfaced to the user (test runner output, editor markers, future synth
/// netlist's Diagnostics list).
/// </summary>
/// <param name="Severity">How alarming the skip is.</param>
/// <param name="Code">Short stable identifier (e.g. <c>VHD-ALIAS-PLACEHOLDER</c>).</param>
/// <param name="Subject">The construct's source identifier when meaningful (function name, alias name, etc.) — empty string when none.</param>
/// <param name="Message">Human-readable explanation of what was dropped or stubbed.</param>
public sealed record SkipDiagnostic(SkipSeverity Severity, string Code, string Subject, string Message);

/// <summary>
/// Process-wide collector of "we silently skipped this VHDL construct"
/// notices. The parser/synthesizer used to drop function bodies, alias
/// declarations, attribute specs, and so on without telling anyone — leaving
/// users wondering why their netlist was wrong. This log gives them an
/// auditable trail.
/// </summary>
/// <remarks>
/// Cleared between fixtures by the same code that clears
/// <see cref="EntityRegistry"/> / <see cref="PackageRegistry"/> /
/// <see cref="FunctionRegistry"/>. Test runners drain and print after each
/// file; the eventual editor integration consumes the same channel and
/// converts each entry into a Monaco marker.
/// </remarks>
public sealed class SkipDiagnosticLog
{
    private static readonly SkipDiagnosticLog _instance = new();
    /// <summary>Process-wide singleton.</summary>
    public static SkipDiagnosticLog Instance => _instance;

    private readonly List<SkipDiagnostic> _entries = new();

    private SkipDiagnosticLog() { }

    /// <summary>Records a single diagnostic.</summary>
    public void Add(SkipSeverity severity, string code, string subject, string message)
        => _entries.Add(new SkipDiagnostic(severity, code, subject, message));

    /// <summary>Convenience for the very common Info severity.</summary>
    public void AddInfo(string code, string subject, string message)
        => Add(SkipSeverity.Info, code, subject, message);

    /// <summary>Convenience for Warning severity (logic actually lost).</summary>
    public void AddWarning(string code, string subject, string message)
        => Add(SkipSeverity.Warning, code, subject, message);

    /// <summary>Read-only view of accumulated entries.</summary>
    public IReadOnlyList<SkipDiagnostic> Entries => _entries;

    /// <summary>Drops every entry. Called between fixtures.</summary>
    public void Clear() => _entries.Clear();

    /// <summary>
    /// Snapshots the current entries and clears the internal store. Useful
    /// from per-fixture loops that want to print and reset in one shot.
    /// </summary>
    public IReadOnlyList<SkipDiagnostic> Drain()
    {
        var snapshot = _entries.ToArray();
        _entries.Clear();
        return snapshot;
    }
}
