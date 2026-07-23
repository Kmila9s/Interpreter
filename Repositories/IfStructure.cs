using System.Text;
using Interpreter.Interfaces;
using Interpreter.Models;
using Interpreter.Services;
using Parser.Models;
using Parser.Repositories;
using static Parser.Models.Constants.TYPE;

namespace Interpreter.Repositories;

/// <summary>
/// Represents a single branch (if, elsif, or else) of a VHDL if statement.
/// </summary>
public class IfBranch
{
    /// <summary>
    /// Gets or sets the condition operation for this branch. Null for else branches.
    /// </summary>
    public Operation? Condition { get; set; }

    /// <summary>
    /// Gets or sets the list of tasks to execute when this branch is taken.
    /// </summary>
    public List<object> Tasks { get; set; } = new();

    /// <summary>
    /// Gets or sets whether this is an else branch (no condition).
    /// </summary>
    public bool IsElse { get; set; } = false;

    /// <summary>
    /// 1-based source line of the <c>if</c> / <c>elsif</c> keyword for this
    /// branch (#5). Lets the live engine emit a condition-evaluation signal
    /// tagged with the line so a breakpoint dropped on the condition pauses
    /// when the branch is taken. Zero means unknown.
    /// </summary>
    public int ConditionLine { get; set; }
}

/// <summary>
/// Represents and manages the logic for a VHDL 'if' statement.
/// It synthesizes the condition and the execution block, and executes it if the condition is met.
/// Supports full if-elsif-else-end if structure.
/// </summary>
/// <remarks>
/// <para><strong>Supported VHDL Syntax:</strong></para>
/// <code>
/// if condition1 then
///     statements;
/// elsif condition2 then
///     statements;
/// else
///     statements;
/// end if;
/// </code>
/// <para><strong>v0.13.0 Improvements:</strong></para>
/// <list type="bullet">
/// <item>Added support for nested case statements within if/elsif/else branches</item>
/// <item>Unified ConsumeEndIf() to generic ConsumeEndKeyword() for if and case structures</item>
/// <item>Enhanced branch body synthesis to handle case keyword</item>
/// </list>
/// <para><strong>Design:</strong></para>
/// <para>
/// Uses a flat list of IfBranch objects rather than recursive structure for better
/// maintainability and execution control. Each branch contains its condition (or IsElse flag)
/// and list of tasks to execute.
/// </para>
/// </remarks>
public class IfStructure : ISControl
{
    /// <summary>The variables in scope during condition and body evaluation.</summary>
    private readonly HashSet<Variable> _variables = new();
    /// <summary>The token stream used for parsing the if structure.</summary>
    private readonly TokenStream _streamer;
    /// <summary>Detector used to classify operations found inside branch bodies.</summary>
    private readonly OperationDetector _detector = new();

    /// <summary>
    /// Gets the list of tasks or operations to be executed if the condition is true.
    /// </summary>
    public List<object> Tasks { get; private set; } = new();

    /// <summary>
    /// All branches (if, elsif*, else?) in order.
    /// </summary>
    public List<IfBranch> Branches { get; private set; } = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="IfStructure"/> class.
    /// </summary>
    /// <param name="Variables">The set of variables available in the current scope.</param>
    /// <param name="Tokenizer">The token stream to read the if structure from.</param>
    public IfStructure(HashSet<Variable> Variables, TokenStream Tokenizer)
    {
        _variables = Variables;
        _streamer = Tokenizer;
    }

