using System.Text;
using Interpreter.Interfaces;
using Interpreter.Models;
using Interpreter.Services;
using Parser.Models;
using Parser.Repositories;
using static Parser.Models.Constants.TYPE;

namespace Interpreter.Repositories;

// Note: This file uses ExitException and NextException from ControlExceptions.cs

/// <summary>
/// Represents and manages the logic for a VHDL 'while' loop statement.
/// Parses and executes while loops with condition-based iteration.
/// </summary>
/// <remarks>
/// <para><strong>Supported VHDL Syntax:</strong></para>
/// <code>
/// -- Simple while loop
/// while count &lt; 10 loop
///     count := count + 1;
/// end loop;
///
/// -- While loop with complex condition
/// while (enable = '1') and (error = '0') loop
///     process_data;
/// end loop;
///
/// -- While loop with exit
/// while true loop
///     statements;
///     exit when done = '1';
/// end loop;
/// </code>
/// <para><strong>Features:</strong></para>
/// <list type="bullet">
/// <item>Condition evaluation before each iteration</item>
/// <item>Support for boolean, std_logic ('1'/'0'), and string conditions</item>
/// <item>Nested control structures (if, case, loops) within body</item>
/// <item>Exit statement support with optional 'when' condition</item>
/// <item>Maximum iteration limit (10,000) to prevent infinite loops</item>
/// </list>
/// <para><strong>Condition Evaluation:</strong></para>
/// <para>
/// The condition is re-evaluated before each iteration using a fresh Operation instance.
/// This ensures variable changes in the loop body affect the condition.
/// Supported condition result types:
/// </para>
/// <list type="bullet">
/// <item><c>bool</c> - Direct boolean evaluation</item>
/// <item><c>char</c> - '1' is true, anything else is false</item>
/// <item><c>string</c> - "1" or "true" (case-insensitive) is true</item>
/// </list>
/// <para><strong>Safety:</strong></para>
/// <para>
/// A maximum iteration count (MaxIterations = 10,000) prevents runaway loops
/// during simulation. A warning is issued if this limit is reached.
/// </para>
/// </remarks>
public class WhileLoopStructure : ILoopStructure
{
    /// <summary>The variables in scope during condition and loop body evaluation.</summary>
    private readonly HashSet<Variable> _variables;
    /// <summary>1-based source line of the statement (errors in its condition/selector report it).</summary>
    private int _line;

    /// <summary>The token stream used for parsing the while loop structure.</summary>
    private readonly TokenStream _streamer;
    /// <summary>Reader of the loop body (null until <see cref="Syntetize"/> for a stand-alone loop).</summary>
    private SequentialStatementReader? _reader;

    /// <summary>
    /// Gets the condition operation for the while loop.
    /// </summary>
    public Operation? Condition { get; private set; }

    /// <summary>
    /// Gets the raw condition string.
    /// </summary>
    public string ConditionString { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the list of tasks to execute in the loop body.
    /// </summary>
    public List<object> Tasks { get; private set; } = new();

    /// <summary>
    /// Gets or sets the optional loop label for targeted exit/next statements.
    /// </summary>
    public string? LoopLabel { get; set; }

    /// <summary>
    /// Maximum iterations to prevent infinite loops during simulation.
    /// </summary>
    private const int MaxIterations = 10000;

    /// <summary>
    /// Initializes a new instance of the <see cref="WhileLoopStructure"/> class.
    /// </summary>
    /// <param name="Variables">The set of variables available in the current scope.</param>
    /// <param name="Tokenizer">The token stream to read the while loop structure from.</param>
    public WhileLoopStructure(HashSet<Variable> Variables, TokenStream Tokenizer)
    {
        _variables = Variables;
        _streamer = Tokenizer;
    }

    /// <summary>Creates a loop read by <paramref name="reader"/> (stream on its first keyword).</summary>
    public WhileLoopStructure(SequentialStatementReader reader) : this(reader.Scope, reader.Stream)
    {
        _reader = reader;
    }

    /// <summary>
    /// Executes the while loop asynchronously.
    /// </summary>
    public async Task ExecuteAsync()
    {
        if (Condition == null) return;

        int iterations = 0;

        while (iterations < MaxIterations)
        {
            // Re-evaluate condition each iteration
            Operation conditionOp = new(ConditionString, _variables);
            var result = conditionOp.Execute();

            if (!Truth.Of(result)) break;

            if (!await LoopPass.RunAsync(Tasks, LoopLabel)) break;
            iterations++;
        }

        if (iterations >= MaxIterations)
        {
            Console.WriteLine($"Warning: While loop exceeded maximum iterations ({MaxIterations})");
        }
    }

    /// <summary>
    /// Synthesizes the while loop structure from the token stream.
    /// </summary>
    public void Syntetize()
    {
        Interpreter.Services.ParseTrace.Log($"WhileLoop @{(_streamer.Current?.Line ?? 0) + 1}");
        _line = _streamer.Current?.SourceLine ?? 0;
        _streamer.Expect(KEYWORDS); // consume 'while'

        // Parse condition until 'loop'
        var conditionBuilder = new StringBuilder();
        while (_streamer.Current?.Value.ToLower() != "loop")
        {
            if (_streamer.Current is null)
                throw new ParserException("'loop' expected after the while condition", _streamer.Previous);
            conditionBuilder.Append(_streamer.Current.Value + " ");
            _streamer.MoveNext();
        }

        ConditionString = conditionBuilder.ToString().Trim();
        Condition = new Operation(ConditionString, _variables, _line);

        // Consume 'loop'
        if (_streamer.Current?.Value.ToLower() == "loop")
        {
            _streamer.MoveNext();
        }

        // Parse loop body
        SyntetizeLoopBody();
    }

    /// <summary>
    /// Synthesizes the loop body.
    /// </summary>
    private void SyntetizeLoopBody() =>
        (_reader ?? new SequentialStatementReader(_streamer, _variables))
            .InLoop(LoopLabel).ReadUntil(Tasks, "end");
}
