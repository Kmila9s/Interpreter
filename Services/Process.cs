using System.Text;
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
public class Process : ISControl, IControl
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
    private readonly OperationDetector _detector = new();

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
    /// <see cref="SyntetizeBehaviour()"/>. The engine uses this to decide
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
    public Process(HashSet<Variable> SignalsPorts, TokenStream Streamer)
    {
        _streamer = Streamer;
        this.SignalsPorts = SignalsPorts;
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

        VariableSyntetizer syntetizer = new(_streamer, SignalsPorts);
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
        if (!HasWait && ContainsNestedWait(Tasks)) HasWait = true;
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
    /// Synthesizes the process from raw data.
    /// </summary>
    /// <param name="rawProcess">The raw data of the process.</param>
    public void Syntetize(string[] rawProcess) => throw new NotImplementedException();

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
    }

    /// <summary>
    /// Synthesizes the behavior of the process.
    /// </summary>
    public void SyntetizeBehaviour() => SyntetizeBehaviour(CancellationToken.None);

    /// <summary>
    /// Synthesizes the behavior of the process.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public void SyntetizeBehaviour(CancellationToken cancellationToken)
    {
        // Combine all available variables: signals/ports and local process variables
        HashSet<Variable> allVariables = [.. SignalsPorts, .. _variables];

        int iterations = 0;
        const int maxIterations = 10000;

        while (_streamer.Current?.Value.ToLower() != "end" && iterations < maxIterations)
        {
            iterations++;
            // Check for cancellation at each iteration
            cancellationToken.ThrowIfCancellationRequested();

            if (ParseTrace.Enabled)
                ParseTrace.Log($"Process.body iter={iterations} tok='{_streamer.Current?.Value}' @{(_streamer.Current?.Line ?? 0) + 1}");

            if (_streamer.Current?.Type == KEYWORDS)
            {
                switch (_streamer.Current.Value.ToLower())
                {
                    case "if":
                        IfStructure ifStructure = new(allVariables, _streamer);
                        ifStructure.Syntetize();
                        Tasks.Add(ifStructure);
                        // Consume 'end if ;'
                        ConsumeEndKeyword("if");
                        break;

                    case "case":
                        CaseStructure caseStructure = new(allVariables, _streamer);
                        caseStructure.Syntetize();
                        Tasks.Add(caseStructure);
                        // Consume 'end case ;'
                        ConsumeEndKeyword("case");
                        break;

                    case "for":
                        ForLoopStructure forLoop = new(allVariables, _streamer);
                        forLoop.Syntetize();
                        Tasks.Add(forLoop);
                        // Consume 'end loop ;'
                        ConsumeEndKeyword("loop");
                        break;

                    case "while":
                        WhileLoopStructure whileLoop = new(allVariables, _streamer);
                        whileLoop.Syntetize();
                        Tasks.Add(whileLoop);
                        // Consume 'end loop ;'
                        ConsumeEndKeyword("loop");
                        break;

                    case "loop":
                        // Simple loop (infinite until exit)
                        SimpleLoopStructure simpleLoop = new(allVariables, _streamer);
                        simpleLoop.Syntetize();
                        Tasks.Add(simpleLoop);
                        // Consume 'end loop ;'
                        ConsumeEndKeyword("loop");
                        break;

                    case "exit":
                        ExitStatement exitStmt = new(allVariables, _streamer);
                        exitStmt.Syntetize();
                        Tasks.Add(exitStmt);
                        break;

                    case "next":
                        NextStatement nextStmt = new(allVariables, _streamer);
                        nextStmt.Syntetize();
                        Tasks.Add(nextStmt);
                        break;

                    case "null":
                        NullStatement nullStmt = new(_streamer);
                        nullStmt.Syntetize();
                        Tasks.Add(nullStmt);
                        break;

                    case "return":
                        ReturnStatement returnStmt = new(allVariables, _streamer);
                        returnStmt.Syntetize();
                        Tasks.Add(returnStmt);
                        break;

                    case "wait":
                        // F-G-13 (2026-07-18): capture the wait as a
                        // WaitForStatement task so the delta engine can
                        // yield the process here and schedule its resume
                        // at current_tick + delay. F-G-13b (Session 23):
                        // pass `allVariables` so `wait until <cond>` can
                        // compile the condition into an Operation and
                        // extract the referenced-signals sensitivity set.
                        var waitStmt = new Interpreter.Repositories.WaitForStatement(_streamer, allVariables);
                        waitStmt.Syntetize();
                        Tasks.Add(waitStmt);
                        HasWait = true;
                        break;

                    case "report":
                        // F-G-11 (2026-07-18): capture and execute at runtime
                        // via ReportBus. Prior to F-G-11 this was skipped so
                        // testbench self-checking sweeps produced no output.
                        var reportStmt = new Interpreter.Repositories.ReportStatement(allVariables, _streamer);
                        reportStmt.Syntetize();
                        Tasks.Add(reportStmt);
                        break;

                    case "assert":
                        // F-G-16 (2026-07-18): evaluate condition + emit the
                        // report message when false. Prior to F-G-16 this
                        // was skipped.
                        var assertStmt = new Interpreter.Repositories.AssertStatement(allVariables, _streamer);
                        assertStmt.Syntetize();
                        Tasks.Add(assertStmt);
                        break;

                    default:
                        // Skip unknown keywords
                        _streamer.MoveNext();
                        break;
                }
            }
            else
            {
                // F-G-12 (2026-07-18): dispatch procedure calls before
                // falling through to signal/variable assignment. Detection:
                // current token is an identifier whose name is registered
                // in FunctionRegistry as a procedure (IsPure=false). Without
                // this, `press_key(KEY_1);` / `begin_case("A1");` used to
                // trip SyntetizeAssignment which then drifted through the
                // arg list without effect.
                if (Interpreter.Repositories.ProcedureCallStatement
                    .IsProcedureCall(_streamer, allVariables))
                {
                    var call = new Interpreter.Repositories.ProcedureCallStatement(
                        allVariables, _streamer);
                    call.Syntetize();
                    if (call.IsValid) Tasks.Add(call);
                }
                else
                {
                    // For simple assignments (signal <= value or variable := value)
                    SyntetizeAssignment(allVariables);
                }
            }
        }

        // Consume 'end process ;'
        _streamer.MoveNext(); // consume 'end'
        if (_streamer.Current?.Value.ToLower() == "process")
        {
            _streamer.MoveNext(); // consume 'process'
        }
        if (_streamer.Current?.Value == ";")
        {
            _streamer.MoveNext(); // consume ';'
        }
    }

    /// <summary>
    /// Consumes a VHDL <c>wait</c> statement up to and including its terminating
    /// <c>;</c>. Handles all forms: <c>wait;</c>, <c>wait for X ns;</c>,
    /// <c>wait until cond;</c>, <c>wait on sig_list;</c>, and combinations.
    /// </summary>
    /// <remarks>
    /// Testbench-only — not synthesizable. Without this, <c>wait for DELAY</c>
    /// caused the process synthesizer to misclassify the trailing <c>for</c> as
    /// a ForLoop, then keep growing an expression buffer until OOM.
    /// </remarks>
    private void SkipWaitStatement()
    {
        // F-G-08 pass 4 (2026-07-17): delegate to the shared helper which
        // correctly tracks in-string state so `end` inside a fragmented
        // string literal (`report "TB: end of stimulus"` etc.) doesn't
        // cause an early exit that leaves the process body mid-parse.
        ParseTrace.SkipWaitStatement(_streamer);
    }

    /// <summary>
    /// Skips tokens up to and including the next <c>;</c>. Delegates to the
    /// shared <see cref="ParseTrace.SkipToSemicolonInclusive"/> helper so
    /// Process and the nested-body dispatchers use identical semantics —
    /// including the F-G-08 pass 4 fix that treats a fragmented string
    /// literal's contents (words like <c>end</c>) as opaque.
    /// </summary>
    private void SkipToSemicolonInclusive()
    {
        ParseTrace.SkipToSemicolonInclusive(_streamer);
    }

    /// <summary>
    /// Consumes 'end keyword ;' tokens.
    /// </summary>
    private void ConsumeEndKeyword(string keyword)
    {
        if (_streamer.Current?.Value.ToLower() == "end")
        {
            _streamer.MoveNext(); // consume 'end'
            if (_streamer.Current?.Value.ToLower() == keyword)
            {
                _streamer.MoveNext(); // consume keyword
            }
            // VHDL-2008 matching-case closes with `end case?;` — accept the
            // optional trailing `?` between keyword and semicolon.
            if (_streamer.Current?.Value == "?") _streamer.MoveNext();
            if (_streamer.Current?.Value == ";")
            {
                _streamer.MoveNext(); // consume ';'
            }
        }
    }

    /// <summary>
    /// Synthesizes a simple assignment statement.
    /// </summary>
    private void SyntetizeAssignment(HashSet<Variable> variables)
    {
        // Phase B+ Deep AST (follow-up): capture the LHS line. Process
        // bodies were the biggest pedagogical gap before this — DFF /
        // shift-register / fix-latch lessons all assign inside processes,
        // and without this they'd fall back to the regex map and
        // over-highlight every branch.
        int assignLine = _streamer.Current?.Line ?? 0;

        var rawDataBuilder = new StringBuilder();
        while (_streamer.Current?.Value != ";" && _streamer.Current?.Value.ToLower() != "end")
        {
            rawDataBuilder.Append(_streamer.Current?.Value + " ");
            _streamer.MoveNext();
        }

        string operation = rawDataBuilder.ToString().Trim();
        if (string.IsNullOrEmpty(operation)) return;

        if (_streamer.Current?.Value == ";")
        {
            _streamer.MoveNext(); // consume ';'
        }

        var type = _detector.GetOperationType(operation);
        switch (type)
        {
            case OperationType.ASSIGNATION:
                Operation op = new(operation, variables, sourceLine: assignLine);
                Tasks.Add(op);
                break;
        }
    }

    /// <summary>
    /// Synthesizes the behavior of the process from raw data.
    /// </summary>
    /// <param name="strings">The raw data of the behavior.</param>
    public void SyntetizeBehaviour(string[] strings) => throw new NotImplementedException();
}