namespace Interpreter.Models;

/// <summary>
/// Base of <see cref="ExitException"/> and <see cref="NextException"/>: carries the
/// optional loop label and the single rule deciding which loop handles it.
/// </summary>
public abstract class LoopControlException(string? label, string message) : Exception(message)
{
    /// <summary>The loop label named by the statement; null targets the innermost loop.</summary>
    public string? Label { get; } = label;

    /// <summary>
    /// True when a loop labeled <paramref name="loopLabel"/> handles this statement:
    /// an unlabeled exit/next targets the innermost loop, a labeled one the loop with
    /// that label (VHDL identifiers are case-insensitive).
    /// </summary>
    public bool Targets(string? loopLabel) =>
        string.IsNullOrEmpty(Label) || string.Equals(Label, loopLabel, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Exception thrown by the 'exit' statement to exit from a loop.
/// </summary>
/// <remarks>
/// <para><strong>VHDL Syntax:</strong></para>
/// <code>
/// exit;                       -- Exit innermost loop
/// exit loop_label;            -- Exit named loop
/// exit when condition;        -- Exit when condition is true
/// exit loop_label when cond;  -- Exit named loop when condition is true
/// </code>
/// <para>
/// This exception is caught by loop structures (for, while, simple loop)
/// to implement proper VHDL loop exit semantics.
/// </para>
/// </remarks>
public sealed class ExitException(string? label = null)
    : LoopControlException(label, "Exit statement executed");

/// <summary>
/// Exception thrown by the 'next' statement to skip to the next iteration of a loop.
/// </summary>
/// <remarks>
/// <para><strong>VHDL Syntax:</strong></para>
/// <code>
/// next;                       -- Skip to next iteration of innermost loop
/// next loop_label;            -- Skip to next iteration of named loop
/// next when condition;        -- Skip when condition is true
/// next loop_label when cond;  -- Skip named loop when condition is true
/// </code>
/// <para>
/// This exception is caught by loop structures to skip remaining body statements
/// and proceed to the next iteration.
/// </para>
/// </remarks>
public sealed class NextException(string? label = null)
    : LoopControlException(label, "Next statement executed");

/// <summary>
/// Exception thrown by the 'return' statement to return from a function or procedure.
/// </summary>
/// <remarks>
/// <para><strong>VHDL Syntax:</strong></para>
/// <code>
/// return;                     -- Return from procedure (no value)
/// return expression;          -- Return from function with value
/// </code>
/// <para>
/// For functions, the Value property contains the evaluated return expression.
/// For procedures, Value is null.
/// </para>
/// </remarks>
public class ReturnException : Exception
{
    /// <summary>
    /// Gets the return value (for functions) or null (for procedures).
    /// </summary>
    public object? Value { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ReturnException"/> class.
    /// </summary>
    /// <param name="value">The return value from a function, or null for procedures.</param>
    public ReturnException(object? value = null) : base("Return statement executed")
    {
        Value = value;
    }
}

/// <summary>
/// The design asked the simulation to end (<c>std.env.stop</c>/<c>finish</c>, or a
/// <c>severity failure</c> assertion/report). Raised by the statement, caught by the
/// engine (<see cref="Interpreter.Services.DeltaCycleEngine.EndRequest"/>): the run
/// ends at the current time, without applying the updates of the current delta, as GHDL does.
/// </summary>
/// <param name="failed">True for a <c>severity failure</c> (the run fails with <paramref name="message"/>).</param>
/// <param name="message">Why the simulation ended.</param>
public sealed class SimulationEndException(bool failed, string message) : Exception(message)
{
    /// <summary>True when the run ends as a failure.</summary>
    public bool Failed { get; } = failed;
}
