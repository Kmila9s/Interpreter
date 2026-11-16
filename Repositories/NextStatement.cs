using System.Text;
using Interpreter.Interfaces;
using Interpreter.Models;
using Interpreter.Services;
using Parser.Models;
using Parser.Repositories;
using static Parser.Models.Constants.TYPE;

namespace Interpreter.Repositories;

/// <summary>
/// Represents and executes a VHDL 'next' statement.
/// </summary>
/// <remarks>
/// <para><strong>Supported VHDL Syntax:</strong></para>
/// <code>
/// next;                       -- Skip to next iteration of innermost loop
/// next loop_label;            -- Skip to next iteration of named loop
/// next when condition;        -- Skip when condition is true
/// next loop_label when cond;  -- Skip named loop when condition is true
/// </code>
/// <para><strong>Execution:</strong></para>
/// <para>
/// When executed, evaluates the optional condition (if present). If the condition
/// is true (or if unconditional), throws a <see cref="NextException"/> which is
/// caught by the enclosing loop structure to skip to the next iteration.
/// </para>
/// </remarks>
public class NextStatement : ISControl
{
    /// <summary>The variables in scope during next evaluation.</summary>
    private readonly HashSet<Variable> _variables;
    /// <summary>The token stream used for parsing the next statement, or null if constructed explicitly.</summary>
    private readonly TokenStream? _streamer;

    /// <summary>
    /// Gets the optional label of the loop to continue.
    /// </summary>
    public string? Label { get; private set; }

    /// <summary>
    /// Gets whether this is an unconditional next.
    /// </summary>
    public bool IsUnconditional { get; private set; } = true;

    /// <summary>
    /// Gets the condition string for conditional next statements.
    /// </summary>
    public string ConditionString { get; private set; } = string.Empty;

    /// <summary>
    /// Initializes a new instance of the <see cref="NextStatement"/> class.
    /// </summary>
    /// <param name="variables">The variables in scope.</param>
    /// <param name="streamer">The token stream for parsing.</param>
    public NextStatement(HashSet<Variable> variables, TokenStream streamer)
    {
        _variables = variables;
        _streamer = streamer;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="NextStatement"/> class with explicit values.
    /// </summary>
    /// <param name="variables">The variables in scope.</param>
    /// <param name="label">Optional loop label.</param>
    /// <param name="conditionString">Optional condition string.</param>
    public NextStatement(HashSet<Variable> variables, string? label, string conditionString)
    {
        _variables = variables;
        _streamer = null;
        Label = label;
        ConditionString = conditionString;
        IsUnconditional = string.IsNullOrEmpty(conditionString);
    }

    /// <summary>
    /// Executes the next statement asynchronously.
    /// </summary>
    /// <exception cref="NextException">Thrown when the next condition is met or unconditional.</exception>
    public Task ExecuteAsync()
    {
        if (IsUnconditional)
        {
            throw new NextException(Label);
        }

        // Evaluate condition
        Operation conditionOp = new(ConditionString, _variables);
        var result = conditionOp.Execute();

        bool shouldNext = EvaluateConditionResult(result);
        if (shouldNext)
        {
            throw new NextException(Label);
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
    /// Synthesizes the next statement from the token stream.
    /// </summary>
    public void Syntetize()
    {
        if (_streamer == null) return;

        _streamer.Expect(KEYWORDS); // consume 'next'

        // Check for optional label (identifier before 'when' or ';')
        if (_streamer.Current?.Type == IDENTIFIERS)
        {
            string potentialLabel = _streamer.Current.Value;
            _streamer.MoveNext();

            // If next is 'when' or ';', then it was a label
            if (_streamer.Current?.Value.ToLower() == "when" || _streamer.Current?.Value == ";")
            {
                Label = potentialLabel;
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
