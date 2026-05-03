using System.Text;
using Interpreter.Interfaces;
using Interpreter.Models;
using Interpreter.Services;
using Parser.Models;
using Parser.Repositories;
using static Parser.Models.Constants.TYPE;

namespace Interpreter.Repositories;

/// <summary>
/// Represents a choice in a selected assignment (with...select).
/// </summary>
public class SelectionChoice
{
    /// <summary>
    /// Gets or sets the value expression for this choice.
    /// </summary>
    public string ValueExpression { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the choice values that select this branch.
    /// </summary>
    public List<string> Choices { get; set; } = new();

    /// <summary>
    /// Gets or sets whether this is the 'others' (default) branch.
    /// </summary>
    public bool IsOthers { get; set; } = false;
}

/// <summary>
/// Represents and executes a VHDL selected signal assignment (with...select).
/// </summary>
/// <remarks>
/// <para><strong>Supported VHDL Syntax:</strong></para>
/// <code>
/// with selector select
///     target &lt;= value1 when "00",
///               value2 when "01" | "10",
///               default when others;
/// </code>
/// <para><strong>Execution:</strong></para>
/// <para>
/// The selector expression is evaluated and compared against each choice.
/// When a match is found, the corresponding value is assigned to the target.
/// The 'others' clause provides a default value.
/// </para>
/// </remarks>
public class SelectedAssignment : ISControl
{
    /// <summary>The variables in scope during selector and value evaluation.</summary>
    private readonly HashSet<Variable> _variables;
    /// <summary>The token stream used for parsing the selected assignment, or null if constructed explicitly.</summary>
    private readonly TokenStream? _streamer;

