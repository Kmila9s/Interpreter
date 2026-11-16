using Interpreter.Interfaces;
using Parser.Repositories;
using static Parser.Models.Constants.TYPE;

namespace Interpreter.Repositories;

/// <summary>
/// Represents and executes a VHDL 'null' statement.
/// </summary>
/// <remarks>
/// <para><strong>Supported VHDL Syntax:</strong></para>
/// <code>
/// null;  -- No operation, placeholder statement
/// </code>
/// <para><strong>Common Uses:</strong></para>
/// <list type="bullet">
/// <item>Placeholder in empty branches: <c>else null;</c></item>
/// <item>Placeholder in case 'others' clause: <c>when others => null;</c></item>
/// <item>Explicit no-op in sequential regions</item>
/// </list>
/// <para><strong>Execution:</strong></para>
/// <para>
/// Does nothing. Returns immediately with no side effects.
/// </para>
/// </remarks>
public class NullStatement : ISControl
{
    /// <summary>The token stream used for parsing the null statement, or null if constructed without one.</summary>
    private readonly TokenStream? _streamer;

    /// <summary>
    /// Initializes a new instance of the <see cref="NullStatement"/> class.
    /// </summary>
    public NullStatement()
    {
        _streamer = null;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="NullStatement"/> class.
    /// </summary>
    /// <param name="streamer">The token stream for parsing.</param>
    public NullStatement(TokenStream streamer)
    {
        _streamer = streamer;
    }

    /// <summary>
    /// Executes the null statement asynchronously.
    /// </summary>
    /// <returns>A completed task (no-op).</returns>
    public Task ExecuteAsync()
    {
        // No operation - intentionally empty
        return Task.CompletedTask;
    }

    /// <summary>
    /// Synthesizes the null statement from the token stream.
    /// </summary>
    public void Syntetize()
    {
        if (_streamer == null) return;

        _streamer.Expect(KEYWORDS); // consume 'null'

        // Consume ';'
        if (_streamer.Current?.Value == ";")
        {
            _streamer.MoveNext();
        }
    }

}
