using System.Text;
using Interpreter.Interfaces;
using Interpreter.Models;
using Interpreter.Services;
using Parser.Models;
using Parser.Repositories;
using static Parser.Models.Constants.TYPE;

namespace Interpreter.Repositories;

/// <summary>
/// Represents and manages the logic for a VHDL 'while' loop statement.
/// Syntax: while condition loop statements end loop;
/// Example: while count < 10 loop ... end loop;
/// </summary>
public class WhileLoopStructure : ISControl
{
    private readonly HashSet<Variable> _variables;
    private readonly TokenStream _streamer;
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
    /// Maximum iterations to prevent infinite loops during simulation.
    /// </summary>
    private const int MaxIterations = 10000;

    /// <summary>
    /// Initializes a new instance of the <see cref="WhileLoopStructure"/> class.
    /// </summary>
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
        while (iterations < MaxIterations)
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
            Executer executer = new(Tasks);
            await executer.ExecuteAsync();

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
    /// Synthesizes an exit statement.
    /// </summary>
    private void SyntetizeExitStatement()
    {
        _streamer.MoveNext(); // consume 'exit'

        // Check for optional 'when' condition
        if (_streamer.Current?.Value.ToLower() == "when")
        {
            _streamer.MoveNext(); // consume 'when'
            while (_streamer.Current?.Value != ";")
            {
                _streamer.MoveNext();
            }
        }

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
