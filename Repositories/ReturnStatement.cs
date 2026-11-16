using System.Text;
using Interpreter.Interfaces;
using Interpreter.Models;
using Interpreter.Services;
using Parser.Models;
using Parser.Repositories;
using static Parser.Models.Constants.TYPE;

namespace Interpreter.Repositories;

/// <summary>
/// Represents and executes a VHDL 'return' statement.
/// </summary>
/// <remarks>
/// <para><strong>Supported VHDL Syntax:</strong></para>
/// <code>
/// return;              -- Return from procedure (no value)
/// return expression;   -- Return from function with value
/// return a + b;        -- Return computed value from function
/// </code>
/// <para><strong>Execution:</strong></para>
/// <para>
/// For functions: Evaluates the return expression and throws a <see cref="ReturnException"/>
/// with the computed value, which is caught by the function executor.
/// </para>
/// <para>
/// For procedures: Throws a <see cref="ReturnException"/> with null value to exit early.
/// </para>
/// </remarks>
public class ReturnStatement : ISControl
{
    /// <summary>The variables in scope during return expression evaluation.</summary>
    private readonly HashSet<Variable> _variables;
    /// <summary>The token stream used for parsing the return statement, or null if constructed explicitly.</summary>
    private readonly TokenStream? _streamer;

    /// <summary>
    /// Gets whether this return statement has an expression (for functions).
    /// </summary>
    public bool HasExpression { get; private set; } = false;

    /// <summary>
    /// Gets the return expression string.
    /// </summary>
    public string ExpressionString { get; private set; } = string.Empty;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReturnStatement"/> class.
    /// </summary>
    /// <param name="variables">The variables in scope.</param>
    /// <param name="streamer">The token stream for parsing.</param>
    public ReturnStatement(HashSet<Variable> variables, TokenStream streamer)
    {
        _variables = variables;
        _streamer = streamer;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ReturnStatement"/> class with explicit values.
    /// </summary>
    /// <param name="variables">The variables in scope.</param>
    /// <param name="expressionString">The return expression string.</param>
    public ReturnStatement(HashSet<Variable> variables, string expressionString)
    {
        _variables = variables;
        _streamer = null;
        ExpressionString = expressionString;
        HasExpression = !string.IsNullOrEmpty(expressionString);
    }

    /// <summary>
    /// Executes the return statement asynchronously.
    /// </summary>
    /// <exception cref="ReturnException">Always thrown to return from function/procedure.</exception>
    public Task ExecuteAsync()
    {
        object? returnValue = null;

        if (HasExpression && !string.IsNullOrEmpty(ExpressionString))
        {
            // Evaluate the return expression
            Operation op = new(ExpressionString, _variables);
            var result = op.Execute();

            // Extract value from Variable if needed
            if (result is Variable varResult)
            {
                returnValue = varResult.Value;
            }
            else
            {
                returnValue = result;
            }
        }

        throw new ReturnException(returnValue);
    }

    /// <summary>
    /// Synthesizes the return statement from the token stream.
    /// </summary>
    public void Syntetize()
    {
        if (_streamer == null) return;

        _streamer.Expect(KEYWORDS); // consume 'return'

        // Check if there's an expression (not immediately followed by ';')
        if (_streamer.Current?.Value != ";")
        {
            HasExpression = true;

            var expressionBuilder = new StringBuilder();
            while (_streamer.Current?.Value != ";")
            {
                expressionBuilder.Append(_streamer.Current?.Value + " ");
                _streamer.MoveNext();
            }
            ExpressionString = expressionBuilder.ToString().Trim();
        }

        // Consume ';'
        if (_streamer.Current?.Value == ";")
        {
            _streamer.MoveNext();
        }
    }

}
