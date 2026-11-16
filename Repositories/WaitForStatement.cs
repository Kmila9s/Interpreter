using Interpreter.Interfaces;
using Interpreter.Services;
using Parser.Models;
using Parser.Repositories;

namespace Interpreter.Repositories;

/// <summary>
/// Represents a VHDL <c>wait</c> statement in a process body. Captured at
/// parse time; consumed at runtime by the process-execution driver in
/// <see cref="Interpreter.Services.DeltaCycleEngine"/>.
///
/// Supported forms:
/// <list type="bullet">
/// <item><description><c>wait for X fs|ps|ns|us|ms|sec;</c> — F-G-13. Set
/// <see cref="DelayNs"/> from the source, <see cref="IsTimed"/> = true,
/// <see cref="IsTerminal"/> = false. Engine reschedules the process at
/// <c>current_tick + DelayTicks</c>.</description></item>
/// <item><description>bare <c>wait;</c> — <see cref="IsTerminal"/> = true,
/// process yields and never resumes (used by testbench-end idiom).</description></item>
/// <item><description><c>wait until &lt;cond&gt;;</c> — F-G-13b (Session 23,
/// 2026-07-18). <see cref="IsCondition"/> = true, <see cref="Condition"/>
/// holds the compiled expression, <see cref="Sensitivity"/> holds the
/// signals referenced. The engine registers the process under each signal
/// in the sensitivity set; on each delta-cycle signal change, the engine
/// re-evaluates the stored condition and wakes the process when it becomes
/// true.</description></item>
/// <item><description><c>wait on &lt;sig&gt;;</c> — captured as terminal
/// for now (follow-up).</description></item>
/// </list>
///
/// The execute path is a no-op — the delta engine special-cases
/// <see cref="WaitForStatement"/> in its process runner (checks the type
/// via <c>is WaitForStatement</c>) rather than dispatching through
/// <see cref="ExecuteAsync"/>, because the engine needs to control the
/// yield / schedule-resume flow.
/// </summary>
public sealed class WaitForStatement : ISControl
{
    private readonly TokenStream? _streamer;
    private readonly HashSet<Variable>? _variables;

    /// <summary>Delay in nanoseconds parsed from the source; 0 for bare wait.</summary>
    public long DelayNs { get; private set; } = 0;

    /// <summary>True when this is <c>wait for X unit;</c> — schedule a resume.</summary>
    public bool IsTimed { get; private set; } = false;

    /// <summary>
    /// True when this wait never resumes (bare <c>wait;</c>, or the
    /// unsupported <c>wait on</c> form). The engine should treat the
    /// process as done after seeing this.
    /// </summary>
    public bool IsTerminal { get; private set; } = false;

    /// <summary>
    /// F-G-13b (2026-07-18): true when this is <c>wait until &lt;cond&gt;;</c>.
    /// The engine registers the process under each signal in
    /// <see cref="Sensitivity"/> and re-evaluates <see cref="Condition"/>
    /// on each delta's signal-change burst.
    /// </summary>
    public bool IsCondition { get; private set; } = false;

    /// <summary>
    /// F-G-13b: compiled condition expression, built via the same
    /// <see cref="Operation"/> pipeline used by <c>if</c>/<c>elsif</c>
    /// conditions. <c>Execute()</c> returns a <see cref="Variable"/> whose
    /// <c>Value</c> is a bool.
    /// </summary>
    public Operation? Condition { get; private set; }

    /// <summary>
    /// F-G-13b: signals referenced by <see cref="Condition"/>, extracted at
    /// synth time by scanning the raw condition tokens against the caller's
    /// variable set. The engine indexes the process into
    /// <c>_conditionBlocked</c> under each of these signals so a signal
    /// change triggers a condition re-evaluation.
    /// </summary>
    public HashSet<Variable> Sensitivity { get; } = new();

    /// <summary>Creates a wait statement bound to the given token stream and optional variable scope.</summary>
    /// <param name="streamer">The token stream positioned at the wait statement.</param>
    /// <param name="variables">The variable scope used to evaluate any wait condition.</param>
    public WaitForStatement(TokenStream streamer, HashSet<Variable>? variables = null)
    {
        _streamer = streamer;
        _variables = variables;
    }

