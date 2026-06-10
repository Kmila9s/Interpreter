using System.Text;
using Interpreter.Interfaces;
using Interpreter.Models;
using Interpreter.Services;
using Parser.Models;
using Parser.Repositories;
using static Parser.Models.Constants.TYPE;
using Constants = Parser.Models.Constants;

namespace Interpreter.Repositories;

// Note: This file uses ExitException and NextException from ControlExceptions.cs

/// <summary>
/// Represents and manages the logic for a VHDL 'for' loop statement.
/// Parses and executes for loops with ascending (to) or descending (downto) iteration.
/// </summary>
/// <remarks>
/// <para><strong>Supported VHDL Syntax:</strong></para>
/// <code>
/// -- Ascending loop
/// for i in 0 to 7 loop
///     statements;
/// end loop;
///
/// -- Descending loop
/// for i in 7 downto 0 loop
///     statements;
/// end loop;
///
/// -- Loop with expression ranges
/// for i in 0 to ARRAY_SIZE - 1 loop
///     statements;
/// end loop;
/// </code>
/// <para><strong>Features:</strong></para>
/// <list type="bullet">
/// <item>Ascending ranges with 'to' keyword</item>
/// <item>Descending ranges with 'downto' keyword</item>
/// <item>Expression-based range bounds (e.g., TREE_DEPTH - 1)</item>
/// <item>Automatic loop variable creation and scoping</item>
/// <item>Nested control structures (if, case, loops) within body</item>
/// <item>Exit statement support with optional 'when' condition</item>
/// </list>
/// <para><strong>v0.13.0 Improvements:</strong></para>
/// <list type="bullet">
/// <item>Added expression evaluation for range bounds via ParseRangeValue()</item>
/// <item>Added iteration guards to prevent infinite loops during synthesis</item>
/// <item>Support for undeclared identifiers in range expressions (returns 0)</item>
/// </list>
/// <para><strong>Execution:</strong></para>
/// <para>
/// During execution, a loop variable is created with the specified name and added
/// to a temporary scope. The loop iterates from RangeStart to RangeEnd (inclusive),
/// updating the loop variable each iteration before executing the body tasks.
/// </para>
/// </remarks>
public class ForLoopStructure : ISControl
{
    /// <summary>The variables in scope during range evaluation and loop body execution.</summary>
    private readonly HashSet<Variable> _variables;

    /// <summary>
    /// The single Variable instance representing the loop iterator. Created
    /// at <see cref="Syntetize"/> time and re-used by <see cref="ExecuteAsync"/>
    /// so DeferredIndex/expression-stack references stay live across
    /// iterations. Bench Phase F bug #14 fix (2026-06-06).
    /// </summary>
    private Variable? _loopVar;

    /// <summary>
    /// Read-only accessor so the engine's scheduled-execution path can
    /// reuse the synth-time iterator instance — body operations (notably
    /// <c>DynamicIndex</c> for `signal(i)` reads) captured a reference to
    /// it and a parallel runtime copy would leave them stale.
    /// </summary>
    public Variable? LoopVar => _loopVar;
    /// <summary>The token stream used for parsing the for loop structure.</summary>
    private readonly TokenStream _streamer;
    /// <summary>Detector used to classify operations found inside the loop body.</summary>
    private readonly OperationDetector _detector = new();

