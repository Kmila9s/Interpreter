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
    private readonly TokenStream _streamer;
    private readonly OperationDetector _detector = new();

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
        progress.ReportPhase(1f, $"Process complete ({Tasks.Count} tasks)", ProcessingPhase.ParsingProcess);
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
                        // Testbench-only construct (wait for X ns; wait until cond; wait;).
                        // Not synthesizable — consume tokens up to the terminating ';' so the
                        // parser doesn't drift into the next statement and treat 'for' as a
                        // ForLoopStructure (which previously caused runaway memory consumption).
                        SkipWaitStatement();
                        break;

                    case "report":
                    case "assert":
                        // Simulation-only diagnostic statements. Skip to ';'.
                        SkipToSemicolonInclusive();
                        break;

                    default:
                        // Skip unknown keywords
                        _streamer.MoveNext();
                        break;
                }
            }
            else
            {
                // For simple assignments (signal <= value or variable := value)
                SyntetizeAssignment(allVariables);
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
        _streamer.MoveNext(); // consume 'wait'
        SkipToSemicolonInclusive();
    }

    /// <summary>
    /// Skips tokens up to and including the next <c>;</c>. Stops early at
    /// <c>end</c> as a safety net so a missing semicolon doesn't run off the
    /// process body.
    /// </summary>
    private void SkipToSemicolonInclusive()
    {
        while (_streamer.Current != null
            && _streamer.Current.Value != ";"
            && _streamer.Current.Value.ToLower() != "end")
        {
            _streamer.MoveNext();
        }
        if (_streamer.Current?.Value == ";")
        {
            _streamer.MoveNext();
        }
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