using System.Text;
using Interpreter.Interfaces;
using Interpreter.Models;
using Interpreter.Services;
using Parser.Models;
using Parser.Repositories;
using static Parser.Models.Constants.TYPE;

namespace Interpreter.Repositories;

/// <summary>
/// Represents a single branch (if, elsif, or else) of a VHDL if statement.
/// </summary>
public class IfBranch
{
    /// <summary>
    /// Gets or sets the condition operation for this branch. Null for else branches.
    /// </summary>
    public Operation? Condition { get; set; }

    /// <summary>
    /// Gets or sets the list of tasks to execute when this branch is taken.
    /// </summary>
    public List<object> Tasks { get; set; } = new();

    /// <summary>
    /// Gets or sets whether this is an else branch (no condition).
    /// </summary>
    public bool IsElse { get; set; } = false;

    /// <summary>
    /// 1-based source line of the <c>if</c> / <c>elsif</c> keyword for this
    /// branch (#5). Lets the live engine emit a condition-evaluation signal
    /// tagged with the line so a breakpoint dropped on the condition pauses
    /// when the branch is taken. Zero means unknown.
    /// </summary>
    public int ConditionLine { get; set; }
}

/// <summary>
/// Represents and manages the logic for a VHDL 'if' statement.
/// It synthesizes the condition and the execution block, and executes it if the condition is met.
/// Supports full if-elsif-else-end if structure.
/// </summary>
/// <remarks>
/// <para><strong>Supported VHDL Syntax:</strong></para>
/// <code>
/// if condition1 then
///     statements;
/// elsif condition2 then
///     statements;
/// else
///     statements;
/// end if;
/// </code>
/// <para><strong>v0.13.0 Improvements:</strong></para>
/// <list type="bullet">
/// <item>Added support for nested case statements within if/elsif/else branches</item>
/// <item>Unified ConsumeEndIf() to generic ConsumeEndKeyword() for if and case structures</item>
/// <item>Enhanced branch body synthesis to handle case keyword</item>
/// </list>
/// <para><strong>Design:</strong></para>
/// <para>
/// Uses a flat list of IfBranch objects rather than recursive structure for better
/// maintainability and execution control. Each branch contains its condition (or IsElse flag)
/// and list of tasks to execute.
/// </para>
/// </remarks>
public class IfStructure : ISControl
{
    /// <summary>The variables in scope during condition and body evaluation.</summary>
    private readonly HashSet<Variable> _variables = new();
    /// <summary>The token stream used for parsing the if structure.</summary>
    private readonly TokenStream _streamer;
    /// <summary>Reader of the branch bodies (null until <see cref="Syntetize"/> for a stand-alone if).</summary>
    private SequentialStatementReader? _reader;

    /// <summary>
    /// Gets the list of tasks or operations to be executed if the condition is true.
    /// </summary>
    public List<object> Tasks { get; private set; } = new();

    /// <summary>
    /// All branches (if, elsif*, else?) in order.
    /// </summary>
    public List<IfBranch> Branches { get; private set; } = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="IfStructure"/> class.
    /// </summary>
    /// <param name="Variables">The set of variables available in the current scope.</param>
    /// <param name="Tokenizer">The token stream to read the if structure from.</param>
    public IfStructure(HashSet<Variable> Variables, TokenStream Tokenizer)
    {
        _variables = Variables;
        _streamer = Tokenizer;
    }

    /// <summary>Creates an if statement read by <paramref name="reader"/> (stream on <c>if</c>).</summary>
    public IfStructure(SequentialStatementReader reader) : this(reader.Scope, reader.Stream)
    {
        _reader = reader;
    }

    /// <summary>
    /// Executes the if structure asynchronously. It evaluates conditions in order
    /// and executes the first branch whose condition is true (or the else branch).
    /// </summary>
    public async Task ExecuteAsync()
    {
        foreach (var branch in Branches)
        {
            // Else branch - always execute if we reach it
            if (branch.IsElse)
            {
                Executer executer = new(branch.Tasks);
                await executer.ExecuteAsync();
                return;
            }

            // Evaluate condition
            if (branch.Condition != null)
            {
                var result = branch.Condition.Execute();
                if (Truth.Of(result))
                {
                    Executer executer = new(branch.Tasks);
                    await executer.ExecuteAsync();
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Synthesizes the complete if structure from the token stream.
    /// Handles if-elsif-else-end if as a single unit.
    /// </summary>
    public void Syntetize()
    {
        ParseTrace.Log($"IfStructure @{(_streamer.Current?.Line ?? 0) + 1}");
        _reader ??= new SequentialStatementReader(_streamer, _variables);
        // Parse the main 'if' branch
        SyntetizeConditionalBranch();

        // Continue parsing elsif and else branches
        while (_streamer.Current != null)
        {
            var currentValue = _streamer.Current.Value.ToLower();

            if (currentValue == "elsif")
            {
                SyntetizeConditionalBranch();
            }
            else if (currentValue == "else")
            {
                SyntetizeElseBranch();
            }
            else if (currentValue == "end")
            {
                // End of if structure - don't consume here, let caller handle it
                break;
            }
            else
            {
                break;
            }
        }

        // Also populate the Tasks list with the first branch for backwards compatibility
        if (Branches.Count > 0)
        {
            Tasks = Branches[0].Tasks;
        }
    }

    /// <summary>
    /// Synthesizes an <c>if</c> or <c>elsif</c> branch: the condition up to <c>then</c>, then the body.
    /// </summary>
    private void SyntetizeConditionalBranch()
    {
        var keyword = _streamer.Current!;
        var branch = new IfBranch { ConditionLine = keyword.SourceLine }; // line of 'if'/'elsif' (#5)
        _streamer.MoveNext(); // consume 'if' / 'elsif'

        var rawDataBuilder = new StringBuilder();
        while (_streamer.Current?.Value.ToLower() != "then")
        {
            if (_streamer.Current is null)
                throw new ParserException($"'then' expected after the '{keyword.Value}' condition", _streamer.Previous);
            rawDataBuilder.Append(_streamer.Current.Value + " ");
            _streamer.MoveNext();
        }
        _streamer.MoveNext(); // consume 'then'

        branch.Condition = new Operation(rawDataBuilder.ToString().Trim(), _variables, branch.ConditionLine);
        SyntetizeBranchBody(branch);
        Branches.Add(branch);
    }

    /// <summary>
    /// Synthesizes the 'else' branch.
    /// </summary>
    private void SyntetizeElseBranch()
    {
        _streamer.Expect(KEYWORDS); // consume 'else'

        var branch = new IfBranch { IsElse = true };

        // Parse body
        SyntetizeBranchBody(branch);

        Branches.Add(branch);
    }

    /// <summary>
    /// Synthesizes the body of a branch until 'elsif', 'else', or 'end' is found.
    /// </summary>
    /// <param name="branch">The branch whose body is being synthesized.</param>
    private void SyntetizeBranchBody(IfBranch branch) =>
        _reader!.ReadUntil(branch.Tasks, "elsif", "else", "end");
}