    /// <summary>
    /// Gets the loop variable name.
    /// </summary>
    public string LoopVariableName { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the start value of the range.
    /// </summary>
    public int RangeStart { get; private set; } = 0;

    /// <summary>
    /// Gets the end value of the range.
    /// </summary>
    public int RangeEnd { get; private set; } = 0;

    /// <summary>
    /// Gets whether the loop is ascending (to) or descending (downto).
    /// </summary>
    public bool IsAscending { get; private set; } = true;

    /// <summary>
    /// Gets the list of tasks to execute in the loop body.
    /// </summary>
    public List<object> Tasks { get; private set; } = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ForLoopStructure"/> class.
    /// </summary>
    /// <param name="Variables">The set of variables available in the current scope.</param>
    /// <param name="Tokenizer">The token stream to read the for loop structure from.</param>
    public ForLoopStructure(HashSet<Variable> Variables, TokenStream Tokenizer)
    {
        _variables = Variables;
        _streamer = Tokenizer;
    }

    /// <summary>
    /// Gets or sets the optional loop label for targeted exit/next statements.
    /// </summary>
    public string? LoopLabel { get; set; }

    /// <summary>
    /// Executes the for loop asynchronously.
    /// </summary>
    public async Task ExecuteAsync()
    {
        // Re-use the synth-time loop variable. Body operations captured a
        // reference to it (via the Variables HashSet passed into their
        // Operation constructors), and a fresh Variable at run time would
        // leave those references pointing at a stale instance — `diff(i)`
        // inside the body wouldn't see the iterator advance.
        // Bench Phase F bug #14 fix.
        Variable loopVar = _loopVar ?? new Variable(
            LoopVariableName, RangeStart.ToString(), Constants.DATATYPES.INTEGER);

        // Create temporary variable set with loop variable
        HashSet<Variable> loopVariables = [.. _variables, loopVar];

        if (IsAscending)
        {
            for (int i = RangeStart; i <= RangeEnd; i++)
            {
                loopVar.Value = i.ToString();
                try
                {
                    Executer executer = new(Tasks);
                    await executer.ExecuteAsync();
                }
                catch (ExitException ex)
                {
                    // Check if this exit is for us (no label or matching label)
                    if (string.IsNullOrEmpty(ex.Label) || ex.Label == LoopLabel)
                    {
                        break; // Exit the loop
                    }
                    throw; // Re-throw for outer loop
                }
                catch (NextException ex)
                {
                    // Check if this next is for us (no label or matching label)
                    if (string.IsNullOrEmpty(ex.Label) || ex.Label == LoopLabel)
                    {
                        continue; // Continue to next iteration
                    }
                    throw; // Re-throw for outer loop
                }
            }
        }
        else
        {
            for (int i = RangeStart; i >= RangeEnd; i--)
            {
                loopVar.Value = i.ToString();
                try
                {
                    Executer executer = new(Tasks);
                    await executer.ExecuteAsync();
                }
                catch (ExitException ex)
                {
                    if (string.IsNullOrEmpty(ex.Label) || ex.Label == LoopLabel)
                    {
                        break;
                    }
                    throw;
                }
                catch (NextException ex)
                {
                    if (string.IsNullOrEmpty(ex.Label) || ex.Label == LoopLabel)
                    {
                        continue;
                    }
                    throw;
                }
            }
        }
    }

    /// <summary>
    /// Synthesizes the for loop structure from the token stream.
    /// </summary>
    public void Syntetize()
    {
        _streamer.Expect(KEYWORDS); // consume 'for'

        // Get loop variable name
        LoopVariableName = _streamer.Current?.Value ?? "";
        _streamer.MoveNext(); // consume variable name

        // Expect 'in'
        if (_streamer.Current?.Value.ToLower() == "in")
        {
            _streamer.MoveNext(); // consume 'in'
        }

        // Special case: <signal>'range / <signal>'reverse_range — the
        // tokenizer emits these as a single VECTORS_ATTRIBUTES token, and
        // they expand to the signal's whole declared bit range with no
        // separate `to` / `downto` keyword. Detect and resolve here so the
        // loop body sees consistent RangeStart / RangeEnd / IsAscending.
        if (_streamer.Current?.Type == VECTORS_ATTRIBUTES &&
            (_streamer.Current.Value.EndsWith("'range",         StringComparison.OrdinalIgnoreCase) ||
             _streamer.Current.Value.EndsWith("'reverse_range", StringComparison.OrdinalIgnoreCase)))
        {
            string attrToken = _streamer.Current.Value;
            string sigName   = attrToken.Substring(0, attrToken.IndexOf('\''));
            bool reverse     = attrToken.EndsWith("'reverse_range", StringComparison.OrdinalIgnoreCase);
            _streamer.MoveNext();

            var sig = _variables.FirstOrDefault(v => v.Name.Equals(sigName, StringComparison.OrdinalIgnoreCase));
            int width = sig?.Size ?? 0;
            if (width <= 0) width = 1;

            // We don't preserve the original `to` vs `downto` direction on
            // a Variable today, so we assume the conventional downto form
            // (size-1 downto 0). 'reverse_range flips it.
            if (reverse) { RangeStart = 0; RangeEnd = width - 1; IsAscending = true;  }
            else         { RangeStart = width - 1; RangeEnd = 0; IsAscending = false; }

            if (_streamer.Current?.Value.ToLower() == "loop")
            {
                _streamer.MoveNext();
            }

            Variable loopVar2 = new(LoopVariableName, RangeStart.ToString(), Constants.DATATYPES.INTEGER);
            _loopVar = loopVar2;
            HashSet<Variable> loopVariables2 = [.. _variables, loopVar2];
            SyntetizeLoopBody(loopVariables2);
            return;
        }

        // Parse range start
        RangeStart = ParseRangeValue();

        // Parse direction (to or downto)
        if (_streamer.Current?.Value.ToLower() == "to")
        {
            IsAscending = true;
            _streamer.MoveNext();
        }
        else if (_streamer.Current?.Value.ToLower() == "downto")
        {
            IsAscending = false;
            _streamer.MoveNext();
        }

        // Parse range end
        RangeEnd = ParseRangeValue();

        // Expect 'loop'
        if (_streamer.Current?.Value.ToLower() == "loop")
        {
            _streamer.MoveNext(); // consume 'loop'
        }

        // Create loop variable for inner scope. Stash it on the structure
        // so ExecuteAsync can re-use the same instance instead of forking
        // a parallel copy that body Operations don't see. Bench bug #14.
        Variable loopVar = new(LoopVariableName, RangeStart.ToString(), Constants.DATATYPES.INTEGER);
        _loopVar = loopVar;
        HashSet<Variable> loopVariables = [.. _variables, loopVar];

        // Parse loop body
        SyntetizeLoopBody(loopVariables);
    }

    /// <summary>
    /// Parses a range value (integer literal, variable reference, or expression).
    /// v0.13.0: Added support for expressions like "TREE_DEPTH - 1".
    /// </summary>
    /// <returns>The parsed integer value, or 0 if parsing fails.</returns>
    private int ParseRangeValue()
    {
        // Collect tokens until we hit 'to', 'downto', 'loop', or 'generate'
        StringBuilder exprBuilder = new();
        while (_streamer.Current != null)
        {
            string currentVal = _streamer.Current.Value.ToLower();
            // Stop at direction/boundary keywords
            if (currentVal == "to" || currentVal == "downto" || currentVal == "loop" || currentVal == "generate")
                break;

            exprBuilder.Append(_streamer.Current.Value + " ");
            _streamer.MoveNext();
        }

        string expr = exprBuilder.ToString().Trim();
        if (string.IsNullOrEmpty(expr))
            return 0;

        // Try simple integer first
        if (int.TryParse(expr, out int simpleResult))
            return simpleResult;

        // Try single variable lookup
        if (!expr.Contains(' '))
        {
            var variable = _variables.FirstOrDefault(v => v.Name.Equals(expr, StringComparison.OrdinalIgnoreCase));
            if (variable != null && int.TryParse(variable.Value?.ToString(), out int varValue))
                return varValue;
        }

        // Try to evaluate as expression (may contain undeclared identifiers)
        try
        {
            Operation op = new(expr, _variables);
            var result = op.Execute();
            if (result is Variable v && int.TryParse(v.Value?.ToString(), out int opResult))
                return opResult;
            if (result is int intResult)
                return intResult;
            if (int.TryParse(result?.ToString(), out int parsedResult))
                return parsedResult;
        }
        catch
        {
            // Expression evaluation failed - return default
        }

        return 0;
    }

    /// <summary>
    /// Synthesizes the loop body.
    /// </summary>
    /// <param name="loopVariables">The variables in scope including the loop variable.</param>
    private void SyntetizeLoopBody(HashSet<Variable> loopVariables)
    {
        int iterations = 0;
        const int maxIterations = 5000;
        while (!IsEndOfLoop() && iterations < maxIterations)
        {
            iterations++;
            if (_streamer.Current?.Type == KEYWORDS)
            {
                var keyword = _streamer.Current.Value.ToLower();
                switch (keyword)
                {
                    case "if":
                        IfStructure ifStructure = new(loopVariables, _streamer);
                        ifStructure.Syntetize();
                        Tasks.Add(ifStructure);
                        ConsumeEndKeyword("if");
                        break;

                    case "case":
                        CaseStructure caseStructure = new(loopVariables, _streamer);
                        caseStructure.Syntetize();
                        Tasks.Add(caseStructure);
                        ConsumeEndKeyword("case");
                        break;

                    case "for":
                        ForLoopStructure nestedFor = new(loopVariables, _streamer);
                        nestedFor.Syntetize();
                        Tasks.Add(nestedFor);
                        ConsumeEndKeyword("loop");
                        break;

                    case "while":
                        WhileLoopStructure nestedWhile = new(loopVariables, _streamer);
                        nestedWhile.Syntetize();
                        Tasks.Add(nestedWhile);
                        ConsumeEndKeyword("loop");
                        break;

                    case "loop":
                        SimpleLoopStructure nestedLoop = new(loopVariables, _streamer);
                        nestedLoop.Syntetize();
                        Tasks.Add(nestedLoop);
                        ConsumeEndKeyword("loop");
                        break;

                    case "exit":
                        ExitStatement exitStmt = new(loopVariables, _streamer);
                        exitStmt.Syntetize();
                        Tasks.Add(exitStmt);
                        break;

                    case "next":
                        NextStatement nextStmt = new(loopVariables, _streamer);
                        nextStmt.Syntetize();
                        Tasks.Add(nextStmt);
                        break;

                    case "null":
                        NullStatement nullStmt = new(_streamer);
                        nullStmt.Syntetize();
                        Tasks.Add(nullStmt);
                        break;

                    case "return":
                        ReturnStatement returnStmt = new(loopVariables, _streamer);
                        returnStmt.Syntetize();
                        Tasks.Add(returnStmt);
                        break;

                    default:
                        _streamer.MoveNext();
                        break;
                }
            }
            else
            {
                SyntetizeAssignment(loopVariables);
            }
        }
    }

    /// <summary>
    /// Checks if we've reached the end of the loop.
    /// </summary>
    /// <returns>True if the current token is 'end' or the stream is exhausted.</returns>
    private bool IsEndOfLoop()
    {
        if (_streamer.Current == null) return true;
        return _streamer.Current.Value.ToLower() == "end";
    }

    /// <summary>
    /// Consumes 'end keyword ;' tokens.
    /// </summary>
    /// <param name="keyword">The keyword expected after 'end' (e.g., "if", "loop", "case").</param>
    private void ConsumeEndKeyword(string keyword)
    {
        if (_streamer.Current?.Value.ToLower() == "end")
        {
            _streamer.MoveNext();
            if (_streamer.Current?.Value.ToLower() == keyword)
            {
                _streamer.MoveNext();
            }
            if (_streamer.Current?.Value == ";")
            {
                _streamer.MoveNext();
            }
        }
    }

    /// <summary>
    /// Synthesizes a simple assignment statement.
    /// </summary>
    /// <param name="variables">The variables in scope for expression evaluation.</param>
    private void SyntetizeAssignment(HashSet<Variable> variables)
    {
        // Phase B+ Deep AST (follow-up): capture the LHS line.
        int assignLine = _streamer.Current?.Line ?? 0;

        var rawDataBuilder = new StringBuilder();
        int iterations = 0;
        const int maxIterations = 1000;
        while (_streamer.Current?.Value != ";" && !IsEndOfLoop() && iterations < maxIterations)
        {
            iterations++;
            rawDataBuilder.Append(_streamer.Current?.Value + " ");
            _streamer.MoveNext();
        }

        string operation = rawDataBuilder.ToString().Trim();
        if (string.IsNullOrEmpty(operation)) return;

        if (_streamer.Current?.Value == ";")
        {
            _streamer.MoveNext();
        }

        var type = _detector.GetOperationType(operation);
        if (type == OperationType.ASSIGNATION)
        {
            Operation op = new(operation, variables, sourceLine: assignLine);
            Tasks.Add(op);
        }
    }

    /// <summary>
    /// Not implemented - use Syntetize() instead.
    /// </summary>
    /// <param name="data">The raw data for synthesis (unused).</param>
    public void Syntetize(string[] data) => throw new NotImplementedException();
}
