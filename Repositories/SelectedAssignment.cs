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
    /// <summary>The target and each choice's assignment.</summary>
    private readonly BranchTarget<SelectionChoice> _target;
    /// <summary>The selector, parsed on first use and re-evaluated against live values.</summary>
    private Operation? _selector;

    /// <summary>
    /// Gets the selector expression.
    /// </summary>
    public string SelectorExpression { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the target signal name.
    /// </summary>
    public string TargetName => Target?.Name ?? _target.Text;

    /// <summary>
    /// Gets the target variable.
    /// </summary>
    public Variable? Target => _target.Destination?.Base;

    /// <summary>The written target resolved (whole, or the bit/range written); null when undeclared.</summary>
    public AssignmentTarget? Destination => _target.Destination;

    /// <summary>True for an aggregate target <c>(a, b) &lt;= …</c> (G55).</summary>
    public bool HasAggregateTarget => _target.IsAggregate;

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
        _target = new BranchTarget<SelectionChoice>(variables);
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
        _target = new BranchTarget<SelectionChoice>(variables);
        _target.Set(targetName);
    }

    /// <summary>
    /// Executes the selected assignment asynchronously.
    /// </summary>
    public Task ExecuteAsync() => ExecuteAsync(scheduler: null);

    /// <summary>
    /// Scheduler-aware execution: when a <paramref name="scheduler"/> is
    /// supplied, the target update is routed through it (proper VHDL
    /// delta-cycle semantics + SignalHistory recording). Otherwise it
    /// falls back to immediate assignment, matching the legacy behavior
    /// kept for non-engine callers. Bench Phase E bug #4 fix.
    /// </summary>
    public Task ExecuteAsync(Services.SignalScheduler? scheduler)
    {
        if (!_target.IsDeclared || string.IsNullOrEmpty(SelectorExpression) || Choices.Count == 0)
            return Task.CompletedTask;

        _selector ??= new Operation(SelectorExpression, _variables);
        string selectorValue = ExtractValue(_selector.Evaluate());

        SelectionChoice? matched = null;
        SelectionChoice? defaultChoice = null;

        // 2026-07-05 (STATUS §1): compare via numeric-value normalisation
        // when both sides parse as integers. Previously a vector selector
        // ("0001") never matched a decimal alternative ("1") because the
        // clean-value string compare hit "0001" != "1". Now the check
        // first normalises both sides to integers via BinaryOrDecimalToInt
        // and falls back to string equality only when either side isn't
        // numeric. Also handles bit-string chars ('0', '1').
        foreach (var choice in Choices)
        {
            if (choice.IsOthers) { defaultChoice = choice; continue; }
            foreach (var choiceVal in choice.Choices)
            {
                if (ChoicesMatch(choiceVal, selectorValue))
                {
                    matched = choice;
                    break;
                }
            }
            if (matched != null) break;
        }

        var chosen = matched ?? defaultChoice;
        if (chosen != null) _target.Assign(chosen, chosen.ValueExpression, scheduler);

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
    /// Numeric-first alternative match for with…select. Tries both sides as
    /// integers (accepting bit-string, decimal, or hex); falls back to case-
    /// insensitive string equality when either side isn't numeric. Closes
    /// STATUS §1: `when 0|1|7` vs. an `std_logic_vector` selector.
    /// </summary>
    private static bool ChoicesMatch(string choiceVal, string selectorValue)
    {
        var a = CleanValue(choiceVal);
        var b = CleanValue(selectorValue);
        if (HdlLiteral.TryParseInt(a, out long ai) && HdlLiteral.TryParseInt(b, out long bi))
            return ai == bi;
        return a.Equals(b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Synthesizes the selected assignment from the token stream.
    /// </summary>
    public void Syntetize()
    {
        if (_streamer == null) return;

        // Capture the statement's source line up-front (before tokens are
        // consumed) for the gated validation pass at the end of this method.
        int sourceLine = _streamer.Current?.SourceLine ?? 0;

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

        _target.Read(_streamer);

        // Expect '<='
        if (_streamer.Current?.Value == "<=")
        {
            _streamer.MoveNext();
        }

        // Parse choices
        ParseChoices();

        // v1.22.0 (item 3): extend the input-port (C8) and type/width (C4/C5)
        // checks — which until now only reached plain concurrent + process
        // assignments — to this selected (with…select) form.
        foreach (var choice in Choices) _target.Validate(choice, choice.ValueExpression, sourceLine);
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
            bool sawOthers = false;
            while (_streamer.Current != null && _streamer.Current.Value != "," && _streamer.Current.Value != ";")
            {
                string val = _streamer.Current.Value;

                // C11 (v1.23.0): a choice list is locally static — it never contains
                // an assignment operator. A '<=' / ':=' here means the previous
                // choices ran past a missing ';' into the next statement.
                if (val == "<=" || val == ":=")
                    throw new ValidationException(
                        $"';' expected to terminate the selected assignment to '{TargetName}' (found '{val}' — missing ';'?)",
                        _streamer.Current);

                if (val.ToLower() == "others")
                {
                    choice.IsOthers = true;
                    sawOthers = true;
                }
                else if (val != "|")
                {
                    // C11 (v1.23.0): nothing may follow an `others` choice except ';';
                    // more content here means a missing ';'.
                    if (sawOthers)
                        throw new ValidationException(
                            $"';' expected to terminate the selected assignment to '{TargetName}' (found '{val}' — missing ';'?)",
                            _streamer.Current);

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

}
