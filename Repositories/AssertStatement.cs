using System.Text;
using Interpreter.Interfaces;
using Interpreter.Models;
using Interpreter.Services;
using Parser.Models;
using Parser.Repositories;

namespace Interpreter.Repositories;

/// <summary>
/// F-G-16 (2026-07-18): represents and executes a VHDL <c>assert</c>
/// statement. Prior to F-G-16 every <c>assert</c> was silently skipped via
/// <c>SkipToSemicolonInclusive</c>, so testbench self-checks that expected
/// to fail loudly on wrong outputs stayed invisible.
///
/// Supported forms:
/// <code>
/// assert &lt;condition&gt;;
/// assert &lt;condition&gt; report &lt;message&gt;;
/// assert &lt;condition&gt; severity &lt;level&gt;;
/// assert &lt;condition&gt; report &lt;message&gt; severity &lt;level&gt;;
/// </code>
///
/// Fires (emits via <see cref="ReportBus"/>) when <c>condition</c>
/// evaluates to FALSE. Default message is <c>"Assertion violation"</c>,
/// default severity is <c>error</c>. If condition evaluation throws or
/// returns an unrecognised shape, treat as failure (emit) — students
/// diagnosing a broken design need the noise more than the silence.
///
/// Condition evaluation uses <see cref="Operation"/>, mirroring how
/// <see cref="ExitStatement"/> gates its optional <c>when</c> clause.
/// Report-message rendering delegates to
/// <see cref="ParseTrace.RenderReportTokens"/> so it gets the same
/// <c>to_string(sig(slice))</c> / string-fragment / identifier-substitution
/// handling as top-level and procedure-inlined reports.
/// </summary>
public sealed class AssertStatement : ISControl
{
    private readonly HashSet<Variable> _variables;
    /// <summary>1-based source line of the statement (errors in its condition/selector report it).</summary>
    private int _line;

    private readonly TokenStream? _streamer;

    /// <summary>Raw source text of the condition expression, for evaluation.</summary>
    public string ConditionString { get; private set; } = string.Empty;

    /// <summary>Tokens of the optional <c>report</c> expression (empty when absent).</summary>
    private readonly List<string> _reportTokens = new();

    /// <summary>VHDL severity level (default <c>error</c> per LRM).</summary>
    public string Severity { get; private set; } = "error";

    /// <summary>Creates an assert statement bound to the given variable scope and token stream.</summary>
    /// <param name="variables">The variable scope used to evaluate the condition.</param>
    /// <param name="streamer">The token stream positioned at the assert statement.</param>
    public AssertStatement(HashSet<Variable> variables, TokenStream streamer)
    {
        _variables = variables;
        _streamer  = streamer;
    }

    /// <summary>Parses the assert statement (condition, optional report message and severity) from the token stream.</summary>
    public void Syntetize()
    {
        if (_streamer == null) return;

        _line = _streamer.Current?.SourceLine ?? 0;
        _streamer.Expect(Constants.TYPE.KEYWORDS); // consume 'assert'

        // Condition: everything up to `report`, `severity`, or `;` (a string
        // literal is one token, so a `;` inside one never truncates it).
        var cond = new StringBuilder();
        while (_streamer.Current != null)
        {
            var val = _streamer.Current.Value ?? string.Empty;
            var low = val.ToLowerInvariant();
            if (val == ";" || low == "report" || low == "severity") break;
            cond.Append(val).Append(' ');
            _streamer.MoveNext();
        }
        ConditionString = cond.ToString().Trim();

        // Optional `report <expr>`.
        if (_streamer.Current?.Value?.ToLowerInvariant() == "report")
        {
            _streamer.MoveNext(); // consume 'report'
            while (_streamer.Current != null)
            {
                var val = _streamer.Current.Value ?? string.Empty;
                if (val == ";" || val.Equals("severity", StringComparison.OrdinalIgnoreCase)) break;
                _reportTokens.Add(val);
                _streamer.MoveNext();
            }
        }

        // Optional `severity <level>`.
        if (_streamer.Current?.Value?.ToLowerInvariant() == "severity")
        {
            _streamer.MoveNext(); // consume 'severity'
            if (_streamer.Current != null && _streamer.Current.Value != ";")
            {
                Severity = _streamer.Current.Value.ToLowerInvariant();
                _streamer.MoveNext();
            }
        }

        if (_streamer.Current?.Value == ";") _streamer.MoveNext();
    }

    /// <summary>Evaluates the condition and emits an assertion violation report when it does not hold.</summary>
    /// <returns>A completed task.</returns>
    public Task ExecuteAsync()
    {
        bool conditionHolds;
        try
        {
            // Empty condition (`assert;`) treated as false so it fires — an
            // unconditional testbench signal. Uncommon but defined by LRM.
            if (string.IsNullOrEmpty(ConditionString))
            {
                conditionHolds = false;
            }
            else
            {
                var op = new Operation(ConditionString, _variables, _line);
                var result = op.Execute();
                conditionHolds = Truth.Of(result);
            }
        }
        catch (Exception e) when (e is not SimulationRuntimeException)
        {
            // Evaluation failed — treat as failure so the assertion surfaces
            // rather than being silently swallowed. Matches educational
            // "loud on unknown" bias.
            conditionHolds = false;
        }

        if (conditionHolds) return Task.CompletedTask;

        var msg = _reportTokens.Count > 0
            ? ParseTrace.RenderReportTokens(_reportTokens, _variables)
            : "Assertion violation";
        ReportBus.Instance.Emit($"[assert-{Severity}] {msg}");
        // `severity failure` ends the simulation with an error (GHDL: "simulation failed").
        if (Severity == "failure") throw new SimulationEndException(true, msg);
        return Task.CompletedTask;
    }


}
