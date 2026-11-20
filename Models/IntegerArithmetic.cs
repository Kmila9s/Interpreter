namespace Interpreter.Models;

/// <summary>
/// VHDL integer arithmetic, the single implementation used while simulating and
/// when folding constants: <c>/</c> truncates toward zero, <c>mod</c> takes the
/// sign of the divisor, <c>rem</c> the sign of the dividend, and a zero divisor
/// is a run-time error (GHDL: <c>division by 0</c>).
/// </summary>
public static class IntegerArithmetic
{
    /// <summary>GHDL's message for a zero divisor.</summary>
    public const string DivisionByZero = "division by 0";

    /// <summary><paramref name="a"/> <paramref name="op"/> <paramref name="b"/>; 0 for an unknown operator.</summary>
    public static long Apply(string op, long a, long b) => op.ToLowerInvariant() switch
    {
        "+" => a + b,
        "-" => a - b,
        "*" => a * b,
        "/" => b == 0 ? throw new SimulationRuntimeException(DivisionByZero) : a / b,
        "mod" => Mod(a, b),
        "rem" => b == 0 ? throw new SimulationRuntimeException(DivisionByZero) : a % b,
        "**" => (long)Math.Pow(a, b),
        _ => 0,
    };

    /// <summary><c>a mod b</c>: the result has the sign of <paramref name="b"/>.</summary>
    public static long Mod(long a, long b)
    {
        if (b == 0) throw new SimulationRuntimeException(DivisionByZero);
        long r = a % b;
        return r != 0 && (r < 0) != (b < 0) ? r + b : r;
    }

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
