using System.Text;
using Interpreter.Interfaces;
using Interpreter.Models;
using Interpreter.Services;
using Parser.Models;
using Parser.Repositories;
using static Parser.Models.Constants.TYPE;

namespace Interpreter.Repositories;

// Note: This file uses ExitException and NextException from ControlExceptions.cs

/// <summary>
/// Represents and manages the logic for a VHDL 'while' loop statement.
/// Parses and executes while loops with condition-based iteration.
/// </summary>
/// <remarks>
/// <para><strong>Supported VHDL Syntax:</strong></para>
/// <code>
/// -- Simple while loop
/// while count &lt; 10 loop
///     count := count + 1;
/// end loop;
///
/// -- While loop with complex condition
/// while (enable = '1') and (error = '0') loop
///     process_data;
/// end loop;
///
/// -- While loop with exit
/// while true loop
///     statements;
///     exit when done = '1';
/// end loop;
/// </code>
/// <para><strong>Features:</strong></para>
/// <list type="bullet">
/// <item>Condition evaluation before each iteration</item>
/// <item>Support for boolean, std_logic ('1'/'0'), and string conditions</item>
/// <item>Nested control structures (if, case, loops) within body</item>
/// <item>Exit statement support with optional 'when' condition</item>
/// <item>Maximum iteration limit (10,000) to prevent infinite loops</item>
/// </list>
/// <para><strong>Condition Evaluation:</strong></para>
/// <para>
/// The condition is re-evaluated before each iteration using a fresh Operation instance.
/// This ensures variable changes in the loop body affect the condition.
/// Supported condition result types:
/// </para>
/// <list type="bullet">
/// <item><c>bool</c> - Direct boolean evaluation</item>
/// <item><c>char</c> - '1' is true, anything else is false</item>
/// <item><c>string</c> - "1" or "true" (case-insensitive) is true</item>
/// </list>
/// <para><strong>Safety:</strong></para>
/// <para>
/// A maximum iteration count (MaxIterations = 10,000) prevents runaway loops
/// during simulation. A warning is issued if this limit is reached.
/// </para>
/// </remarks>
public class WhileLoopStructure : ISControl
{
    /// <summary>The variables in scope during condition and loop body evaluation.</summary>
    private readonly HashSet<Variable> _variables;
    /// <summary>The token stream used for parsing the while loop structure.</summary>
    private readonly TokenStream _streamer;
    /// <summary>Detector used to classify operations found inside the loop body.</summary>
    private readonly OperationDetector _detector = new();

    /// <summary>
    /// Gets the condition operation for the while loop.
    /// </summary>
    public Operation? Condition { get; private set; }

    /// <summary>
    /// Gets the raw condition string.
    /// </summary>
    public string ConditionString { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the list of tasks to execute in the loop body.
    /// </summary>
    public List<object> Tasks { get; private set; } = new();

    /// <summary>
    /// Gets or sets the optional loop label for targeted exit/next statements.
    /// </summary>
    public string? LoopLabel { get; set; }

    /// <summary>
    /// Maximum iterations to prevent infinite loops during simulation.
    /// </summary>
    private const int MaxIterations = 10000;

    /// <summary>
    /// Initializes a new instance of the <see cref="WhileLoopStructure"/> class.
    /// </summary>
    /// <param name="Variables">The set of variables available in the current scope.</param>
    /// <param name="Tokenizer">The token stream to read the while loop structure from.</param>
    public WhileLoopStructure(HashSet<Variable> Variables, TokenStream Tokenizer)
    {
        _variables = Variables;
        _streamer = Tokenizer;
    }

    /// <summary>
    /// Executes the while loop asynchronously.
    /// </summary>
    public async Task ExecuteAsync()
    {
        if (Condition == null) return;

        int iterations = 0;
        bool shouldExit = false;

        while (iterations < MaxIterations && !shouldExit)
        {
            // Re-evaluate condition each iteration
            Operation conditionOp = new(ConditionString, _variables);
            var result = conditionOp.Execute();

            // Check if condition is still true
            if (result is bool boolResult && !boolResult)
            {
                break;
            }
            else if (result is char charResult && charResult != '1')
            {
                break;
            }
            else if (result is string strResult && strResult != "1" && strResult.ToLower() != "true")
            {
                break;
            }

            // Execute loop body
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
                    shouldExit = true;
                    break;
                }
                throw; // Re-throw for outer loop
            }
            catch (NextException ex)
            {
                // Check if this next is for us (no label or matching label)
                if (string.IsNullOrEmpty(ex.Label) || ex.Label == LoopLabel)
                {
                    iterations++;
                    continue; // Continue to next iteration
                }
                throw; // Re-throw for outer loop
            }

            iterations++;
        }