    /// <summary>
    /// Parses starting AT the <c>wait</c> keyword. Consumes through the
    /// terminating <c>;</c>. On any parse anomaly falls back to
    /// <see cref="IsTerminal"/> = true so the process at least halts
    /// cleanly instead of consuming forever.
    /// </summary>
    public void Syntetize()
    {
        if (_streamer == null) return;

        _streamer.MoveNext(); // consume 'wait'

        var current = _streamer.Current?.Value?.ToLowerInvariant();
        if (current == null || current == ";")
        {
            // Bare `wait;` — terminal.
            IsTerminal = true;
            if (_streamer.Current?.Value == ";") _streamer.MoveNext();
            return;
        }

        if (current == "for")
        {
            _streamer.MoveNext(); // consume 'for'

            // Amount token.
            var amountStr = _streamer.Current?.Value ?? "0";
            if (!long.TryParse(amountStr, out long amount))
            {
                // Fall back: treat as terminal so we don't run away.
                IsTerminal = true;
                while (_streamer.Current != null && _streamer.Current.Value != ";")
                    _streamer.MoveNext();
                if (_streamer.Current?.Value == ";") _streamer.MoveNext();
                return;
            }
            _streamer.MoveNext(); // consume amount

            // Unit token.
            var unit = _streamer.Current?.Value?.ToLowerInvariant() ?? "ns";
            long factor = unit switch
            {
                "fs"  => 0,                      // sub-ns rounds to 0 — the engine's tick resolution is ns-scale
                "ps"  => 0,
                "ns"  => 1,
                "us"  => 1_000,
                "ms"  => 1_000_000,
                "sec" => 1_000_000_000,
                "min" => 60_000_000_000,
                "hr"  => 3_600_000_000_000,
                _     => 1,
            };
            DelayNs = amount * factor;
            IsTimed = DelayNs > 0;
            IsTerminal = !IsTimed;
            _streamer.MoveNext(); // consume unit

            if (_streamer.Current?.Value == ";") _streamer.MoveNext();
            return;
        }

        if (current == "until")
        {
            // F-G-13b (2026-07-18): `wait until <cond>;` — capture the raw
            // condition tokens between `until` and the terminating `;`,
            // then build an Operation just like an if-branch condition.
            _streamer.MoveNext(); // consume 'until'
            var tokens = new List<string>();
            while (_streamer.Current != null && _streamer.Current.Value != ";")
            {
                var v = _streamer.Current.Value;
                if (!string.IsNullOrEmpty(v)) tokens.Add(v);
                _streamer.MoveNext();
            }
            if (_streamer.Current?.Value == ";") _streamer.MoveNext();

            if (tokens.Count == 0 || _variables == null)
            {
                // Malformed or no variable set to bind against — fall back
                // to terminal so the process halts cleanly.
                IsTerminal = true;
                return;
            }

            string conditionRaw = string.Join(" ", tokens);
            try
            {
                Condition = new Operation(conditionRaw, _variables);
            }
            catch
            {
                IsTerminal = true;
                return;
            }

            // Sensitivity extraction: any variable name whose token
            // appears in the raw condition. Case-insensitive (matches the
            // Operation binding behaviour). This is a name-scan rather
            // than an AST walk so it survives DeferredFunction wrappers
            // like `rising_edge(clk)` — the `clk` token still appears.
            // Tier 4.1 (2026-07-28): attribute tokens like `clk'event`
            // arrive glued into one token via VECTORS_ATTRIBUTES; strip the
            // `'attr` suffix so the underlying signal still registers in
            // the wait's sensitivity set.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in tokens)
            {
                if (string.IsNullOrEmpty(raw) || !char.IsLetter(raw[0])) continue;
                int apos = raw.IndexOf('\'');
                string t = apos > 0 ? raw[..apos] : raw;
                if (!seen.Add(t)) continue;
                var match = _variables.FirstOrDefault(v =>
                    !ReferenceEquals(v, null) &&
                    !string.IsNullOrEmpty(v.Name) &&
                    v.Name.Equals(t, StringComparison.OrdinalIgnoreCase));
                if (match is not null) Sensitivity.Add(match);
            }

            if (Sensitivity.Count == 0)
            {
                // Nothing to wake on → the process would spin forever
                // waiting on a static condition. Treat as terminal to
                // match the bare-wait halt semantics.
                IsTerminal = true;
                return;
            }

            IsCondition = true;
            return;
        }

        // `wait on <sig>;` — capture as terminal for now (follow-up work).
        IsTerminal = true;
        while (_streamer.Current != null && _streamer.Current.Value != ";")
            _streamer.MoveNext();
        if (_streamer.Current?.Value == ";") _streamer.MoveNext();
    }

    /// <summary>
    /// F-G-13b: evaluates the stored <see cref="Condition"/> and returns
    /// true when it holds. Called from the delta engine on each signal
    /// change involving a member of <see cref="Sensitivity"/>.
    /// </summary>
    public bool EvaluateCondition()
    {
        if (Condition == null) return false;
        var result = Condition.Execute() as Variable;
        return result?.Value is bool b && b;
    }

    /// <summary>No-op; wait timing is handled specially by the engine.</summary>
    /// <returns>A completed task.</returns>
    public Task ExecuteAsync() => Task.CompletedTask; // Handled by the engine specially.

}
