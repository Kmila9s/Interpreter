using System.Text;
using Interpreter.Interfaces;
using Interpreter.Models;
using Interpreter.Services;
using Parser.Models;
using Parser.Repositories;
using static Parser.Models.Constants.TYPE;

namespace Interpreter.Repositories;

/// <summary>
/// Represents a branch in a conditional assignment (when...else).
/// </summary>
public class ConditionalBranch
{
    /// <summary>
    /// Gets or sets the value expression for this branch.
    /// </summary>
    public string ValueExpression { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the condition expression for this branch (null for 'else' branch).
    /// </summary>
    public string? ConditionExpression { get; set; }

    /// <summary>
    /// Gets or sets whether this is the default (else) branch.
    /// </summary>
    public bool IsDefault { get; set; } = false;
}

/// <summary>
/// Represents and executes a VHDL conditional signal assignment (when...else).
/// </summary>
/// <remarks>
/// <para><strong>Supported VHDL Syntax:</strong></para>
/// <code>
/// target &lt;= value1 when cond1 else
///           value2 when cond2 else
///           default_value;
/// </code>
/// <para><strong>Execution:</strong></para>
/// <para>
/// Conditions are evaluated in order. The first condition that evaluates to true
/// determines the assigned value. If no condition is true, the default value is used.
/// </para>
/// </remarks>
public class ConditionalAssignment : ISControl
{
    /// <summary>The variables in scope during condition and value evaluation.</summary>
    private readonly HashSet<Variable> _variables;
    /// <summary>The token stream used for parsing the conditional assignment, or null if constructed explicitly.</summary>
    private readonly TokenStream? _streamer;