        if (iterations >= MaxIterations)
        {
            Console.WriteLine($"Warning: While loop exceeded maximum iterations ({MaxIterations})");
        }
    }

    /// <summary>
    /// Synthesizes the while loop structure from the token stream.
    /// </summary>
    public void Syntetize()
    {
        _streamer.Expect(KEYWORDS); // consume 'while'

        // Parse condition until 'loop'
        var conditionBuilder = new StringBuilder();
        while (_streamer.Current?.Value.ToLower() != "loop")
        {
            conditionBuilder.Append(_streamer.Current?.Value + " ");
            _streamer.MoveNext();
        }

        ConditionString = conditionBuilder.ToString().Trim();
        Condition = new Operation(ConditionString, _variables);

        // Consume 'loop'
        if (_streamer.Current?.Value.ToLower() == "loop")
        {
            _streamer.MoveNext();
        }

        // Parse loop body
        SyntetizeLoopBody();
    }

    /// <summary>
    /// Synthesizes the loop body.
    /// </summary>
    private void SyntetizeLoopBody()
    {
        while (!IsEndOfLoop())
        {
            if (_streamer.Current?.Type == KEYWORDS)
            {
                var keyword = _streamer.Current.Value.ToLower();
                switch (keyword)
                {
                    case "if":
                        IfStructure ifStructure = new(_variables, _streamer);
                        ifStructure.Syntetize();
                        Tasks.Add(ifStructure);
                        ConsumeEndKeyword("if");
                        break;

                    case "case":
                        CaseStructure caseStructure = new(_variables, _streamer);
                        caseStructure.Syntetize();
                        Tasks.Add(caseStructure);
                        ConsumeEndKeyword("case");
                        break;

                    case "for":
                        ForLoopStructure nestedFor = new(_variables, _streamer);
                        nestedFor.Syntetize();
                        Tasks.Add(nestedFor);
                        ConsumeEndKeyword("loop");
                        break;

                    case "while":
                        WhileLoopStructure nestedWhile = new(_variables, _streamer);
                        nestedWhile.Syntetize();
                        Tasks.Add(nestedWhile);
                        ConsumeEndKeyword("loop");
                        break;

                    case "loop":
                        SimpleLoopStructure nestedLoop = new(_variables, _streamer);
                        nestedLoop.Syntetize();
                        Tasks.Add(nestedLoop);
                        ConsumeEndKeyword("loop");
                        break;

                    case "exit":
                        ExitStatement exitStmt = new(_variables, _streamer);
                        exitStmt.Syntetize();
                        Tasks.Add(exitStmt);
                        break;

                    case "next":
                        NextStatement nextStmt = new(_variables, _streamer);
                        nextStmt.Syntetize();
                        Tasks.Add(nextStmt);
                        break;

                    case "null":
                        NullStatement nullStmt = new(_streamer);
                        nullStmt.Syntetize();
                        Tasks.Add(nullStmt);
                        break;

                    case "return":
                        ReturnStatement returnStmt = new(_variables, _streamer);
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
                SyntetizeAssignment();
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
    private void SyntetizeAssignment()
    {
        // Phase B+ Deep AST (follow-up): capture the LHS line.
        int assignLine = _streamer.Current?.Line ?? 0;

        var rawDataBuilder = new StringBuilder();
        while (_streamer.Current?.Value != ";" && !IsEndOfLoop())
        {
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
            Operation op = new(operation, _variables, sourceLine: assignLine);
            Tasks.Add(op);
        }
    }

    /// <summary>
    /// Not implemented - use Syntetize() instead.
    /// </summary>
    /// <param name="data">The raw data for synthesis (unused).</param>
    public void Syntetize(string[] data) => throw new NotImplementedException();
}