    /// <summary>
    /// Gets the selector expression.
    /// </summary>
    public string SelectorExpression { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the target signal name.
    /// </summary>
    public string TargetName { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the target variable.
    /// </summary>
    public Variable? Target { get; private set; }

    /// <summary>
    /// Gets the list of selection choices.
    /// </summary>
    public List<SelectionChoice> Choices { get; private set; } = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="SelectedAssignment"/> class.
    /// </summary>
    /// <param name="variables">The variables in scope.</param>
    /// <param name="streamer">The token stream for parsing.</param>
    public SelectedAssignment(HashSet<Variable> variables, TokenStream streamer)
    {
        _variables = variables;
        _streamer = streamer;
    }

    /// <summary>
    /// Initializes a new instance with explicit values.
    /// </summary>
    /// <param name="variables">The variables in scope.</param>
    /// <param name="selectorExpression">The selector expression.</param>
    /// <param name="targetName">The target signal name.</param>
    public SelectedAssignment(HashSet<Variable> variables, string selectorExpression, string targetName)
    {
        _variables = variables;
        _streamer = null;
        SelectorExpression = selectorExpression;
        TargetName = targetName;
        Target = variables.FirstOrDefault(v => v.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Executes the selected assignment asynchronously.
    /// </summary>
    public Task ExecuteAsync()
    {
        if (Target == null || string.IsNullOrEmpty(SelectorExpression) || Choices.Count == 0)
            return Task.CompletedTask;

        // Evaluate selector
        Operation selectorOp = new(SelectorExpression, _variables);
        var selectorResult = selectorOp.Execute();
        string selectorValue = ExtractValue(selectorResult);

        // Find matching choice
        foreach (var choice in Choices)
        {
            if (choice.IsOthers)
            {
                // Default branch - use this value
                AssignValue(choice.ValueExpression);
                return Task.CompletedTask;
            }

            // Check if selector matches any of the choices
            foreach (var choiceVal in choice.Choices)
            {
                string cleanChoice = CleanValue(choiceVal);
                string cleanSelector = CleanValue(selectorValue);

                if (cleanChoice.Equals(cleanSelector, StringComparison.OrdinalIgnoreCase))
                {
                    AssignValue(choice.ValueExpression);
                    return Task.CompletedTask;
                }
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Extracts value from result.
    /// </summary>
    /// <param name="result">The result object, which may be a Variable or a raw value.</param>
    /// <returns>The string representation of the extracted value.</returns>
    private static string ExtractValue(object? result)
    {
        if (result is Variable varResult)
            return varResult.Value?.ToString() ?? "";
        return result?.ToString() ?? "";
    }

    /// <summary>
    /// Cleans a value by removing quotes.
    /// </summary>
    /// <param name="value">The value to clean.</param>
    /// <returns>The value with single and double quotes removed and whitespace trimmed.</returns>
    private static string CleanValue(string value)
    {
        return value.Replace("\"", "").Replace("'", "").Trim();
    }

    /// <summary>
    /// Assigns a value to the target signal.
    /// </summary>
    /// <param name="valueExpression">The expression to evaluate and assign.</param>
    private void AssignValue(string valueExpression)
    {
        if (Target == null)
            return;

        Operation valueOp = new(valueExpression, _variables);
        var result = valueOp.Execute();

        if (result is Variable varResult)
        {
            Target.Value = varResult.Value;
        }
        else
        {
            Target.Value = result;
        }
    }

    /// <summary>
    /// Synthesizes the selected assignment from the token stream.
    /// </summary>
    public void Syntetize()
    {
        if (_streamer == null) return;

        // Expect 'with'
        if (_streamer.Current?.Value.ToLower() == "with")
        {
            _streamer.MoveNext();
        }

        // Parse selector expression until 'select'
        var selectorBuilder = new StringBuilder();
        while (_streamer.Current != null && _streamer.Current.Value.ToLower() != "select")
        {
            selectorBuilder.Append(_streamer.Current.Value + " ");
            _streamer.MoveNext();
        }
        SelectorExpression = selectorBuilder.ToString().Trim();

        // Expect 'select'
        if (_streamer.Current?.Value.ToLower() == "select")
        {
            _streamer.MoveNext();
        }

        // Parse target
        if (_streamer.Current?.Type == IDENTIFIERS)
        {
            TargetName = _streamer.Current.Value;
            Target = _variables.FirstOrDefault(v => v.Name.Equals(TargetName, StringComparison.OrdinalIgnoreCase));
            _streamer.MoveNext();
        }

        // Skip array indexing if present
        if (_streamer.Current?.Value == "(")
        {
            SkipIndexing();
        }

        // Expect '<='
        if (_streamer.Current?.Value == "<=")
        {
            _streamer.MoveNext();
        }

        // Parse choices
        ParseChoices();
    }

    /// <summary>
    /// Skips array indexing like (7 downto 0).
    /// </summary>
    private void SkipIndexing()
    {
        int depth = 0;
        while (_streamer != null && _streamer.Current != null)
        {
            if (_streamer.Current.Value == "(")
                depth++;
            else if (_streamer.Current.Value == ")")
            {
                depth--;
                if (depth == 0)
                {
                    _streamer.MoveNext();
                    break;
                }
            }
            _streamer.MoveNext();
        }
    }

    /// <summary>
    /// Parses the selection choices.
    /// </summary>
    private void ParseChoices()
    {
        while (_streamer != null && _streamer.Current != null && _streamer.Current.Value != ";")
        {
            var choice = new SelectionChoice();

            // Parse value expression until 'when'
            var valueBuilder = new StringBuilder();
            int parenDepth = 0;

            while (_streamer.Current != null && _streamer.Current.Value != ";")
            {
                if (_streamer.Current.Value == "(")
                    parenDepth++;
                else if (_streamer.Current.Value == ")")
                    parenDepth--;

                // Stop at 'when' (but not inside parentheses)
                if (_streamer.Current.Value.ToLower() == "when" && parenDepth == 0)
                    break;

                valueBuilder.Append(_streamer.Current.Value + " ");
                _streamer.MoveNext();
            }

            choice.ValueExpression = valueBuilder.ToString().Trim();

            // Expect 'when'
            if (_streamer.Current?.Value.ToLower() == "when")
            {
                _streamer.MoveNext();
            }

            // Parse choices until ',' or ';'
            while (_streamer.Current != null && _streamer.Current.Value != "," && _streamer.Current.Value != ";")
            {
                string val = _streamer.Current.Value;

                if (val.ToLower() == "others")
                {
                    choice.IsOthers = true;
                }
                else if (val != "|")
                {
                    // Add as a choice value
                    choice.Choices.Add(val);
                }

                _streamer.MoveNext();
            }

            Choices.Add(choice);

            // Check for ','
            if (_streamer.Current?.Value == ",")
            {
                _streamer.MoveNext();
            }
        }

        // Consume ';'
        if (_streamer?.Current?.Value == ";")
        {
            _streamer.MoveNext();
        }
    }

    /// <summary>
    /// Not implemented - use Syntetize() instead.
    /// </summary>
    /// <param name="data">The raw data for synthesis (unused).</param>
    public void Syntetize(string[] data) => throw new NotImplementedException();
}
