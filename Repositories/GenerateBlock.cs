using System.Text;
using Interpreter.Interfaces;
using Interpreter.Models;
using Interpreter.Services;
using Parser.Models;
using Parser.Repositories;
using static Parser.Models.Constants.TYPE;

namespace Interpreter.Repositories;

/// <summary>
/// Specifies the type of generate statement.
/// </summary>
public enum GenerateType
{
    /// <summary>For-generate: Iterative replication.</summary>
    ForGenerate,
    /// <summary>If-generate: Conditional inclusion.</summary>
    IfGenerate
}

/// <summary>
/// Represents and executes a VHDL generate statement.
/// </summary>
/// <remarks>
/// <para><strong>Supported VHDL Syntax:</strong></para>
/// <code>
/// -- For-generate (iterative)
/// label : for i in 0 to 7 generate
///     signal_name(i) &lt;= some_expression;
/// end generate [label];
///
/// -- If-generate (conditional)
/// label : if CONDITION generate
///     -- statements
/// end generate [label];
/// </code>
/// <para><strong>Execution:</strong></para>
/// <para>
/// For-generate: The body is executed multiple times with the loop variable
/// taking each value in the specified range.
/// </para>
/// <para>
/// If-generate: The body is executed only if the condition is true at synthesis time.
/// </para>
/// </remarks>
public class GenerateBlock : ISControl
{
    /// <summary>The variables in scope during generate block evaluation.</summary>
    private readonly HashSet<Variable> _variables;
    /// <summary>The token stream used for parsing the generate block.</summary>
    private readonly TokenStream? _streamer;
    /// <summary>Detector used to classify operations found inside the generate body.</summary>
    private readonly OperationDetector _detector = new();

