using Interpreter.Interfaces;
using Interpreter.Models;
using Interpreter.Repositories;
using Parser.Models;
using Parser.Repositories;
using static Parser.Models.Constants.TYPE;
namespace Interpreter.Services;

/// <summary>
/// Represents a VHDL process.
/// </summary>
public partial class Process : ISControl
{
    /// <summary>
    /// Gets the sensitive list of the process.
    /// </summary>
    public HashSet<Variable> SensitiveList { get; private set; } = new();

    /// <summary>
    /// Gets the signals and ports of the process.
    /// </summary>
    public HashSet<Variable> SignalsPorts { get; private set; } = new();

    /// <summary>
    /// Gets the tasks of the process.
    /// </summary>
    public List<object> Tasks { get; private set; } = new();
    private HashSet<Variable> _variables = new();

    /// <summary>
    /// Pass 19 (2026-07-30) — process-local variable declarations (from
    /// the <c>process</c>'s <c>begin</c>-preceding decl part). The runtime
    /// consumes these via <c>allVariables = SignalsPorts ∪ _variables</c>;
    /// the synth path also needs to see them so signal-typed array
    /// locals (<c>variable mem : mem_t</c>) can be lowered into RAM
    /// cells the same way architecture-level signals are.
    /// </summary>
    public HashSet<Variable> Variables => _variables;
    private readonly TokenStream _streamer;

    /// <summary>
    /// The process label (<c>lbl : process</c>), or null. A closing
    /// <c>end process lbl;</c> must repeat it.
    /// </summary>
    public string? Label { get; init; }

    /// <summary>
    /// F-G-13 MVP (2026-07-18): program counter for the wait-scheduled
    /// process runner. Points to the next <see cref="Tasks"/> index to
    /// execute when the process resumes. 0 before the first run, advanced
    /// past each <c>WaitForStatement</c> when it yields.
    /// </summary>
    public int Pc { get; set; } = 0;

    /// <summary>
    /// F-G-13 MVP: true when this process's body contains any
    /// <see cref="Interpreter.Repositories.WaitForStatement"/>. Set during
    /// <see cref="SyntetizeBehaviour(CancellationToken)"/>. The engine uses this to decide
    /// whether the process is time-driven (F-G-13 path) or
    /// sensitivity-driven (legacy path via
    /// <see cref="ProcessScheduler"/>).
    /// </summary>
    public bool HasWait { get; private set; } = false;

    /// <summary>
    /// F-G-13d (Phase 2, 2026-07-22): tracks whether the process has
    /// yielded (via any wait) since the last time it started iterating
    /// from <c>Pc = 0</c>. Used by
    /// <c>DeltaCycleEngine.ExecuteProcessSegmentAsync</c> to decide whether
    /// an implicit loop-back is safe: <c>HasWait &amp;&amp; YieldedSinceLoopStart</c>
    /// means the last body pass advanced time, so we can re-enter safely.
    /// If the pass fell off end without yielding, we terminate to prevent
    /// a spin-loop.
    /// </summary>
    public bool YieldedSinceLoopStart { get; set; } = false;

    /// <summary>
    /// F-G-13 MVP: true when the process has hit a terminal wait (bare
    /// <c>wait;</c> or unsupported <c>wait on</c>) — the engine should
    /// NOT schedule any further resumes.
    /// </summary>
    public bool IsTerminated { get; set; } = false;

    /// <summary>
    /// F-G-13c (Session 23, 2026-07-18): stack of loop-iteration frames
    /// captured when a <c>wait</c> fires inside a for/while/simple loop
    /// body. On resume, the engine's segment runner pops these frames
    /// (deepest first) via the resumable loop executors so iteration
    /// continues from the statement after the wait.
    /// Empty stack means "not currently paused inside a loop" — resume
    /// falls through to the normal top-level <see cref="Pc"/> walk.
    /// </summary>
    public Stack<Interpreter.Models.IProcessFrame> Frames { get; } = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="Process"/> class.
    /// </summary>
    /// <param name="SignalsPorts">The signals and ports of the process.</param>
    /// <param name="Streamer">The token stream.</param>
    /// <param name="Types">User types visible to the process declarative part (the architecture's).</param>
    public Process(HashSet<Variable> SignalsPorts, TokenStream Streamer, TypeRegistry? Types = null)
    {
        _streamer = Streamer;
        this.SignalsPorts = SignalsPorts;
        _types = Types;
    }

    /// <summary>User types visible to the declarative part (record/array/enum variables).</summary>
    private readonly TypeRegistry? _types;

