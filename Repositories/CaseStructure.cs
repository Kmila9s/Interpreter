using System.Text;
using Interpreter.Interfaces;
using Interpreter.Models;
using Interpreter.Services;
using Parser.Models;
using Parser.Repositories;
using static Parser.Models.Constants.TYPE;

namespace Interpreter.Repositories;

/// <summary>
/// Represents a single 'when' branch of a case statement.
/// </summary>
public class CaseWhenBranch
{
    /// <summary>
    /// Gets or sets the choice values for this branch (e.g., "00", "01", or "others").
    /// </summary>
    public List<string> Choices { get; set; } = new();

    /// <summary>
    /// Gets or sets whether this is the 'others' branch.
    /// </summary>
    public bool IsOthers { get; set; } = false;

    /// <summary>
    /// Gets or sets the list of tasks to execute when this branch is taken.
    /// </summary>
    public List<object> Tasks { get; set; } = new();

    /// <summary>
    /// 1-based source line of the <c>when</c> keyword for this arm (#5). Lets
    /// the live engine emit a condition-evaluation signal tagged with the line
    /// so a breakpoint on a <c>when</c> arm pauses when that arm is selected.
    /// Zero means unknown.
    /// </summary>
    public int ConditionLine { get; set; }
}

/// <summary>
/// Represents and manages the logic for a VHDL 'case' statement.
/// Syntax: case selector is when choice1 => statements; when choice2 => statements; when others => statements; end case;
/// </summary>
/// <remarks>
/// <para><strong>Supported VHDL Syntax:</strong></para>
/// <code>
/// case selector is
///     when "0000" => statements;
///     when "0001" | "0010" => statements;  -- Multiple choices
///     when others => statements;           -- Default branch
/// end case;
/// </code>
/// <para><strong>Features:</strong></para>
/// <list type="bullet">
/// <item>Multiple choice values per branch using '|' separator</item>
/// <item>'when others =>' default/fallback branch</item>
/// <item>Nested control structures (if, case, loops) within branches</item>
/// <item>v0.13.0: Can be nested within if/elsif/else branches</item>
/// </list>
/// <para><strong>Design:</strong></para>
/// <para>
/// Uses CaseWhenBranch objects to represent each 'when' clause. During execution,
/// branches are evaluated in order until a match is found. The 'others' branch
/// always matches and should be last.
/// </para>
/// </remarks>
public class CaseStructure : ISControl
{
    /// <summary>The variables in scope during case selector and branch evaluation.</summary>
    private readonly HashSet<Variable> _variables = new();
    /// <summary>The token stream used for parsing the case structure.</summary>
    private readonly TokenStream _streamer;
    /// <summary>Detector used to classify operations found inside branch bodies.</summary>
    private readonly OperationDetector _detector = new();
    /// <summary>The raw name of the selector expression as parsed from the token stream.</summary>
    private string _selectorName = string.Empty;
    /// <summary>The resolved selector variable, or null if it could not be found in scope.</summary>
    private Variable? _selector = null;
    /// <summary>High bit of the selector slice when the case targets a sliced
    /// selector like <c>case op(3 downto 0) is</c>; -1 if the whole signal is
    /// used.</summary>
    private int _selectorSliceHigh = -1;
    /// <summary>Low bit of the selector slice; -1 if the whole signal is used.</summary>
    private int _selectorSliceLow  = -1;

    /// <summary>
    /// Gets the selector variable for the case statement.
    /// </summary>
    public Variable? Selector => _selector;

    /// <summary>
    /// High bit of the slice the selector targets, or -1 if no slice was
    /// specified. Synthesizers that honor this should compare just the
    /// selector bits in <c>[<see cref="SelectorSliceLow"/>, <see cref="SelectorSliceHigh"/>]</c>.
    /// </summary>
    public int SelectorSliceHigh => _selectorSliceHigh;

    /// <summary>Low bit of the slice; -1 when no slice is specified.</summary>
    public int SelectorSliceLow  => _selectorSliceLow;

    /// <summary>True when the case targets a sliced selector.</summary>
    public bool HasSelectorSlice => _selectorSliceHigh >= 0 && _selectorSliceLow >= 0;

