using Interpreter.Models;
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
/// <see cref="DelayFs"/> from the source, <see cref="IsTimed"/> = true,
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
/// <item><description><c>wait on &lt;sig&gt;, …;</c> — <see cref="IsCondition"/> with the
/// listed signals as <see cref="Sensitivity"/> and no condition: any event wakes it.</description></item>
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
    /// <summary>1-based source line of the statement (errors in its condition/selector report it).</summary>
    private int _line;


    /// <summary>Delay in femtoseconds parsed from the source; 0 for bare wait.</summary>
    public long DelayFs { get; private set; } = 0;

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
    /// Parses <c>wait [on s1, s2] [until cond] [for time];</c> (LRM §10.2), consuming
    /// the terminating <c>;</c>. Clause order is the LRM one; anything else is a
    /// <see cref="ParserException"/>.
    /// </summary>
    public void Syntetize()
    {
        if (_streamer == null) return;
        var waitToken = _streamer.Current;
        _line = waitToken?.SourceLine ?? 0;
        _streamer.MoveNext(); // consume 'wait'

        var on = new List<Token>();
        var until = new List<string>();
        var forTokens = new List<Token>();
        string clause = string.Empty;
        while (_streamer.Current is { } cur && cur.Value != ";")
        {
            string low = cur.Value.ToLowerInvariant();
            bool next = low switch
            {
                "on" => clause == string.Empty,
                "until" => clause is "" or "on",
                "for" => clause is "" or "on" or "until",
                _ => false,
            };
            if (next) { clause = low; _streamer.MoveNext(); continue; }
            switch (clause)
            {
                case "on": if (cur.Value != ",") on.Add(cur); break;
                case "until": until.Add(cur.Value); break;
                case "for": forTokens.Add(cur); break;
                default:
                    throw new ParserException("'on', 'until', 'for' or ';' expected after 'wait'", cur);
            }
            _streamer.MoveNext();
        }
        if (_streamer.Current is null)
            throw new ParserException("';' expected at the end of the wait statement", _streamer.Previous);
        _streamer.MoveNext(); // consume ';'

        if (on.Count == 0 && until.Count == 0)
        {
            if (forTokens.Count > 0) SetDelay(forTokens);
            else IsTerminal = true;   // bare `wait;`
            return;
        }
        // `wait on … until … [for T]`: the condition, with an optional timeout (G78).
        SetCondition(on, until);
        if (forTokens.Count > 0)
        {
            SetDelay(forTokens);
            IsTerminal = !IsCondition && !IsTimed;
        }
    }

    /// <summary><c>wait for &lt;time expression&gt;;</c>.</summary>
    private void SetDelay(List<Token> tokens)
    {
        string text = string.Join(' ', tokens.Select(t => t.Value));
        if (!Interpreter.Models.TimeValue.TryEvaluateFs(text, _variables, out long fs))
            throw new ParserException($"time expression '{text}' is not supported in a wait statement yet", tokens[0]);
        DelayFs = fs;
        IsTimed = DelayFs > 0;
        IsTerminal = !IsTimed;
    }

    /// <summary><c>wait on … [until …];</c> / <c>wait until …;</c>.</summary>
    private void SetCondition(List<Token> on, List<string> until)
    {
        if (_variables == null) { IsTerminal = true; return; }
        if (until.Count > 0) Condition = new Operation(string.Join(" ", until), _variables, _line);

        if (on.Count > 0)
        {
            foreach (var name in on)
            {
                var sig = _variables.FirstOrDefault(v => v is not null && v.Name.Equals(name.Value, StringComparison.OrdinalIgnoreCase))
                    ?? throw ParserException.UndeclaredVariable(name.Value, name);
                Sensitivity.Add(sig);
            }
        }
        else
        {
            // Implicit sensitivity of `wait until`: every signal named in the condition.
            Sensitivity.UnionWith(ImplicitSensitivity.SignalsIn(until, _variables));
        }

        // Nothing can wake the process: it halts, like a bare wait.
        if (Sensitivity.Count == 0) { IsTerminal = true; return; }
        IsCondition = true;
    }

    /// <summary>
    /// True when the process may resume: the <c>until</c> condition holds, or —
    /// for <c>wait on</c> without a condition — any event on the sensitivity set.
    /// </summary>
    public bool EvaluateCondition()
    {
        if (Condition == null) return IsCondition;
        var result = Condition.Execute();
        return Truth.Of(result);
    }

    /// <summary>No-op; wait timing is handled specially by the engine.</summary>
    /// <returns>A completed task.</returns>
    public Task ExecuteAsync() => Task.CompletedTask; // Handled by the engine specially.

}