    /// <summary>
    /// Every signal of <paramref name="scope"/> named in <paramref name="texts"/> —
    /// the implicit sensitivity of an equivalent process or of <c>wait until</c>.
    /// String literals are ignored; constants are not signals.
    /// </summary>
    internal static IEnumerable<Variable> SignalsNamedIn(IEnumerable<string> texts, HashSet<Variable> scope)
    {
        foreach (var text in texts)
            foreach (System.Text.RegularExpressions.Match m in NameRegex().Matches(StringLiteralRegex().Replace(text, "")))
            {
                var signal = scope.FirstOrDefault(v => v is not null && !v.IsConstant && v.Name.Equals(m.Value, StringComparison.OrdinalIgnoreCase));
                if (signal is not null) yield return signal;
            }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"[A-Za-z_]\w*")]
    private static partial System.Text.RegularExpressions.Regex NameRegex();

    [System.Text.RegularExpressions.GeneratedRegex("\"[^\"]*\"")]
    private static partial System.Text.RegularExpressions.Regex StringLiteralRegex();

    /// <summary>
    /// The equivalent process of a concurrent statement (LRM §11.4/§11.5: concurrent
    /// assertion and procedure call): runs <paramref name="statement"/> at
    /// initialisation and on every event of the signals the statement reads.
    /// </summary>
    /// <param name="scope">Objects visible to the statement.</param>
    /// <param name="statement">The sequential form of the concurrent statement.</param>
    /// <param name="reads">Texts the statement reads (condition, arguments): every signal named in them is in the sensitivity list.</param>
    /// <param name="label">The statement label, or null.</param>
    public static Process Implicit(HashSet<Variable> scope, object statement, IEnumerable<string> reads, string? label)
    {
        var process = new Process(scope, null!) { Label = label };
        process.Tasks.Add(statement);
        process.SensitiveList.UnionWith(SignalsNamedIn(reads, scope));
        return process;
    }

    /// <summary>
    /// Executes the process asynchronously.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task ExecuteAsync()
    {
        Executer executer = new(Tasks);
        await executer.ExecuteAsync();
    }

    /// <summary>
    /// Executes the process with signal scheduling for delta cycle semantics.
    /// Signal assignments are scheduled instead of applied immediately.
    /// </summary>
    /// <param name="scheduler">The signal scheduler to use for updates.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task ExecuteWithSchedulerAsync(SignalScheduler scheduler)
    {
        // Create scheduled versions of operations
        var scheduledTasks = new List<object>();
        foreach (var task in Tasks)
        {
            if (task is Operation op)
            {
                scheduledTasks.Add(new ScheduledOperation(op, scheduler));
            }
            else
            {
                scheduledTasks.Add(task);
            }
        }

        Executer executer = new(scheduledTasks);
        await executer.ExecuteAsync();
    }

    /// <summary>
    /// Checks if this process is sensitive to any of the changed signals.
    /// </summary>
    /// <param name="changedSignals">The set of signals that changed.</param>
    /// <returns>True if the process should be re-executed.</returns>
    public bool IsSensitiveTo(HashSet<Variable> changedSignals)
    {
        return SensitiveList.Overlaps(changedSignals);
    }

    /// <summary>
    /// Synthesizes the process.
    /// </summary>
    public void Syntetize() => Syntetize(CancellationToken.None);

    /// <summary>
    /// Synthesizes the process.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public void Syntetize(CancellationToken cancellationToken)
    {
        var progress = ProgressReporter.Instance;
        progress.ReportPhase(0.1f, "Parsing process", ProcessingPhase.ParsingProcess);

        // Check for cancellation before starting
        cancellationToken.ThrowIfCancellationRequested();

        VariableSyntetizer syntetizer = new(_streamer, SignalsPorts, TypeRegistry: _types);
        SyntentizeSensitiveList();

        progress.ReportPhase(0.3f, $"Sensitivity list: {SensitiveList.Count} signal(s)", ProcessingPhase.ParsingProcess);

        // Check for cancellation after sensitivity list
        cancellationToken.ThrowIfCancellationRequested();

        if (_streamer.Current?.Value != "begin")
        {
            progress.ReportPhase(0.4f, "Parsing process variables", ProcessingPhase.ParsingProcess);
            syntetizer.Syntetize(cancellationToken);
            _variables = syntetizer.Variables;
            progress.ReportPhase(0.6f, $"Parsed {_variables.Count} variable(s)", ProcessingPhase.ParsingProcess);
        }
        _streamer.Expect(KEYWORDS); // begin

        progress.ReportPhase(0.7f, "Synthesizing process behavior", ProcessingPhase.ParsingProcess);
        SyntetizeBehaviour(cancellationToken);
        // F-G-13d (Phase 2, 2026-07-22): if any nested body (if branch,
        // case arm, loop) contains a wait, promote the process to
        // wait-driven so the engine's process-wake scheduler picks it up
        // instead of routing it through the sensitivity-driven path.
        // Pre-Phase-2 HasWait was only set for TOP-level `wait` tokens,
        // so a process with waits only inside an if body ran through
        // its full body once at t=0 with the waits behaving as zero-delay
        // yields — no time actually advanced.
        HasWait = ContainsNestedWait(Tasks);
        progress.ReportPhase(1f, $"Process complete ({Tasks.Count} tasks)", ProcessingPhase.ParsingProcess);
    }

    /// <summary>
    /// F-G-13d: recursively walk a task list looking for any
    /// <see cref="Interpreter.Repositories.WaitForStatement"/> anchored
    /// inside an if / case / loop body. Used to promote a process to
    /// wait-driven when top-level SyntetizeBehaviour didn't see a bare
    /// `wait` keyword. Cheap because process task lists rarely exceed
    /// a few dozen items and nesting depth is small.
    /// </summary>
    private static bool ContainsNestedWait(List<object> tasks, HashSet<string>? visitedProcs = null)
    {
        foreach (var t in tasks)
        {
            if (t is Interpreter.Repositories.WaitForStatement) return true;
            if (t is Interpreter.Repositories.IfStructure ifs)
            {
                foreach (var b in ifs.Branches)
                    if (ContainsNestedWait(b.Tasks, visitedProcs)) return true;
            }
            else if (t is Interpreter.Repositories.CaseStructure cs)
            {
                foreach (var b in cs.Branches)
                    if (ContainsNestedWait(b.Tasks, visitedProcs)) return true;
            }
            else if (t is Interpreter.Repositories.ForLoopStructure fs)
            {
                if (ContainsNestedWait(fs.Tasks, visitedProcs)) return true;
            }
            else if (t is Interpreter.Repositories.WhileLoopStructure ws)
            {
                if (ContainsNestedWait(ws.Tasks, visitedProcs)) return true;
            }
            else if (t is Interpreter.Repositories.SimpleLoopStructure ss)
            {
                if (ContainsNestedWait(ss.Tasks, visitedProcs)) return true;
            }
            // Phase 3 (Session 26, 2026-07-22): detect waits reached only
            // through procedure calls. Without this, a process that
            // exclusively waits by delegating to a procedure would have
            // HasWait = false, the delta engine would skip its implicit
            // outer-loop wrapper, and the simulation would silently drop
            // the wait behaviour.
            //
            // Detection is textual on the callee's BodyRaw (fast) with a
            // visited-set cycle guard — VHDL permits mutually recursive
            // procedures, and without the guard `A calls B calls A` would
            // stack-overflow during Process.Syntetize.
            else if (t is Interpreter.Repositories.ProcedureCallStatement pcall)
            {
                visitedProcs ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!visitedProcs.Add(pcall.ProcedureName)) continue; // cycle → skip
                var def = FunctionRegistry.Instance.Get(pcall.ProcedureName);
                if (def != null && !string.IsNullOrEmpty(def.BodyRaw)
                    && BodyRawContainsWait(def.BodyRaw))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Phase 3 (2026-07-22): cheap textual scan for a bare <c>wait</c>
    /// token in a procedure's raw body. Tracks string-literal parity via
    /// double-quote counting so a <c>report "wait"</c> doesn't false-fire.
    /// Correct for the vast majority of well-formed VHDL — an obfuscated
    /// procedure that hides a wait behind identifier concatenation would
    /// slip through, but that shape doesn't occur in practice.
    /// </summary>
    private static bool BodyRawContainsWait(string bodyRaw)
    {
        var tokens = bodyRaw.Split(new[] { ' ', '\n', '\r', '\t' },
            StringSplitOptions.RemoveEmptyEntries);
        bool insideString = false;
        foreach (var t in tokens)
        {
            int qc = 0;
            foreach (var c in t) if (c == '"') qc++;
            bool wasInside = insideString;
            if (qc % 2 == 1) insideString = !insideString;
            if (!wasInside && t.Equals("wait", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }


    /// <summary>
    /// Synthesizes the sensitive list of the process.
    /// </summary>
    private void SyntentizeSensitiveList()
    {
        _streamer.Expect(KEYWORDS);
        if (_streamer.Current?.Value == "(")
        {
            _streamer.Expect(PARENTHESIS);
            while (_streamer.Current?.Value != ")")
            {
                string varName = _streamer.Current?.Value ?? string.Empty;
                _streamer.Expect(IDENTIFIERS);
                var variable = SignalsPorts.FirstOrDefault(p => p.Name.Equals(varName));
                if (variable is null)
                    throw ParserException.UndeclaredVariable(varName, _streamer.Previous);
                else
                {
                    SensitiveList.Add(variable);
                }
                if (_streamer.Current?.Value == ",")
                    _streamer.MoveNext();
            }
            _streamer.Expect(PARENTHESIS);
        }
        // The optional `is` of `process [(…)] is` (VHDL-93).
        if (string.Equals(_streamer.Current?.Value, "is", StringComparison.OrdinalIgnoreCase))
            _streamer.MoveNext();
    }


    /// <summary>
    /// Synthesizes the statement part of the process and its closing
    /// <c>end [postponed] process [label];</c>.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public void SyntetizeBehaviour(CancellationToken cancellationToken)
    {
        // Signals/ports plus the process-local variables.
        HashSet<Variable> allVariables = [.. SignalsPorts, .. _variables];
        new SequentialStatementReader(_streamer, allVariables, SequentialBody.Process, cancellationToken)
            .ReadUntil(Tasks, "end");
        EndClause.Consume(_streamer, "process", Label, allowPostponed: true);
    }
}