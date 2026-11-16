using System.Text;
using Interpreter.Interfaces;
using Interpreter.Models;
using Interpreter.Services;
using Parser.Models;
using Parser.Repositories;
using static Parser.Models.Constants.TYPE;

namespace Interpreter.Repositories;

/// <summary>
/// Represents and executes a VHDL 'exit' statement.
/// </summary>
/// <remarks>
/// <para><strong>Supported VHDL Syntax:</strong></para>
/// <code>
/// exit;                       -- Exit innermost loop unconditionally
/// exit loop_label;            -- Exit named loop unconditionally
/// exit when condition;        -- Exit innermost loop when condition is true
/// exit loop_label when cond;  -- Exit named loop when condition is true
/// </code>
/// <para><strong>Execution:</strong></para>
/// <para>
/// When executed, evaluates the optional condition (if present). If the condition
/// is true (or if unconditional), throws an <see cref="ExitException"/> which is
/// caught by the enclosing loop structure.
/// </para>
/// </remarks>
public class ExitStatement : ISControl
{
    /// <summary>The variables in scope during exit evaluation.</summary>
    private readonly HashSet<Variable> _variables;
    /// <summary>The token stream used for parsing the exit statement, or null if constructed explicitly.</summary>
    private readonly TokenStream? _streamer;

    /// <summary>
    /// Gets the optional label of the loop to exit.
    /// </summary>
    public string? Label { get; private set; }

    /// <summary>
    /// Gets whether this is an unconditional exit.
    /// </summary>
    public bool IsUnconditional { get; private set; } = true;

    /// <summary>
    /// Gets the condition string for conditional exits.
    /// </summary>
    public string ConditionString { get; private set; } = string.Empty;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExitStatement"/> class.
    /// </summary>
    /// <param name="variables">The variables in scope.</param>
    /// <param name="streamer">The token stream for parsing.</param>
    public ExitStatement(HashSet<Variable> variables, TokenStream streamer)
    {
        _variables = variables;
        _streamer = streamer;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ExitStatement"/> class with explicit values.
    /// </summary>
    /// <param name="variables">The variables in scope.</param>
    /// <param name="label">Optional loop label.</param>
    /// <param name="conditionString">Optional condition string.</param>
    public ExitStatement(HashSet<Variable> variables, string? label, string conditionString)
    {
        _variables = variables;
        _streamer = null;
        Label = label;
        ConditionString = conditionString;
        IsUnconditional = string.IsNullOrEmpty(conditionString);
    }

    /// <summary>
    /// Executes the exit statement asynchronously.
    /// </summary>
    /// <exception cref="ExitException">Thrown when the exit condition is met or unconditional.</exception>
    public Task ExecuteAsync()
    {
        if (IsUnconditional)
        {
            throw new ExitException(Label);
        }

        // Evaluate condition
        Operation conditionOp = new(ConditionString, _variables);
        var result = conditionOp.Execute();

        bool shouldExit = EvaluateConditionResult(result);
        if (shouldExit)
        {
            throw new ExitException(Label);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Evaluates a condition result to determine if it represents true.
    /// </summary>
    /// <param name="result">The result of a condition evaluation (bool, char, string, or Variable).</param>
    /// <returns>True if the result represents a true/positive condition.</returns>
    private static bool EvaluateConditionResult(object? result)
    {
        if (result is bool boolResult)
            return boolResult;
        if (result is char charResult)
            return charResult == '1';
        if (result is string strResult)
            return strResult == "1" || strResult.Equals("true", StringComparison.OrdinalIgnoreCase);
        if (result is Variable varResult)
            return EvaluateConditionResult(varResult.Value);
        return false;
    }

    /// <summary>
    /// Synthesizes the exit statement from the token stream.
    /// </summary>
    public void Syntetize()
    {
        if (_streamer == null) return;

        _streamer.Expect(KEYWORDS); // consume 'exit'

        // Check for optional label (identifier before 'when' or ';')
        if (_streamer.Current?.Type == IDENTIFIERS)
        {
            // Could be a label - peek ahead
            string potentialLabel = _streamer.Current.Value;
            _streamer.MoveNext();

            // If next is 'when' or ';', then it was a label
            if (_streamer.Current?.Value.ToLower() == "when" || _streamer.Current?.Value == ";")
            {
                Label = potentialLabel;
            }
            else
            {
                // Not a label, this shouldn't happen in valid VHDL
                // Put it back (we'll handle this in condition parsing)
            }
        }

        // Check for optional 'when' condition
        if (_streamer.Current?.Value.ToLower() == "when")
        {
            _streamer.MoveNext(); // consume 'when'
            IsUnconditional = false;

            var conditionBuilder = new StringBuilder();
            while (_streamer.Current?.Value != ";")
            {
                conditionBuilder.Append(_streamer.Current?.Value + " ");
                _streamer.MoveNext();
            }
            ConditionString = conditionBuilder.ToString().Trim();
        }

        // Consume ';'
        if (_streamer.Current?.Value == ";")
        {
            _streamer.MoveNext();
        }
    }

}
