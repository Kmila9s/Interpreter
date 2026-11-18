using Interpreter.Interfaces;
using Interpreter.Models;
using Interpreter.Services;
using Parser.Models;
using Parser.Repositories;
using static Parser.Models.Constants.TYPE;

namespace Interpreter.Repositories;

// Note: This file uses ExitException and NextException from ControlExceptions.cs

/// <summary>
/// Represents and manages the logic for a VHDL simple (infinite) loop statement.
/// Parses and executes infinite loops that rely on exit statements to terminate.
/// </summary>
/// <remarks>
/// <para><strong>Supported VHDL Syntax:</strong></para>
/// <code>
/// -- Simple infinite loop with conditional exit
/// loop
///     read_data;
///     exit when buffer_full = '1';
/// end loop;
///
/// -- Loop with unconditional exit
/// loop
///     process_item;
///     if done then
///         exit;
///     end if;
/// end loop;
///
/// -- Loop with multiple exit conditions
/// loop
///     compute;
///     exit when error = '1';
///     exit when complete = '1';
/// end loop;
/// </code>
/// <para><strong>Features:</strong></para>
/// <list type="bullet">
/// <item>Infinite loop semantics (no implicit termination condition)</item>
/// <item>Exit statement support with optional 'when' condition</item>
/// <item>Unconditional exit (just 'exit;') supported</item>
/// <item>Nested control structures (if, case, loops) within body</item>
/// <item>Maximum iteration limit (10,000) as safety fallback</item>
/// </list>
/// <para><strong>Exit Handling:</strong></para>
/// <para>
/// <c>exit</c>/<c>next</c> statements in the body throw <see cref="ExitException"/> /
/// <see cref="NextException"/>, caught here when unlabeled or naming this loop.
/// </para>
/// <para><strong>Safety:</strong></para>
/// <para>
/// A maximum iteration count (MaxIterations = 10,000) prevents runaway loops
/// during simulation if no exit condition is met. A warning is issued if reached.
/// </para>
/// </remarks>
public class SimpleLoopStructure : ILoopStructure
{
    /// <summary>The variables in scope during loop body execution.</summary>
    private readonly HashSet<Variable> _variables;
    /// <summary>The token stream used for parsing the loop structure.</summary>
    private readonly TokenStream _streamer;
    /// <summary>Reader of the loop body (null until <see cref="Syntetize"/> for a stand-alone loop).</summary>
    private SequentialStatementReader? _reader;

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
    /// Initializes a new instance of the <see cref="SimpleLoopStructure"/> class.
    /// </summary>
    /// <param name="Variables">The set of variables available in the current scope.</param>
    /// <param name="Tokenizer">The token stream to read the loop structure from.</param>
    public SimpleLoopStructure(HashSet<Variable> Variables, TokenStream Tokenizer)
    {
        _variables = Variables;
        _streamer = Tokenizer;
    }

    /// <summary>Creates a loop read by <paramref name="reader"/> (stream on its first keyword).</summary>
    public SimpleLoopStructure(SequentialStatementReader reader) : this(reader.Scope, reader.Stream)
    {
        _reader = reader;
    }

    /// <summary>
    /// Executes the simple loop asynchronously.
    /// </summary>
    public async Task ExecuteAsync()
    {
        int iterations = 0;
        while (iterations < MaxIterations && await LoopPass.RunAsync(Tasks, LoopLabel))
            iterations++;

        if (iterations >= MaxIterations)
        {
            Console.WriteLine($"Warning: Simple loop exceeded maximum iterations ({MaxIterations})");
        }
    }

    /// <summary>
    /// Synthesizes the simple loop structure from the token stream.
    /// </summary>
    public void Syntetize()
    {
        Interpreter.Services.ParseTrace.Log($"SimpleLoop @{(_streamer.Current?.Line ?? 0) + 1}");
        _streamer.Expect(KEYWORDS); // consume 'loop'

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
