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
    /// <paramref name="tasks"/> with every (nested) generate replaced by its statements: a
    /// generate is elaboration-time structure, its unrolled statements are ordinary
    /// concurrent statements. Returns <paramref name="tasks"/> itself when it has none.
    /// </summary>
    public static List<object> Flatten(List<object> tasks)
    {
        if (!tasks.Any(t => t is GenerateBlock)) return tasks;
        var flat = new List<object>();
        foreach (var task in tasks)
        {
            if (task is GenerateBlock generate) flat.AddRange(Flatten(generate.Tasks));
            else flat.Add(task);
        }
        return flat;
    }

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
            DispatchBodyStatement(bodyStream, iterationVariables);
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
    /// Detects a labeled statement (<c>identifier : …</c>) at the current position
    /// without consuming it, returning the (lower-cased) keyword that follows the
    /// colon — e.g. <c>process</c>, <c>if</c>, <c>for</c> — or "" for a labeled
    /// assignment/instantiation.
    /// </summary>
    private static bool IsLabeledStatement(TokenStream stream, out string keyword)
    {
        keyword = string.Empty;
        if (stream.Current?.Type != IDENTIFIERS) return false;

        stream.SetRollBack();
        stream.MoveNext(); // past the label
        bool isLabel = stream.Current?.Value == ":";
        if (isLabel)
        {
            stream.MoveNext(); // past ':'
            keyword = stream.Current?.Value.ToLower() ?? string.Empty;
        }
        stream.RollBack();
        return isLabel;
    }

    /// <summary>
    /// After a label: <c>entity …</c>, <c>component …</c>, <c>configuration …</c> or
    /// <c>name generic|port map …</c> — a component instantiation.
    /// </summary>
    private static bool IsInstantiation(TokenStream stream) =>
        EndClause.Is(stream.Current, "entity") || EndClause.Is(stream.Current, "component")
        || EndClause.Is(stream.Current, "configuration")
        || (stream.Current?.Type == IDENTIFIERS && (EndClause.Is(stream.Peek(), "port") || EndClause.Is(stream.Peek(), "generic")));

    /// <summary>
    /// A concurrent signal assignment or procedure call of a generate body, with an
    /// optional label — read by the same <see cref="SimpleStatement"/> as the
    /// architecture body.
    /// </summary>
    private void ParseConcurrentStatement(TokenStream stream, HashSet<Variable> variables)
    {
        string? label = null;
        if (stream.Current is { Type: IDENTIFIERS } head && stream.Peek()?.Value == ":")
        {
            label = head.Value;
            stream.MoveNext();
            stream.MoveNext();
        }
        if (label is not null && IsInstantiation(stream))
        {
            // A component instance in a generate body is flattened by SourceElaborator
            // when it can be; one it left here is not modelled yet (G57).
            while (stream.Current is not null && stream.Current.Value != ";") stream.MoveNext();
            stream.MoveNext();
            return;
        }
        if (ConcurrentStatements.IsProcedureCall(stream))
        {
            Tasks.Add(ConcurrentStatements.ProcedureCall(stream, variables, label));
            return;
        }
        var st = SimpleStatement.Read(stream, StatementRegion.Concurrent);
        Tasks.Add(ConcurrentStatements.AssignmentTask(st.Text, variables, st.First.SourceLine));
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
            return Truth.Of(condOp.Execute());
        }
        catch (Exception e) when (e is not SimulationRuntimeException) { }

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
            DispatchBodyStatement(stream, variables);
        }
    }

    /// <summary>
    /// Dispatches one statement of a generate body (shared by the for-generate
    /// unroller and the if-generate body parser). Routes nested processes, nested
    /// generates and if-statements — including the common LABELED forms
    /// (<c>label : process</c>, <c>label : if…generate</c>, <c>label : for…generate</c>)
    /// — to their proper handlers, so they are not mis-read as concurrent
    /// assignments. Everything else is parsed as a concurrent (possibly labeled)
    /// assignment, where the C11/C8/C4/C5 validation runs.
    /// </summary>
    private void DispatchBodyStatement(TokenStream stream, HashSet<Variable> variables)
    {
        if (stream.Current == null) return;

        if (stream.Current.Type == KEYWORDS)
        {
            switch (stream.Current.Value.ToLower())
            {
                case "process":
                {
                    Process process = new(variables, stream);
                    process.Syntetize();
                    Tasks.Add(process);
                    break;
                }
                case "if":
                    DispatchIfOrIfGenerate(stream, variables);
                    break;
                case "for":
                {
                    GenerateBlock forGen = new(variables, stream);
                    forGen.Syntetize();
                    Tasks.Add(forGen);
                    break;
                }
                case "with":
                {
                    SelectedAssignment selAssign = new(variables, stream);
                    selAssign.Syntetize();
                    Tasks.Add(selAssign);
                    break;
                }
                case "assert" or "postponed":
                    Tasks.Add(ConcurrentStatements.ProcessOrAssert(stream, variables, null));
                    break;
                case "begin":
                    stream.MoveNext(); // end of the generate's declarative part
                    break;
                case "signal" or "constant" or "variable" or "type" or "subtype" or "attribute" or "alias" or "component":
                    // Generate-local declarations are not modelled yet (G57): skip the
                    // declaration as a whole instead of reading its pieces as statements.
                    while (stream.Current is not null && stream.Current.Value != ";") stream.MoveNext();
                    stream.MoveNext();
                    break;
                default:
                    throw new ParserException(
                        $"unexpected token '{stream.Current.Value}' in a concurrent statement list", stream.Current);
            }
        }
        else if (stream.Current.Type == IDENTIFIERS)
        {
            // A LABELED nested statement (`label : process | if…generate |
            // for…generate`) starts with an identifier; route it to the proper
            // handler so it is not mis-read as a concurrent assignment. VHDL requires
            // labels on generate statements, so nested generates always land here.
            // Note: `name : type [:= init]` declarations and `label : y <= …`
            // assignments are NOT special-cased — they fall through to
            // ParseConcurrentStatement, whose leading-':' guard handles them.
            if (IsLabeledStatement(stream, out string stmtKeyword)
                && (stmtKeyword is "process" or "if" or "for" or "with" or "assert" or "postponed"))
            {
                string stmtLabel = stream.Current!.Value;
                stream.MoveNext(); // consume label
                stream.MoveNext(); // consume ':'
                switch (stmtKeyword)
                {
                    case "process":
                    {
                        Process labeledProc = new(variables, stream) { Label = stmtLabel };
                        labeledProc.Syntetize();
                        Tasks.Add(labeledProc);
                        break;
                    }
                    case "for":
                    {
                        GenerateBlock labeledFor = new(variables, stream);
                        labeledFor.Syntetize();
                        Tasks.Add(labeledFor);
                        break;
                    }
                    case "if":
                        DispatchIfOrIfGenerate(stream, variables);
                        break;
                    case "with":
                    {
                        SelectedAssignment labeledSel = new(variables, stream);
                        labeledSel.Syntetize();
                        Tasks.Add(labeledSel);
                        break;
                    }
                    case "assert" or "postponed":
                        Tasks.Add(ConcurrentStatements.ProcessOrAssert(stream, variables, stmtLabel));
                        break;
                }
            }
            else
            {
                ParseConcurrentStatement(stream, variables);
            }
        }
        else
        {
            ParseConcurrentStatement(stream, variables);
        }
    }

    /// <summary>
    /// Dispatches an <c>if</c> at the current position as either a nested
    /// if-generate or a regular if-statement.
    /// </summary>
    private void DispatchIfOrIfGenerate(TokenStream stream, HashSet<Variable> variables)
    {
        if (LooksLikeIfGenerate(stream))
        {
            GenerateBlock nestedGen = new(variables, stream);
            nestedGen.Syntetize();
            Tasks.Add(nestedGen);
        }
        else
        {
            IfStructure ifStruct = new(variables, stream);
            ifStruct.Syntetize();
            Tasks.Add(ifStruct);
            EndClause.Consume(stream, "if");
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

}
