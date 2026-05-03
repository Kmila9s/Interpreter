namespace Interpreter.Models;

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
public class ExitException : Exception
{
    /// <summary>
    /// Gets the optional label of the loop to exit.
    /// When null, exits the innermost enclosing loop.
    /// </summary>
    public string? Label { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ExitException"/> class.
    /// </summary>
    /// <param name="label">Optional label of the loop to exit.</param>
    public ExitException(string? label = null) : base("Exit statement executed")
    {
        Label = label;
    }
}

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
public class NextException : Exception
{
    /// <summary>
    /// Gets the optional label of the loop to continue.
    /// When null, continues the innermost enclosing loop.
    /// </summary>
    public string? Label { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="NextException"/> class.
    /// </summary>
    /// <param name="label">Optional label of the loop to continue.</param>
    public NextException(string? label = null) : base("Next statement executed")
    {
        Label = label;
    }
}

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