    /// <summary>
    /// Executes the if structure asynchronously. It evaluates conditions in order
    /// and executes the first branch whose condition is true (or the else branch).
    /// </summary>
    public async Task ExecuteAsync()
    {
        foreach (var branch in Branches)
        {
            // Else branch - always execute if we reach it
            if (branch.IsElse)
            {
                Executer executer = new(branch.Tasks);
                await executer.ExecuteAsync();
                return;
            }

            // Evaluate condition
            if (branch.Condition != null)
            {
                var result = branch.Condition.Execute() as Variable;
                if (result?.Value is bool condition && condition)
                {
                    Executer executer = new(branch.Tasks);
                    await executer.ExecuteAsync();
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Synthesizes the complete if structure from the token stream.
    /// Handles if-elsif-else-end if as a single unit.
    /// </summary>
    public void Syntetize()
    {
        ParseTrace.Log($"IfStructure @{(_streamer.Current?.Line ?? 0) + 1}");
        // Parse the main 'if' branch
        SyntetizeIfBranch();

        // Continue parsing elsif and else branches
        while (_streamer.Current != null)
        {
            var currentValue = _streamer.Current.Value.ToLower();

            if (currentValue == "elsif")
            {
                SyntetizeElsifBranch();
            }
            else if (currentValue == "else")
            {
                SyntetizeElseBranch();
            }
            else if (currentValue == "end")
            {
                // End of if structure - don't consume here, let caller handle it
                break;
            }
            else
            {
                break;
            }
        }

        // Also populate the Tasks list with the first branch for backwards compatibility
        if (Branches.Count > 0)
        {
            Tasks = Branches[0].Tasks;
        }
    }

    /// <summary>
    /// Synthesizes the main 'if' branch.
    /// </summary>
    private void SyntetizeIfBranch()
    {
        int condLine = _streamer.Current?.Line ?? 0; // line of 'if' keyword (#5)
        _streamer.Expect(KEYWORDS); // consume 'if'

        var branch = new IfBranch { ConditionLine = condLine };

        // Parse condition (up to 'then')
        var rawDataBuilder = new StringBuilder();
        while (_streamer.Current?.Value.ToLower() != "then")
        {
            rawDataBuilder.Append(_streamer.Current?.Value + " ");
            _streamer.MoveNext();
        }
        _streamer.Expect(KEYWORDS); // consume 'then'

        string conditionRaw = rawDataBuilder.ToString().Trim();
        branch.Condition = new Operation(conditionRaw, _variables);

        // Parse body
        SyntetizeBranchBody(branch);

        Branches.Add(branch);
    }

    /// <summary>
    /// Synthesizes an 'elsif' branch.
    /// </summary>
    private void SyntetizeElsifBranch()
    {
        int condLine = _streamer.Current?.Line ?? 0; // line of 'elsif' keyword (#5)
        _streamer.Expect(KEYWORDS); // consume 'elsif'

        var branch = new IfBranch { ConditionLine = condLine };

        // Parse condition (up to 'then')
        var rawDataBuilder = new StringBuilder();
        while (_streamer.Current?.Value.ToLower() != "then")
        {
            rawDataBuilder.Append(_streamer.Current?.Value + " ");
            _streamer.MoveNext();
        }
        _streamer.Expect(KEYWORDS); // consume 'then'

        string conditionRaw = rawDataBuilder.ToString().Trim();
        branch.Condition = new Operation(conditionRaw, _variables);

        // Parse body
        SyntetizeBranchBody(branch);

        Branches.Add(branch);
    }

    /// <summary>
    /// Synthesizes the 'else' branch.
    /// </summary>
    private void SyntetizeElseBranch()
    {
        _streamer.Expect(KEYWORDS); // consume 'else'

        var branch = new IfBranch { IsElse = true };

        // Parse body
        SyntetizeBranchBody(branch);

        Branches.Add(branch);
    }

    /// <summary>
    /// Synthesizes the body of a branch until 'elsif', 'else', or 'end' is found.
    /// </summary>
    /// <param name="branch">The branch whose body is being synthesized.</param>
    private void SyntetizeBranchBody(IfBranch branch)
    {
        int iterations = 0;
        const int maxIterations = 5000;
        while (!IsEndOfBranch() && iterations < maxIterations)
        {
            iterations++;
            var currentValue = _streamer.Current?.Value.ToLower();

            if (_streamer.Current?.Type == KEYWORDS)
            {
                switch (currentValue)
                {
                    case "if":
                        // Nested if statement - recursively parse and add to branch tasks
                        IfStructure nestedIf = new(_variables, _streamer);
                        nestedIf.Syntetize();
                        branch.Tasks.Add(nestedIf);
                        // Consume 'end if ;' tokens after nested structure
                        ConsumeEndKeyword("if");
                        break;

                    case "case":
                        // Nested case statement - allows case statements inside if/elsif/else branches
                        // Added in v0.13.0 to support complex control flow nesting
                        CaseStructure nestedCase = new(_variables, _streamer);
                        nestedCase.Syntetize();
                        branch.Tasks.Add(nestedCase);
                        // Consume 'end case ;' tokens after nested structure
                        ConsumeEndKeyword("case");
                        break;

                    case "for":
                        ForLoopStructure forLoop = new(_variables, _streamer);
                        forLoop.Syntetize();
                        branch.Tasks.Add(forLoop);
                        ConsumeEndKeyword("loop");
                        break;

                    case "while":
                        WhileLoopStructure whileLoop = new(_variables, _streamer);
                        whileLoop.Syntetize();
                        branch.Tasks.Add(whileLoop);
                        ConsumeEndKeyword("loop");
                        break;

                    case "loop":
                        SimpleLoopStructure simpleLoop = new(_variables, _streamer);
                        simpleLoop.Syntetize();
                        branch.Tasks.Add(simpleLoop);
                        ConsumeEndKeyword("loop");
                        break;

                    case "exit":
                        ExitStatement exitStmt = new(_variables, _streamer);
                        exitStmt.Syntetize();
                        branch.Tasks.Add(exitStmt);
                        break;

                    case "next":
                        NextStatement nextStmt = new(_variables, _streamer);
                        nextStmt.Syntetize();
                        branch.Tasks.Add(nextStmt);
                        break;

                    case "null":
                        NullStatement nullStmt = new(_streamer);
                        nullStmt.Syntetize();
                        branch.Tasks.Add(nullStmt);
                        break;

                    case "return":
                        ReturnStatement returnStmt = new(_variables, _streamer);
                        returnStmt.Syntetize();
                        branch.Tasks.Add(returnStmt);
                        break;

                    // F-G-08 pass 4 (2026-07-17): testbench-only statements.
                    // Prevents nested `wait for … ns;` / `report …;` / `assert …;`
                    // from mis-dispatching their trailing tokens as for/while headers.
                    case "wait":
                        Interpreter.Services.ParseTrace.SkipWaitStatement(_streamer);
                        break;
                    case "report":
                        // F-G-11b (2026-07-18): execute reports nested inside
                        // an if/elsif/else branch.
                        var _rpt = new ReportStatement(_variables, _streamer);
                        _rpt.Syntetize();
                        branch.Tasks.Add(_rpt);
                        break;
                    case "assert":
                        // F-G-16 (2026-07-18): execute asserts nested inside an if/elsif/else branch.
                        var _ass = new AssertStatement(_variables, _streamer);
                        _ass.Syntetize();
                        branch.Tasks.Add(_ass);
                        break;

                    default:
                        // Other keywords - skip
                        _streamer.MoveNext();
                        break;
                }
            }
            else
            {
                // Simple assignations
                SyntetizeAssingation(branch);
            }
        }
    }

    /// <summary>
    /// Checks if we've reached the end of the current branch (elsif, else, or end).
    /// </summary>
    /// <returns>True if the current token is 'elsif', 'else', 'end', or the stream is exhausted.</returns>
    private bool IsEndOfBranch()
    {
        if (_streamer.Current == null) return true;

        var currentValue = _streamer.Current.Value.ToLower();
        return currentValue == "elsif" ||
               currentValue == "else" ||
               currentValue == "end";
    }

    /// <summary>
    /// Consumes 'end keyword ;' tokens after a nested structure.
    /// </summary>
    /// <param name="keyword">The keyword expected after 'end' (e.g., "if", "loop", "case").</param>
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
    /// Synthesizes a simple assignment statement within a branch.
    /// </summary>
    /// <param name="branch">The branch to add the parsed assignment to.</param>
    private void SyntetizeAssingation(IfBranch branch)
    {
        // Phase B+ Deep AST (follow-up 2026-05-19): capture the line of the
        // assignment's LHS token before the streamer consumes it. Works for
        // every `sig <= …` inside an if/elsif/else branch, which was the
        // biggest pedagogical gap of the first AST pass.
        int assignLine = _streamer.Current?.Line ?? 0;

        var rawDataBuilder = new StringBuilder();
        int iterations = 0;
        const int maxIterations = 1000;
        while (_streamer.Current?.Value != ";" && !IsEndOfBranch() && iterations < maxIterations)
        {
            iterations++;
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
                Operation op = new(operation, _variables, sourceLine: assignLine);
                branch.Tasks.Add(op);
                break;
        }
    }

    /// <summary>
    /// Synthesizes simple assignment statements within the if block (for backwards compatibility).
    /// </summary>
    public void SyntetizeAssingations()
    {
        if (Branches.Count > 0)
        {
            SyntetizeAssingation(Branches[0]);
        }
    }

    /// <summary>
    /// Synthesizes the behavior from an array of strings. This method is not implemented.
    /// </summary>
    /// <param name="data">The raw data for synthesis.</param>
    public void Syntetize(string[] data) => throw new NotImplementedException();
}