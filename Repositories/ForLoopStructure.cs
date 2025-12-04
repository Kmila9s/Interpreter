using System.Text;
using Interpreter.Interfaces;
using Interpreter.Models;
using Interpreter.Services;
using Parser.Models;
using Parser.Repositories;
using static Parser.Models.Constants.TYPE;
using Constants = Parser.Models.Constants;

namespace Interpreter.Repositories;

/// <summary>
/// Represents and manages the logic for a VHDL 'for' loop statement.
/// Syntax: for identifier in range loop statements end loop;
/// Example: for i in 0 to 7 loop ... end loop;
/// </summary>
public class ForLoopStructure : ISControl
{
    private readonly HashSet<Variable> _variables;
    private readonly TokenStream _streamer;
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
    public ForLoopStructure(HashSet<Variable> Variables, TokenStream Tokenizer)
    {
        _variables = Variables;
        _streamer = Tokenizer;
    }

    /// <summary>
    /// Executes the for loop asynchronously.
    /// </summary>
    public async Task ExecuteAsync()
    {
        // Create loop variable
        Variable loopVar = new(LoopVariableName, RangeStart.ToString(), Constants.DATATYPES.INTEGER);

        // Create temporary variable set with loop variable
        HashSet<Variable> loopVariables = [.. _variables, loopVar];

        if (IsAscending)
        {
            for (int i = RangeStart; i <= RangeEnd; i++)
            {
                loopVar.Value = i.ToString();
                Executer executer = new(Tasks);
                await executer.ExecuteAsync();
            }
        }
        else
        {
            for (int i = RangeStart; i >= RangeEnd; i--)
            {
                loopVar.Value = i.ToString();
                Executer executer = new(Tasks);
                await executer.ExecuteAsync();
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

        // Create loop variable for inner scope
        Variable loopVar = new(LoopVariableName, RangeStart.ToString(), Constants.DATATYPES.INTEGER);
        HashSet<Variable> loopVariables = [.. _variables, loopVar];

        // Parse loop body
        SyntetizeLoopBody(loopVariables);
    }

    /// <summary>
    /// Parses a range value (integer literal or variable reference).
    /// </summary>
    private int ParseRangeValue()
    {
        string value = _streamer.Current?.Value ?? "0";
        _streamer.MoveNext();

        // Try to parse as integer
        if (int.TryParse(value, out int result))
        {
            return result;
        }

        // Try to find variable
        var variable = _variables.FirstOrDefault(v => v.Name.Equals(value, StringComparison.OrdinalIgnoreCase));
        if (variable != null && int.TryParse(variable.Value?.ToString(), out int varValue))
        {
            return varValue;
        }

        return 0;
    }

    /// <summary>
    /// Synthesizes the loop body.
    /// </summary>
    private void SyntetizeLoopBody(HashSet<Variable> loopVariables)
    {
        while (!IsEndOfLoop())
        {
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
                        // Handle exit statement
                        SyntetizeExitStatement();
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
            // Skip the condition for now
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
    private void SyntetizeAssignment(HashSet<Variable> variables)
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
            Operation op = new(operation, variables);
            Tasks.Add(op);
        }
    }

    /// <summary>
    /// Not implemented - use Syntetize() instead.
    /// </summary>
    public void Syntetize(string[] data) => throw new NotImplementedException();
}
