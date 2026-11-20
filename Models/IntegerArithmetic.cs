namespace Interpreter.Models;

/// <summary>
/// VHDL integer arithmetic, the single implementation used while simulating and
/// when folding constants: <c>/</c> truncates toward zero, <c>mod</c> takes the
/// sign of the divisor, <c>rem</c> the sign of the dividend, and a zero divisor
/// is a run-time error (GHDL: <c>division by 0</c>). The operators themselves live
/// in <see cref="LogicVector.Apply"/> at any width; this is the <c>long</c> fast path.
/// </summary>
public static class IntegerArithmetic
{
    /// <summary>GHDL's message for a zero divisor.</summary>
    public const string DivisionByZero = "division by 0";

    /// <summary><paramref name="a"/> <paramref name="op"/> <paramref name="b"/> (<see cref="LogicVector.Apply"/>); 0 for an unknown operator.</summary>
    public static long Apply(string op, long a, long b) => op.ToLowerInvariant() switch
    {
        "+" => a + b,
        "-" => a - b,
        "*" => a * b,
        "/" => b == 0 ? throw new SimulationRuntimeException(DivisionByZero) : a / b,
        "rem" => b == 0 ? throw new SimulationRuntimeException(DivisionByZero) : a % b,
        "mod" => b == 0 ? throw new SimulationRuntimeException(DivisionByZero) : (a % b is var r && r != 0 && (r < 0) != (b < 0) ? r + b : r),
        _ => (long)LogicVector.Apply(op, a, b),   // ** (and the rest) at any width
    };

    /// <summary>Real <c>/</c> with the same zero-divisor error; the other operators as in C#.</summary>
    public static double ApplyReal(string op, double a, double b) => op.ToLowerInvariant() switch
    {
        "+" => a + b,
        "-" => a - b,
        "*" => a * b,
        "/" => b == 0 ? throw new SimulationRuntimeException(DivisionByZero) : a / b,
        "**" => Math.Pow(a, b),
        _ => 0,
    };
}
