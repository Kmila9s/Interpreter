using System.Text;
using Interpreter.Interfaces;
using Interpreter.Services;
using Parser.Models;
using Parser.Repositories;

namespace Interpreter.Repositories;

/// <summary>
/// F-G-11 (2026-07-18): represents and executes a VHDL <c>report</c>
/// statement. Prior to F-G-11 every report was silently skipped via
/// <see cref="Interpreter.Services.ParseTrace.SkipToSemicolonInclusive"/>,
/// which meant testbench self-checking sweeps produced no observable output.
///
/// Supported forms:
/// <code>
/// report "message";
/// report "part" &amp; other &amp; " and more";
/// report "message" severity note;      -- severity clause is captured but ignored
/// </code>
///
/// Best-effort evaluation: for each token in the expression, string literals
/// pass through with their quotes stripped, bare identifiers that resolve to
/// a string in the process's variable scope are substituted (used by the
/// Ring 5 25-case markers where <c>name</c> is a procedure parameter), and
/// anything else falls back to the raw source text. This is imperfect —
/// full VHDL string handling (<c>image</c> attributes,
/// <c>to_string(vector)</c>, etc.) is future work — but covers the
/// straight <c>report "…";</c> case that testbenches use for progress
/// markers.
/// </summary>
public sealed class ReportStatement : ISControl
{
    private readonly HashSet<Variable> _variables;
    private readonly TokenStream? _streamer;

    /// <summary>Raw expression text captured between <c>report</c> and the terminating <c>;</c> (severity clause included when present).</summary>
    public string RawExpression { get; private set; } = string.Empty;

    /// <summary>Individual tokens of the expression, for best-effort evaluation at <see cref="ExecuteAsync"/> time.</summary>
    private readonly List<string> _tokens = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ReportStatement"/> class.
    /// </summary>
    /// <param name="variables">The variables in scope for the report expression.</param>
    /// <param name="streamer">The token stream positioned at the report statement.</param>
    public ReportStatement(HashSet<Variable> variables, TokenStream streamer)
    {
        _variables = variables;
        _streamer = streamer;
    }

    /// <summary>
    /// Parses <c>report &lt;expression&gt; [severity &lt;level&gt;];</c>.
    /// Consumes the terminating <c>;</c>. The expression text is captured
    /// verbatim; individual tokens are also kept so <see cref="ExecuteAsync"/>
    /// can do the small amount of runtime substitution we support today.
    /// </summary>
    public void Syntetize()
    {
        if (_streamer == null) return;

        _streamer.Expect(Constants.TYPE.KEYWORDS); // consume 'report'

        // Track whether we're inside a string literal — the tokenizer
        // splits `"a; b"` on whitespace, so a `;` inside the literal
        // arrives as its own token and would prematurely terminate the
        // walk. Same fix shape as F-G-09's SplitArchitecture masking.
        var raw = new StringBuilder();
        bool insideString = false;
        while (_streamer.Current != null)
        {
            var val = _streamer.Current.Value;

            if (!insideString && val == ";") break;

            var low = val.ToLowerInvariant();

            // Stop capturing at 'severity' but still consume its token +
            // level identifier so the ';' termination stays intact.
            if (!insideString && low == "severity")
            {
                _streamer.MoveNext(); // 'severity'
                if (_streamer.Current != null && _streamer.Current.Value != ";")
                    _streamer.MoveNext(); // level (note / warning / error / failure)
                continue;
            }

            // Toggle insideString on any token with an unmatched `"`.
            int quoteCount = 0;
            foreach (var c in val) if (c == '"') quoteCount++;
            if (quoteCount % 2 == 1) insideString = !insideString;

            _tokens.Add(val);
            raw.Append(val).Append(' ');
            _streamer.MoveNext();
        }
        if (_streamer.Current?.Value == ";") _streamer.MoveNext(); // consume ';'

        RawExpression = raw.ToString().TrimEnd();
    }

    /// <summary>
    /// Fires the report to <see cref="ReportBus"/>. Best-effort renders the
    /// captured tokens: string literals lose their quotes, bare identifiers
    /// that resolve to a variable with a printable value get substituted,
    /// everything else round-trips as source text.
    /// </summary>
    public Task ExecuteAsync()
    {
        // F-G-15 (2026-07-18): delegate rendering to the shared helper so
        // top-level reports and procedure-inlined reports produce identical
        // output — including `to_string(sig(slice))` evaluation.
        var rendered = ParseTrace.RenderReportTokens(_tokens, _variables);
        if (string.IsNullOrEmpty(rendered)) rendered = RawExpression;
        ReportBus.Instance.Emit(rendered);
        return Task.CompletedTask;
    }

    /// <summary>Not used — see <see cref="Syntetize()"/>.</summary>
    public void Syntetize(string[] data) => throw new NotImplementedException();
}