    /// <summary>
    /// Gets the label of the generate block.
    /// </summary>
    public string Label { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the type of generate statement.
    /// </summary>
    public GenerateType Type { get; private set; } = GenerateType.ForGenerate;

    /// <summary>
    /// Gets the loop variable name (for for-generate).
    /// </summary>
    public string LoopVariableName { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the range start (for for-generate).
    /// </summary>
    public int RangeStart { get; private set; } = 0;

    /// <summary>
    /// Gets the range end (for for-generate).
    /// </summary>
    public int RangeEnd { get; private set; } = 0;

    /// <summary>
    /// Gets whether the range is ascending (for for-generate).
    /// </summary>
    public bool IsAscending { get; private set; } = true;

    /// <summary>
    /// Gets the condition expression (for if-generate).
    /// </summary>
    public string ConditionExpression { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the list of generated tasks (unrolled).
    /// </summary>
    public List<object> Tasks { get; private set; } = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="GenerateBlock"/> class.
    /// </summary>
    /// <param name="variables">The variables in scope.</param>
    /// <param name="streamer">The token stream for parsing.</param>
    public GenerateBlock(HashSet<Variable> variables, TokenStream streamer)
    {
        _variables = variables;
        _streamer = streamer;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="GenerateBlock"/> class.
    /// </summary>
    /// <param name="variables">The variables in scope.</param>
    /// <param name="streamer">The token stream for parsing.</param>
    /// <param name="label">The generate block label.</param>
    public GenerateBlock(HashSet<Variable> variables, TokenStream streamer, string label)
    {
        _variables = variables;
        _streamer = streamer;
        Label = label;
    }

    /// <summary>
    /// Executes the generate block asynchronously.
    /// </summary>
    public async Task ExecuteAsync()
    {
        Executer executer = new(Tasks);
        await executer.ExecuteAsync();
    }

    /// <summary>
    /// Synthesizes the generate block from the token stream.
    /// </summary>
    public void Syntetize()
    {
        if (_streamer == null) return;

        // Check the generate type (for or if)
        if (_streamer.Current?.Value.ToLower() == "for")
        {
            Type = GenerateType.ForGenerate;
            SyntetizeForGenerate();
        }
        else if (_streamer.Current?.Value.ToLower() == "if")
        {
            Type = GenerateType.IfGenerate;
            SyntetizeIfGenerate();
        }
    }

    /// <summary>
    /// Synthesizes a for-generate statement.
    /// </summary>
    private void SyntetizeForGenerate()
    {
        _streamer!.MoveNext(); // consume 'for'

        // Get loop variable name
        LoopVariableName = _streamer.Current?.Value ?? "";
        _streamer.MoveNext();

        // Expect 'in'
        if (_streamer.Current?.Value.ToLower() == "in")
        {
            _streamer.MoveNext();
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

        // Expect 'generate'
        if (_streamer.Current?.Value.ToLower() == "generate")
        {
            _streamer.MoveNext();
        }

        // Parse and unroll the body for each iteration
        UnrollForGenerate();

        // Consume 'end generate [label] ;'
        ConsumeEndGenerate();
    }

    /// <summary>
    /// Parses a range value.
    /// </summary>
    /// <returns>The parsed integer value, or 0 if parsing fails.</returns>
    private int ParseRangeValue()
    {
        StringBuilder exprBuilder = new();
        while (_streamer!.Current != null)
        {
            string currentVal = _streamer.Current.Value.ToLower();
            if (currentVal == "to" || currentVal == "downto" || currentVal == "generate")
                break;

            exprBuilder.Append(_streamer.Current.Value + " ");
            _streamer.MoveNext();
        }

        string expr = exprBuilder.ToString().Trim();
        if (string.IsNullOrEmpty(expr))
            return 0;

        if (int.TryParse(expr, out int simpleResult))
            return simpleResult;

        // Try single variable lookup
        if (!expr.Contains(' '))
        {
            var variable = _variables.FirstOrDefault(v => v.Name.Equals(expr, StringComparison.OrdinalIgnoreCase));
            if (variable is not null && int.TryParse(variable.Value?.ToString(), out int varValue))
                return varValue;
        }

        // Try to evaluate as expression
        try
        {
            Operation op = new(expr, _variables);
            var result = op.Execute();
            if (result is Variable v && int.TryParse(v.Value?.ToString(), out int opResult))
                return opResult;
            if (result is int intResult)
                return intResult;
            if (int.TryParse(result?.ToString(), out int parsedResult))
                return parsedResult;
        }
        catch { }

        return 0;
    }

    /// <summary>
    /// Unrolls the for-generate body for each iteration.
    /// </summary>
    private void UnrollForGenerate()
    {
        // Store the body tokens
        List<Token> bodyTokens = CollectBodyTokens();

        // Create tasks for each iteration
        if (IsAscending)
        {
            for (int i = RangeStart; i <= RangeEnd; i++)
            {
                UnrollIteration(bodyTokens, i);
            }
        }
        else
        {
            for (int i = RangeStart; i >= RangeEnd; i--)
            {
                UnrollIteration(bodyTokens, i);
            }
        }
    }

    /// <summary>
    /// Collects tokens until 'end generate'.
    /// </summary>
    /// <returns>A list of tokens representing the generate body (excluding the closing 'end generate').</returns>
    private List<Token> CollectBodyTokens()
    {
        var tokens = new List<Token>();
        int depth = 1;

        while (_streamer!.Current != null && depth > 0)
        {
            string val = _streamer.Current.Value.ToLower();

            if (val == "for" || val == "if")
            {
                // Check if followed by 'generate'
                _streamer.SetRollBack();
                _streamer.MoveNext();
                // Skip to check for generate
                while (_streamer.Current != null && _streamer.Current.Value.ToLower() != "generate" && _streamer.Current.Value != ";")
                {
                    _streamer.MoveNext();
                }
                if (_streamer.Current?.Value.ToLower() == "generate")
                {
                    depth++;
                }
                _streamer.RollBack();
            }
            else if (val == "end")
            {
                // Peek at next token
                var currentToken = _streamer.Current;
                _streamer.MoveNext();
                if (_streamer.Current?.Value.ToLower() == "generate")
                {
                    depth--;
                    if (depth == 0)
                    {
                        // Don't include 'end generate' in body
                        break;
                    }
                }
                // Add 'end' token to body
                tokens.Add(currentToken);
            }

            if (depth > 0)
            {
                tokens.Add(_streamer.Current!);
            }
            _streamer.MoveNext();
        }

        return tokens;
    }

    /// <summary>
    /// Unrolls a single iteration of the for-generate.
    /// </summary>
    /// <param name="bodyTokens">The tokens comprising the generate body.</param>
    /// <param name="iterationValue">The loop variable value for this iteration.</param>
    private void UnrollIteration(List<Token> bodyTokens, int iterationValue)
    {
        // Create loop variable
        Variable loopVar = new(LoopVariableName, iterationValue.ToString(), Constants.DATATYPES.INTEGER);
        HashSet<Variable> iterationVariables = [.. _variables, loopVar];

        // Create a new token stream from body tokens
        if (bodyTokens.Count == 0)
            return;

        TokenStream bodyStream = new(bodyTokens);

        // Parse the body
        int iterations = 0;
        const int maxIterations = 10000;

        while (bodyStream.Current != null && iterations < maxIterations)
        {
            iterations++;

            if (bodyStream.Current.Type == KEYWORDS)
            {
                switch (bodyStream.Current.Value.ToLower())
                {
                    case "process":
                        Process process = new(iterationVariables, bodyStream);
                        process.Syntetize();
                        Tasks.Add(process);
                        break;

                    case "if":
                        // Check if it's if-generate or regular if
                        if (LooksLikeIfGenerate(bodyStream))
                        {
                            GenerateBlock nestedGen = new(iterationVariables, bodyStream);
                            nestedGen.Syntetize();
                            Tasks.Add(nestedGen);
                        }
                        else
                        {
                            IfStructure ifStruct = new(iterationVariables, bodyStream);
                            ifStruct.Syntetize();
                            Tasks.Add(ifStruct);
                            ConsumeEndKeyword(bodyStream, "if");
                        }
                        break;

                    default:
                        bodyStream.MoveNext();
                        break;
                }
            }
            else if (bodyStream.Current.Type == IDENTIFIERS)
            {
                // Concurrent assignment
                ParseConcurrentStatement(bodyStream, iterationVariables);
            }
            else
            {
                bodyStream.MoveNext();
            }
        }
    }

    /// <summary>
    /// Checks if current position looks like an if-generate.
    /// </summary>
    /// <param name="stream">The token stream to inspect.</param>
    /// <returns>True if the tokens ahead indicate an if-generate rather than a regular if statement.</returns>
    private static bool LooksLikeIfGenerate(TokenStream stream)
    {
        // Scan ahead to see if we find 'generate' before 'then'
        stream.SetRollBack();
        stream.MoveNext(); // skip 'if'

        while (stream.Current != null)
        {
            string val = stream.Current.Value.ToLower();
            if (val == "generate")
            {
                stream.RollBack();
                return true;
            }
            if (val == "then" || val == ";")
            {
                stream.RollBack();
                return false;
            }
            stream.MoveNext();
        }

        stream.RollBack();
        return false;
    }

    /// <summary>
    /// Parses a concurrent statement.
    /// </summary>
    /// <param name="stream">The token stream to parse from.</param>
    /// <param name="variables">The variables in scope for this iteration.</param>
    private void ParseConcurrentStatement(TokenStream stream, HashSet<Variable> variables)
    {
        // Phase B+ Deep AST (follow-up): capture the LHS line before the
        // streamer walks through the statement.
        int assignLine = stream.Current?.Line ?? 0;

        var rawDataBuilder = new StringBuilder();
        while (stream.Current?.Value != ";")
        {
            rawDataBuilder.Append(stream.Current?.Value + " ");
            stream.MoveNext();
        }
        stream.MoveNext(); // consume ';'

        string operation = rawDataBuilder.ToString().Trim();
        if (string.IsNullOrEmpty(operation))
            return;

        var type = _detector.GetOperationType(operation);
        if (type == OperationType.ASSIGNATION)
        {
            // Check for conditional assignment
            if (operation.Contains("when", StringComparison.OrdinalIgnoreCase))
            {
                var condAssign = ConditionalAssignment.TryParse(operation, variables);
                if (condAssign != null)
                {
                    Tasks.Add(condAssign);
                    return;
                }
            }

            Operation op = new(operation, variables, sourceLine: assignLine);
            Tasks.Add(op);
        }
    }

    /// <summary>
    /// Consumes 'end keyword ;'.
    /// </summary>
    /// <param name="stream">The token stream to consume from.</param>
    /// <param name="keyword">The keyword expected after 'end' (e.g., "if", "loop").</param>
    private static void ConsumeEndKeyword(TokenStream stream, string keyword)
    {
        if (stream.Current?.Value.ToLower() == "end")
        {
            stream.MoveNext();
            if (stream.Current?.Value.ToLower() == keyword)
            {
                stream.MoveNext();
            }
            // VHDL-2008 matching-case: `end case?;`
            if (stream.Current?.Value == "?") stream.MoveNext();
            if (stream.Current?.Value == ";")
            {
                stream.MoveNext();
            }
        }
    }

    /// <summary>
    /// Synthesizes an if-generate statement.
    /// </summary>
    private void SyntetizeIfGenerate()
    {
        _streamer!.MoveNext(); // consume 'if'

        // Parse condition until 'generate'
        var conditionBuilder = new StringBuilder();
        while (_streamer.Current != null && _streamer.Current.Value.ToLower() != "generate")
        {
            conditionBuilder.Append(_streamer.Current.Value + " ");
            _streamer.MoveNext();
        }
        ConditionExpression = conditionBuilder.ToString().Trim();

        // Expect 'generate'
        if (_streamer.Current?.Value.ToLower() == "generate")
        {
            _streamer.MoveNext();
        }

        // Evaluate condition at synthesis time
        bool conditionTrue = EvaluateCondition();

        if (conditionTrue)
        {
            // Parse body as we would for for-generate
            List<Token> bodyTokens = CollectBodyTokens();
            if (bodyTokens.Count > 0)
            {
                TokenStream bodyStream = new(bodyTokens);
                ParseGenerateBody(bodyStream, _variables);
            }
        }
        else
        {
            // Skip body
            CollectBodyTokens();
        }

        // Consume 'end generate [label] ;'
        ConsumeEndGenerate();
    }

    /// <summary>
    /// Evaluates the if-generate condition.
    /// </summary>
    /// <returns>True if the condition evaluates to a truthy value.</returns>
    private bool EvaluateCondition()
    {
        if (string.IsNullOrEmpty(ConditionExpression))
            return false;

        try
        {
            Operation condOp = new(ConditionExpression, _variables);
            var result = condOp.Execute();

            if (result is bool boolResult)
                return boolResult;
            if (result is char charResult)
                return charResult == '1';
            if (result is string strResult)
                return strResult == "1" || strResult.Equals("true", StringComparison.OrdinalIgnoreCase);
            if (result is Variable varResult)
            {
                var val = varResult.Value;
                if (val is bool b) return b;
                if (val is char c) return c == '1';
                if (val is string s) return s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch { }

        return false;
    }

    /// <summary>
    /// Parses generate body.
    /// </summary>
    /// <param name="stream">The token stream to parse from.</param>
    /// <param name="variables">The variables in scope for this generate body.</param>
    private void ParseGenerateBody(TokenStream stream, HashSet<Variable> variables)
    {
        int iterations = 0;
        const int maxIterations = 10000;

        while (stream.Current != null && iterations < maxIterations)
        {
            iterations++;

            if (stream.Current.Type == KEYWORDS)
            {
                switch (stream.Current.Value.ToLower())
                {
                    case "process":
                        Process process = new(variables, stream);
                        process.Syntetize();
                        Tasks.Add(process);
                        break;

                    default:
                        stream.MoveNext();
                        break;
                }
            }
            else if (stream.Current.Type == IDENTIFIERS)
            {
                ParseConcurrentStatement(stream, variables);
            }
            else
            {
                stream.MoveNext();
            }
        }
    }

    /// <summary>
    /// Consumes 'end generate [label] ;'.
    /// </summary>
    private void ConsumeEndGenerate()
    {
        // Already past 'end', now consume 'generate'
        if (_streamer?.Current?.Value.ToLower() == "generate")
        {
            _streamer.MoveNext();
        }
        // Optional label
        if (_streamer?.Current?.Type == IDENTIFIERS)
        {
            _streamer.MoveNext();
        }
        // Semicolon
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
