using System.Text;
using Interpreter.Interfaces;
using Interpreter.Models;
using Interpreter.Services;
using Parser.Models;
using Parser.Repositories;
using static Parser.Models.Constants.TYPE;

namespace Interpreter.Repositories;

/// <summary>
/// Represents and manages the logic for a VHDL simple (infinite) loop statement.
/// Parses and executes infinite loops that rely on exit statements to terminate.
/// </summary>
/// <remarks>
/// <para><strong>Supported VHDL Syntax:</strong></para>
/// <code>
/// -- Simple infinite loop with conditional exit
/// loop
///     read_data;
///     exit when buffer_full = '1';
/// end loop;
///
/// -- Loop with unconditional exit
/// loop
///     process_item;
///     if done then
///         exit;
///     end if;
/// end loop;
///
/// -- Loop with multiple exit conditions
/// loop
///     compute;
///     exit when error = '1';
///     exit when complete = '1';
/// end loop;
/// </code>
/// <para><strong>Features:</strong></para>
/// <list type="bullet">
/// <item>Infinite loop semantics (no implicit termination condition)</item>
/// <item>Exit statement support with optional 'when' condition</item>
/// <item>Multiple exit conditions tracked via ExitConditions list</item>
/// <item>Unconditional exit (just 'exit;') supported</item>
/// <item>Nested control structures (if, case, loops) within body</item>
/// <item>Maximum iteration limit (10,000) as safety fallback</item>
/// </list>
/// <para><strong>Exit Condition Handling:</strong></para>
/// <para>
/// Exit statements are parsed during synthesis and stored as ExitCondition objects.
/// During execution, after each iteration:
/// </para>
/// <list type="number">
/// <item>If an unconditional exit was encountered, loop terminates</item>
/// <item>Each conditional exit's condition is re-evaluated</item>
/// <item>If any condition is true ('1', "true", or boolean true), loop terminates</item>
/// </list>
/// <para><strong>Safety:</strong></para>
/// <para>
/// A maximum iteration count (MaxIterations = 10,000) prevents runaway loops
/// during simulation if no exit condition is met. A warning is issued if reached.
/// </para>
/// </remarks>
public class SimpleLoopStructure : ISControl
{
    private readonly HashSet<Variable> _variables;
    private readonly TokenStream _streamer;
    private readonly OperationDetector _detector = new();

    /// <summary>
    /// Gets the list of tasks to execute in the loop body.
    /// </summary>
    public List<object> Tasks { get; private set; } = new();

    /// <summary>
    /// Gets the list of exit conditions.
    /// </summary>
    public List<ExitCondition> ExitConditions { get; private set; } = new();

    /// <summary>
    /// Maximum iterations to prevent infinite loops during simulation.
    /// </summary>
    private const int MaxIterations = 10000;

    /// <summary>
    /// Initializes a new instance of the <see cref="SimpleLoopStructure"/> class.
    /// </summary>
    public SimpleLoopStructure(HashSet<Variable> Variables, TokenStream Tokenizer)
    {
        _variables = Variables;
        _streamer = Tokenizer;
    }

    /// <summary>
    /// Executes the simple loop asynchronously.
    /// </summary>
    public async Task ExecuteAsync()
    {
        int iterations = 0;
        bool shouldExit = false;

        while (iterations < MaxIterations && !shouldExit)
        {
            // Execute loop body
            Executer executer = new(Tasks);
            await executer.ExecuteAsync();

            // Check exit conditions
            foreach (var exitCondition in ExitConditions)
            {
                if (exitCondition.IsUnconditional)
                {
                    shouldExit = true;
                    break;
                }

                if (!string.IsNullOrEmpty(exitCondition.ConditionString))
                {
                    Operation conditionOp = new(exitCondition.ConditionString, _variables);
                    var result = conditionOp.Execute();

                    if (result is bool boolResult && boolResult)
                    {
                        shouldExit = true;
                        break;
                    }
                    else if (result is char charResult && charResult == '1')
                    {
                        shouldExit = true;
                        break;
                    }
                    else if (result is string strResult && (strResult == "1" || strResult.ToLower() == "true"))
                    {
                        shouldExit = true;
                        break;
                    }
                }
            }

            iterations++;
        }

        if (iterations >= MaxIterations)
        {
            Console.WriteLine($"Warning: Simple loop exceeded maximum iterations ({MaxIterations})");
        }
    }

    /// <summary>
    /// Synthesizes the simple loop structure from the token stream.
    /// </summary>
    public void Syntetize()
    {
        _streamer.Expect(KEYWORDS); // consume 'loop'

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
                        SyntetizeExitStatement();
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
    private bool IsEndOfLoop()
    {
        if (_streamer.Current == null) return true;
        return _streamer.Current.Value.ToLower() == "end";
    }

    /// <summary>
    /// Consumes 'end keyword ;' tokens.
    /// </summary>
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
    /// Synthesizes an exit statement and stores it as an ExitCondition.
    /// </summary>
    private void SyntetizeExitStatement()
    {
        _streamer.MoveNext(); // consume 'exit'

        var exitCondition = new ExitCondition();

        // Check for optional 'when' condition
        if (_streamer.Current?.Value.ToLower() == "when")
        {
            _streamer.MoveNext(); // consume 'when'

            var conditionBuilder = new StringBuilder();
            while (_streamer.Current?.Value != ";")
            {
                conditionBuilder.Append(_streamer.Current?.Value + " ");
                _streamer.MoveNext();
            }
            exitCondition.ConditionString = conditionBuilder.ToString().Trim();
            exitCondition.IsUnconditional = false;
        }
        else
        {
            exitCondition.IsUnconditional = true;
        }

        ExitConditions.Add(exitCondition);

        if (_streamer.Current?.Value == ";")
        {
            _streamer.MoveNext();
        }
    }

    /// <summary>
    /// Synthesizes a simple assignment statement.
    /// </summary>
    private void SyntetizeAssignment()
    {
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
            Operation op = new(operation, _variables);
            Tasks.Add(op);
        }
    }

    /// <summary>
    /// Not implemented - use Syntetize() instead.
    /// </summary>
    public void Syntetize(string[] data) => throw new NotImplementedException();
}

/// <summary>
/// Represents an exit condition for a loop.
/// </summary>
public class ExitCondition
{
    /// <summary>
    /// Gets or sets whether this is an unconditional exit.
    /// </summary>
    public bool IsUnconditional { get; set; } = false;

    /// <summary>
    /// Gets or sets the condition string for conditional exits.
    /// </summary>
    public string ConditionString { get; set; } = string.Empty;
}
