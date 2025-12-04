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
    private readonly HashSet<Variable> _variables = new();
    private readonly TokenStream _streamer;
    private readonly OperationDetector _detector = new();
    private string _selectorName = string.Empty;
    private Variable? _selector = null;

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

        // Get selector expression
        _selectorName = _streamer.Current?.Value ?? "";
        _selector = _variables.FirstOrDefault(v => v.Name.Equals(_selectorName, StringComparison.OrdinalIgnoreCase));
        _streamer.MoveNext(); // consume selector

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
    /// Synthesizes a single 'when' branch.
    /// </summary>
    private void SyntetizeWhenBranch()
    {
        _streamer.Expect(KEYWORDS); // consume 'when'

        var branch = new CaseWhenBranch();

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
    private bool IsEndOfBranch()
    {
        if (_streamer.Current == null) return true;
        var value = _streamer.Current.Value.ToLower();
        return value == "when" || value == "end";
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
    /// Synthesizes a simple assignment statement.
    /// </summary>
    private void SyntetizeAssignment(CaseWhenBranch branch)
    {
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
            Operation op = new(operation, _variables);
            branch.Tasks.Add(op);
        }
    }

    /// <summary>
    /// Not implemented - use Syntetize() instead.
    /// </summary>
    public void Syntetize(string[] data) => throw new NotImplementedException();
}
