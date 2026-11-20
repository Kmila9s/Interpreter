using System.Numerics;
using System.Text;
using Parser.Models;

namespace Interpreter.Models;

/// <summary>
/// The single numeric core of logic vectors (numeric_std, std_logic_arith,
/// std_logic_signed/unsigned): bits ↔ integer of any width as
/// <see cref="BigInteger"/>, signed (two's complement) or unsigned, and the
/// numeric_std operators with their result widths. <c>H</c>/<c>L</c> read as
/// <c>1</c>/<c>0</c>; any other metavalue makes the value unknown.
/// </summary>
public static class LogicVector
{
    /// <summary>
    /// The integer <paramref name="bits"/> (MSB first) stands for; false when it
    /// holds a metavalue (<c>U X Z W -</c>) or is empty.
    /// </summary>
    public static bool TryToInteger(string bits, Signedness signedness, out BigInteger value)
    {
        value = BigInteger.Zero;
        if (bits.Length == 0) return false;
        foreach (char c in bits)
        {
            char b = StdLogic.ToX01(c);
            if (b == 'X') return false;
            value = (value << 1) + (b == '1' ? 1 : 0);
        }
        if (signedness == Signedness.Signed && StdLogic.ToX01(bits[0]) == '1')
            value -= BigInteger.One << bits.Length;
        return true;
    }

    /// <summary><paramref name="value"/> as <paramref name="width"/> bits, two's complement, wrapped modulo 2^width.</summary>
    public static string FromInteger(BigInteger value, int width)
    {
        if (width <= 0) return string.Empty;
        var modulus = BigInteger.One << width;
        var v = BigInteger.Remainder(value, modulus);
        if (v.Sign < 0) v += modulus;
        var sb = new StringBuilder(width);
        for (int i = width - 1; i >= 0; i--)
            sb.Append(((v >> i) & BigInteger.One).IsZero ? '0' : '1');
        return sb.ToString();
    }

    /// <summary>
    /// <paramref name="bits"/> resized to <paramref name="width"/> as numeric_std
    /// <c>resize</c>: unsigned zero-extends or keeps the rightmost bits; signed
    /// extends the sign, and when shrinking keeps the sign bit and the rightmost
    /// <c>width − 1</c> bits.
    /// </summary>
    public static string Resize(string bits, int width, Signedness signedness)
    {
        if (width <= 0) return string.Empty;
        if (bits.Length == 0) return new string('0', width);
        if (signedness == Signedness.Signed)
        {
            if (width >= bits.Length) return new string(bits[0], width - bits.Length) + bits;
            return bits[0] + bits[(bits.Length - width + 1)..];
        }
        return width >= bits.Length ? new string('0', width - bits.Length) + bits : bits[(bits.Length - width)..];
    }

    /// <summary>
    /// Shift (<c>sll srl sla sra</c>, <c>shift_left/right</c>) or rotate
    /// (<c>rol ror</c>, <c>rotate_left/right</c>) of <paramref name="bits"/> by
    /// <paramref name="count"/> positions. A shift by the length or more empties the
    /// vector (fill only); <c>shift_right</c> of a signed value and <c>sra</c> fill
    /// with the sign bit, <c>sla</c> with the rightmost bit; rotations wrap.
    /// A negative count shifts the other way.
    /// </summary>
    public static string Shift(string bits, string op, long count, Signedness signedness)
    {
        int n = bits.Length;
        if (n == 0) return bits;
        string o = op.ToLowerInvariant();
        if (count < 0)
        {
            count = -count;
            o = o switch
            {
                "sll" => "srl", "srl" => "sll", "sla" => "sra", "sra" => "sla",
                "rol" => "ror", "ror" => "rol",
                "shift_left" => "shift_right", "shift_right" => "shift_left",
                "rotate_left" => "rotate_right", "rotate_right" => "rotate_left",
                _ => o,
            };
        }
        bool left = o is "sll" or "sla" or "rol" or "shift_left" or "rotate_left";
        if (o is "rol" or "ror" or "rotate_left" or "rotate_right")
        {
            int k = (int)(count % n);
            return left ? bits[k..] + bits[..k] : bits[(n - k)..] + bits[..(n - k)];
        }
        char fill = o switch
        {
            "sra" => bits[0],
            "sla" => bits[^1],
            "shift_right" when signedness == Signedness.Signed => bits[0],
            _ => '0',
        };
        int s = (int)Math.Min(count, n);
        return left ? bits[s..] + new string(fill, s) : new string(fill, s) + bits[..(n - s)];
    }

    /// <summary>
    /// Width of a numeric_std arithmetic result: <c>+ -</c> the longer operand,
    /// <c>*</c> the sum (an integer operand counts as the vector's length),
    /// <c>/</c> the left length, <c>mod rem</c> the right length.
    /// </summary>
    /// <param name="op">The operator.</param>
    /// <param name="left">Left length, or null for an integer operand.</param>
    /// <param name="right">Right length, or null for an integer operand.</param>
    public static int ResultWidth(string op, int? left, int? right)
    {
        int l = left ?? right ?? 1, r = right ?? left ?? 1;
        return op.ToLowerInvariant() switch
        {
            "*" => l + r,
            "/" => l,
            "mod" or "rem" => r,
            _ => Math.Max(l, r),
        };
    }

    /// <summary>
    /// <paramref name="a"/> <paramref name="op"/> <paramref name="b"/> on integers:
    /// <c>+ - * / mod rem **</c> with VHDL's rounding and signs (division by zero
    /// is the run-time error of <see cref="IntegerArithmetic"/>).
    /// </summary>
    public static BigInteger Apply(string op, BigInteger a, BigInteger b) => op.ToLowerInvariant() switch
    {
        "+" => a + b,
        "-" => a - b,
        "*" => a * b,
        "/" => b.IsZero ? throw new SimulationRuntimeException(IntegerArithmetic.DivisionByZero) : BigInteger.Divide(a, b),
        "rem" => b.IsZero ? throw new SimulationRuntimeException(IntegerArithmetic.DivisionByZero) : BigInteger.Remainder(a, b),
        "mod" => b.IsZero ? throw new SimulationRuntimeException(IntegerArithmetic.DivisionByZero) : Mod(a, b),
        "**" => b.Sign < 0 ? BigInteger.Zero : BigInteger.Pow(a, (int)b),
        _ => BigInteger.Zero,
    };

    private static BigInteger Mod(BigInteger a, BigInteger b)
    {
        var r = BigInteger.Remainder(a, b);
        return !r.IsZero && (r.Sign < 0) != (b.Sign < 0) ? r + b : r;
    }

    /// <summary>
    /// <paramref name="value"/> as a VHDL <c>integer</c>: GHDL does not check the
    /// base type, so a result outside 32 bits wraps (<c>integer'high + 1</c> =
    /// <c>integer'low</c>).
    /// </summary>
    public static int ToInt32(BigInteger value) => (int)(uint)(value & uint.MaxValue);
}