    /// <summary>
    /// Gets the list of tasks (for compatibility).
    /// </summary>
    public List<object> Tasks { get; private set; } = new();

    /// <summary>
    /// Gets all 'when' branches.
    /// </summary>
    public List<CaseWhenBranch> Branches { get; private set; } = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="CaseStructure"/> class.
    /// </summary>
    /// <param name="Variables">The set of variables available in the current scope.</param>
    /// <param name="Tokenizer">The token stream to read the case structure from.</param>
    public CaseStructure(HashSet<Variable> Variables, TokenStream Tokenizer)
    {
        _variables = Variables;
        _streamer = Tokenizer;
    }

    /// <summary>
    /// Executes the case structure asynchronously.
    /// </summary>
    public async Task ExecuteAsync()
    {
        if (_selector == null) return;

        string selectorValue = _selector.Value?.ToString() ?? "";

        foreach (var branch in Branches)
        {
            // Check if this is the 'others' branch
            if (branch.IsOthers)
            {
                Executer executer = new(branch.Tasks);
                await executer.ExecuteAsync();
                return;
            }

            // Check if selector matches any of the choices
            foreach (var choice in branch.Choices)
            {
                string cleanChoice = choice.Replace("\"", "").Replace("'", "").Trim();
                string cleanSelector = selectorValue.Replace("\"", "").Replace("'", "").Trim();

                if (cleanChoice.Equals(cleanSelector, StringComparison.OrdinalIgnoreCase))
                {
                    Executer executer = new(branch.Tasks);
                    await executer.ExecuteAsync();
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Synthesizes the case structure from the token stream.
    /// </summary>
    public void Syntetize()
    {
        _streamer.Expect(KEYWORDS); // consume 'case'

        // v0.16.1: Handle optional parentheses around selector: case (selector) is
        bool hasParens = _streamer.Current?.Value == "(";
        if (hasParens)
        {
            _streamer.MoveNext(); // consume '('
        }

        // Get selector expression
        _selectorName = _streamer.Current?.Value ?? "";
        _selector = _variables.FirstOrDefault(v => v.Name.Equals(_selectorName, StringComparison.OrdinalIgnoreCase));
        _streamer.MoveNext(); // consume selector

        // Slice/index after the selector identifier:
        //   case op(3 downto 0) is — slice
        //   case mem(idx) is        — single bit (also handled, slice = idx..idx)
        // Anything more complex (function calls, expressions) is consumed but
        // leaves the slice fields at -1 so the synthesizer falls back to the
        // whole-vector comparison.
        if (_streamer.Current?.Value == "(")
        {
            ParseSelectorSlice();
        }

        // v0.16.1: Consume closing parenthesis if we had an opening one
        if (hasParens && _streamer.Current?.Value == ")")
        {
            _streamer.MoveNext(); // consume ')'
        }

        _streamer.Expect(KEYWORDS); // consume 'is'

        // Parse all 'when' branches
        // Safety: limit iterations to prevent infinite loop
        int mainIterations = 0;
        int maxMainIterations = 10000;

        while (_streamer.Current?.Value?.ToLower() != "end" && _streamer.Current != null && mainIterations < maxMainIterations)
        {
            mainIterations++;

            if (_streamer.Current?.Value?.ToLower() == "when")
            {
                SyntetizeWhenBranch();
            }
            else
            {
                _streamer.MoveNext();
            }
        }

        if (mainIterations >= maxMainIterations)
            throw new InvalidOperationException($"Case statement main loop exceeded maximum iterations. Last token: {_streamer.Current?.Value}");

        // Populate Tasks with first branch for compatibility
        if (Branches.Count > 0)
        {
            Tasks = Branches[0].Tasks;
        }
    }

    /// <summary>
    /// Consumes the parenthesised slice/index expression that may follow a
    /// case selector identifier (e.g. <c>case op(3 downto 0) is</c>) and,
    /// when the expression is a simple literal range, populates
    /// <see cref="SelectorSliceHigh"/> / <see cref="SelectorSliceLow"/>.
    /// </summary>
    /// <remarks>
    /// More complex expressions (function calls, arithmetic, generic-derived
    /// indices) are still consumed cleanly so the parser can advance past
    /// the <c>)</c> — they just don't populate the slice fields and the
    /// downstream synthesizer falls back to the whole-vector comparison.
    /// </remarks>
    private void ParseSelectorSlice()
    {
        _streamer.MoveNext(); // consume the leading '('

        var inner = new List<string>();
        int depth = 1;
        while (_streamer.Current != null && depth > 0)
        {
            string val = _streamer.Current.Value;
            if (val == "(") { depth++; inner.Add(val); _streamer.MoveNext(); continue; }
            if (val == ")")
            {
                depth--;
                if (depth == 0) break;
                inner.Add(val);
                _streamer.MoveNext();
                continue;
            }
            inner.Add(val);
            _streamer.MoveNext();
        }
        if (_streamer.Current?.Value == ")") _streamer.MoveNext();

        // Pattern: literal direction literal — a clean slice we can record.
        if (inner.Count == 3)
        {
            string dir = inner[1].ToLowerInvariant();
            if ((dir == "downto" || dir == "to")
                && int.TryParse(inner[0], out int a)
                && int.TryParse(inner[2], out int b))
            {
                _selectorSliceHigh = Math.Max(a, b);
                _selectorSliceLow  = Math.Min(a, b);
            }
        }
        // Pattern: single literal — bit index.
        else if (inner.Count == 1 && int.TryParse(inner[0], out int idx))
        {
            _selectorSliceHigh = idx;
            _selectorSliceLow  = idx;
        }
        // Anything else: leave slice fields at -1, synth uses full vector.
    }

    /// <summary>
    /// Synthesizes a single 'when' branch.
    /// </summary>
    private void SyntetizeWhenBranch()
    {
        int condLine = _streamer.Current?.Line ?? 0; // line of 'when' keyword (#5)
        _streamer.Expect(KEYWORDS); // consume 'when'

        var branch = new CaseWhenBranch { ConditionLine = condLine };

        // Parse choices (can be multiple separated by '|')
        // Safety: limit iterations to prevent infinite loop
        int maxIterations = 1000;
        int iterations = 0;
        // Note: '=>' is tokenized as a single OPERATORS token by the Parser
        while (_streamer.Current?.Value != "=>" && _streamer.Current != null && iterations < maxIterations)
        {
            iterations++;
            string currentValue = _streamer.Current?.Value ?? "";

            if (currentValue.ToLower() == "others")
            {
                branch.IsOthers = true;
                _streamer.MoveNext();
            }
            else if (currentValue == "|")
            {
                _streamer.MoveNext(); // skip separator
            }
            else if (!string.IsNullOrWhiteSpace(currentValue))
            {
                branch.Choices.Add(currentValue);
                _streamer.MoveNext();
            }
            else
            {
                _streamer.MoveNext();
            }
        }

        if (iterations >= maxIterations)
            throw new InvalidOperationException("Case statement parsing exceeded maximum iterations - possible malformed input");

        // Consume '=>' (single token)
        if (_streamer.Current?.Value == "=>")
        {
            _streamer.MoveNext(); // consume '=>'
        }

        // Parse branch body until next 'when' or 'end'
        SyntetizeBranchBody(branch);

        Branches.Add(branch);
    }

    /// <summary>
    /// Synthesizes the body of a when branch.
    /// </summary>
    /// <param name="branch">The branch whose body is being synthesized.</param>
    private void SyntetizeBranchBody(CaseWhenBranch branch)
    {
        int branchIterations = 0;
        int maxBranchIterations = 10000;

        while (!IsEndOfBranch() && branchIterations < maxBranchIterations)
        {
            branchIterations++;
            if (_streamer.Current?.Type == KEYWORDS)
            {
                var keyword = _streamer.Current.Value.ToLower();
                switch (keyword)
                {
                    case "if":
                        IfStructure ifStructure = new(_variables, _streamer);
                        ifStructure.Syntetize();
                        branch.Tasks.Add(ifStructure);
                        ConsumeEndKeyword("if");
                        break;

                    case "case":
                        CaseStructure nestedCase = new(_variables, _streamer);
                        nestedCase.Syntetize();
                        branch.Tasks.Add(nestedCase);
                        ConsumeEndKeyword("case");
                        break;

                    case "for":
                        ForLoopStructure forLoop = new(_variables, _streamer);
                        forLoop.Syntetize();
                        branch.Tasks.Add(forLoop);
                        ConsumeEndKeyword("loop");
                        break;

                    case "while":
                        WhileLoopStructure whileLoop = new(_variables, _streamer);
                        whileLoop.Syntetize();
                        branch.Tasks.Add(whileLoop);
                        ConsumeEndKeyword("loop");
                        break;

                    case "loop":
                        SimpleLoopStructure simpleLoop = new(_variables, _streamer);
                        simpleLoop.Syntetize();
                        branch.Tasks.Add(simpleLoop);
                        ConsumeEndKeyword("loop");
                        break;

                    case "exit":
                        ExitStatement exitStmt = new(_variables, _streamer);
                        exitStmt.Syntetize();
                        branch.Tasks.Add(exitStmt);
                        break;

                    case "next":
                        NextStatement nextStmt = new(_variables, _streamer);
                        nextStmt.Syntetize();
                        branch.Tasks.Add(nextStmt);
                        break;

                    case "null":
                        NullStatement nullStmt = new(_streamer);
                        nullStmt.Syntetize();
                        branch.Tasks.Add(nullStmt);
                        break;

                    case "return":
                        ReturnStatement returnStmt = new(_variables, _streamer);
                        returnStmt.Syntetize();
                        branch.Tasks.Add(returnStmt);
                        break;

                    default:
                        _streamer.MoveNext();
                        break;
                }
            }
            else
            {
                SyntetizeAssignment(branch);
            }
        }

        if (branchIterations >= maxBranchIterations)
            throw new InvalidOperationException("Case branch body parsing exceeded maximum iterations");
    }

    /// <summary>
    /// Checks if we've reached the end of the current branch.
    /// </summary>
    /// <returns>True if the current token is 'when', 'end', or the stream is exhausted.</returns>
    private bool IsEndOfBranch()
    {
        if (_streamer.Current == null) return true;
        var value = _streamer.Current.Value.ToLower();
        return value == "when" || value == "end";
    }

    /// <summary>
    /// Consumes 'end keyword ;' tokens.
    /// </summary>
    /// <param name="keyword">The keyword expected after 'end' (e.g., "if", "loop", "case").</param>
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
    /// Synthesizes a simple assignment statement.
    /// </summary>
    /// <param name="branch">The branch to add the parsed assignment to.</param>
    private void SyntetizeAssignment(CaseWhenBranch branch)
    {
        // Phase B+ Deep AST (follow-up): capture the assignment's source
        // line BEFORE the streamer consumes the LHS token.
        int assignLine = _streamer.Current?.Line ?? 0;

        var rawDataBuilder = new StringBuilder();
        int assignIterations = 0;
        int maxAssignIterations = 1000;

        while (_streamer.Current?.Value != ";" && !IsEndOfBranch() && _streamer.Current != null && assignIterations < maxAssignIterations)
        {
            assignIterations++;
            rawDataBuilder.Append(_streamer.Current?.Value + " ");
            _streamer.MoveNext();
        }

        if (assignIterations >= maxAssignIterations)
            throw new InvalidOperationException("Case assignment parsing exceeded maximum iterations");

        string operation = rawDataBuilder.ToString().Trim();
        if (string.IsNullOrEmpty(operation)) return;

        if (_streamer.Current?.Value == ";")
        {
            _streamer.MoveNext();
        }

        var type = _detector.GetOperationType(operation);
        if (type == OperationType.ASSIGNATION)
        {
            Operation op = new(operation, _variables, sourceLine: assignLine);
            branch.Tasks.Add(op);
        }
    }

    /// <summary>
    /// Not implemented - use Syntetize() instead.
    /// </summary>
    /// <param name="data">The raw data for synthesis (unused).</param>
    public void Syntetize(string[] data) => throw new NotImplementedException();
}