    /// <summary>
    /// Gets the target signal name.
    /// </summary>
    public string TargetName { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the target variable.
    /// </summary>
    public Variable? Target { get; private set; }

    /// <summary>
    /// Gets the list of conditional branches.
    /// </summary>
    public List<ConditionalBranch> Branches { get; private set; } = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ConditionalAssignment"/> class.
    /// </summary>
    /// <param name="variables">The variables in scope.</param>
    /// <param name="streamer">The token stream for parsing.</param>
    public ConditionalAssignment(HashSet<Variable> variables, TokenStream streamer)
    {
        _variables = variables;
        _streamer = streamer;
    }

    /// <summary>
    /// Initializes a new instance with explicit target.
    /// </summary>
    /// <param name="variables">The variables in scope.</param>
    /// <param name="targetName">The target signal name.</param>
    public ConditionalAssignment(HashSet<Variable> variables, string targetName)
    {
        _variables = variables;
        _streamer = null;
        TargetName = targetName;
        Target = variables.FirstOrDefault(v => v.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Executes the conditional assignment asynchronously.
    /// </summary>
    public Task ExecuteAsync() => ExecuteAsync(scheduler: null);

    /// <summary>
    /// Scheduler-aware execution. Mirrors the bug #4 fix for
    /// <see cref="SelectedAssignment"/>: routes the target update through
    /// the supplied scheduler so signal-history sees the change and delta-
    /// cycle semantics apply. Bench Phase E bug #10 fix.
    /// </summary>
    public Task ExecuteAsync(Services.SignalScheduler? scheduler)
    {
        if (Target is null || Branches.Count == 0)
            return Task.CompletedTask;

        foreach (var branch in Branches)
        {
            if (branch.IsDefault)
            {
                AssignValue(branch.ValueExpression, scheduler);
                return Task.CompletedTask;
            }

            if (string.IsNullOrEmpty(branch.ConditionExpression))
                continue;

            Operation condOp = new(branch.ConditionExpression, _variables);
            var result = condOp.Execute();

            if (EvaluateConditionResult(result))
            {
                AssignValue(branch.ValueExpression, scheduler);
                return Task.CompletedTask;
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Assigns a value to the target signal.
    /// </summary>
    /// <param name="valueExpression">The expression to evaluate and assign.</param>
    /// <param name="scheduler">Optional signal scheduler used to queue the assignment; null for immediate.</param>
    private void AssignValue(string valueExpression, Services.SignalScheduler? scheduler = null)
    {
        if (Target is null)
            return;

        Operation valueOp = new(valueExpression, _variables);
        var result = valueOp.Execute();
        object value = (result is Variable varResult) ? varResult.Value : result!;

        if (scheduler != null)
            scheduler.Schedule(Target, value);
        else
            Target.Value = value;
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
    /// Synthesizes the conditional assignment from the token stream.
    /// </summary>
    public void Syntetize()
    {
        if (_streamer == null) return;

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

        // Parse branches
        ParseBranches();
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
    /// Parses the conditional branches.
    /// </summary>
    private void ParseBranches()
    {
        while (_streamer != null && _streamer.Current != null && _streamer.Current.Value != ";")
        {
            var branch = new ConditionalBranch();

            // Parse value expression until 'when' or ';'
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

                // Stop at 'else' (but not inside parentheses)
                if (_streamer.Current.Value.ToLower() == "else" && parenDepth == 0)
                    break;

                valueBuilder.Append(_streamer.Current.Value + " ");
                _streamer.MoveNext();
            }

            branch.ValueExpression = valueBuilder.ToString().Trim();

            // Check for 'when' condition
            if (_streamer.Current?.Value.ToLower() == "when")
            {
                _streamer.MoveNext(); // consume 'when'

                // Parse condition until 'else' or ';'
                var conditionBuilder = new StringBuilder();
                parenDepth = 0;

                while (_streamer.Current != null && _streamer.Current.Value != ";")
                {
                    if (_streamer.Current.Value == "(")
                        parenDepth++;
                    else if (_streamer.Current.Value == ")")
                        parenDepth--;

                    if (_streamer.Current.Value.ToLower() == "else" && parenDepth == 0)
                        break;

                    conditionBuilder.Append(_streamer.Current.Value + " ");
                    _streamer.MoveNext();
                }

                branch.ConditionExpression = conditionBuilder.ToString().Trim();
            }
            else
            {
                // No 'when' - this is the default branch
                branch.IsDefault = true;
            }

            Branches.Add(branch);

            // Check for 'else'
            if (_streamer.Current?.Value.ToLower() == "else")
            {
                _streamer.MoveNext(); // consume 'else'
            }
        }

        // Consume ';'
        if (_streamer?.Current?.Value == ";")
        {
            _streamer.MoveNext();
        }
    }

    /// <summary>
    /// Parses a conditional assignment from raw operation string.
    /// </summary>
    /// <param name="operation">The raw operation string like "target &lt;= val1 when cond1 else val2".</param>
    /// <param name="variables">The variables in scope.</param>
    /// <returns>A new ConditionalAssignment instance, or null if not a conditional assignment.</returns>
    public static ConditionalAssignment? TryParse(string operation, HashSet<Variable> variables)
    {
        // Check if this contains 'when' keyword (indicating conditional assignment)
        if (!operation.Contains("when", StringComparison.OrdinalIgnoreCase))
            return null;

        // Find the assignment operator
        int assignIndex = operation.IndexOf("<=");
        if (assignIndex < 0)
            return null;

        string targetPart = operation[..assignIndex].Trim();
        string valuePart = operation[(assignIndex + 2)..].Trim();

        var assignment = new ConditionalAssignment(variables, targetPart);
        if (assignment.Target is null)
        {
            // Try to create stub target
            assignment.Target = new Variable(targetPart, "0", Constants.DATATYPES.STD_LOGIC_VECTOR);
        }

        // Parse branches from value part
        ParseBranchesFromString(assignment, valuePart);

        return assignment;
    }

    /// <summary>
    /// Parses branches from a string like "val1 when cond1 else val2 when cond2 else default".
    /// </summary>
    /// <param name="assignment">The conditional assignment to populate with branches.</param>
    /// <param name="valuePart">The string containing value-when-condition-else branches.</param>
    private static void ParseBranchesFromString(ConditionalAssignment assignment, string valuePart)
    {
        // Split by 'else' first
        var parts = SplitByKeyword(valuePart, "else");

        foreach (var part in parts)
        {
            var branch = new ConditionalBranch();
            string trimmedPart = part.Trim();

            // Check for 'when' in this part
            int whenIndex = FindKeywordIndex(trimmedPart, "when");
            if (whenIndex > 0)
            {
                branch.ValueExpression = trimmedPart[..whenIndex].Trim();
                branch.ConditionExpression = trimmedPart[(whenIndex + 4)..].Trim();
            }
            else
            {
                // No 'when' - this is either a value or default
                branch.ValueExpression = trimmedPart;
                branch.IsDefault = true;
            }

            if (!string.IsNullOrEmpty(branch.ValueExpression))
            {
                assignment.Branches.Add(branch);
            }
        }
    }

    /// <summary>
    /// Splits a string by a keyword, respecting parentheses.
    /// </summary>
    /// <param name="text">The text to split.</param>
    /// <param name="keyword">The keyword to split by (e.g., "else").</param>
    /// <returns>A list of string segments separated by the keyword.</returns>
    private static List<string> SplitByKeyword(string text, string keyword)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        int parenDepth = 0;
        string lowerText = text.ToLower();
        int i = 0;

        while (i < text.Length)
        {
            if (text[i] == '(')
                parenDepth++;
            else if (text[i] == ')')
                parenDepth--;

            // Check for keyword at current position (if not in parens)
            if (parenDepth == 0 && i + keyword.Length <= text.Length)
            {
                bool isKeyword = lowerText.Substring(i, keyword.Length) == keyword;
                // Make sure it's a word boundary
                bool startBoundary = i == 0 || !char.IsLetterOrDigit(text[i - 1]);
                bool endBoundary = i + keyword.Length >= text.Length || !char.IsLetterOrDigit(text[i + keyword.Length]);

                if (isKeyword && startBoundary && endBoundary)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                    i += keyword.Length;
                    continue;
                }
            }

            current.Append(text[i]);
            i++;
        }

        parts.Add(current.ToString());
        return parts;
    }

    /// <summary>
    /// Finds keyword index respecting parentheses.
    /// </summary>
    /// <param name="text">The text to search in.</param>
    /// <param name="keyword">The keyword to search for (e.g., "when").</param>
    /// <returns>The index of the keyword in the text, or -1 if not found.</returns>
    private static int FindKeywordIndex(string text, string keyword)
    {
        string lowerText = text.ToLower();
        int parenDepth = 0;

        for (int i = 0; i <= text.Length - keyword.Length; i++)
        {
            if (text[i] == '(')
                parenDepth++;
            else if (text[i] == ')')
                parenDepth--;

            if (parenDepth == 0 && lowerText.Substring(i, keyword.Length) == keyword)
            {
                bool startBoundary = i == 0 || !char.IsLetterOrDigit(text[i - 1]);
                bool endBoundary = i + keyword.Length >= text.Length || !char.IsLetterOrDigit(text[i + keyword.Length]);

                if (startBoundary && endBoundary)
                    return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Not implemented - use Syntetize() instead.
    /// </summary>
    /// <param name="data">The raw data for synthesis (unused).</param>
    public void Syntetize(string[] data) => throw new NotImplementedException();
}
