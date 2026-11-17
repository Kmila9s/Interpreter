using Parser.Models;

namespace Interpreter.Models;

/// <summary>
/// The single mapping from a VHDL index (or index range) to the characters
/// of a vector/array value string, used by every indexed read
/// (<see cref="DeferredIndex"/>, <see cref="DynamicIndex"/>) and every
/// indexed write (<see cref="AssignmentTarget"/>, the signal scheduler).
/// </summary>
/// <remarks>
/// Values are stored MSB-first: the character at offset 0 is the leftmost
/// element of the declared range. For a vector, one index is one bit; for an
/// array of vectors (<see cref="Variable.ElementSize"/> &gt; 1) one index is
/// one element slot of <c>ElementSize</c> characters. The declared range
/// (<see cref="Variable.IndexToOffset"/>) gives both <c>to</c> and
/// <c>downto</c> directions and non-zero bounds (<c>array (1 to N)</c>).
/// </remarks>
public static class VectorIndexing
{
    /// <summary>
    /// The engine-wide name of bit/element <paramref name="high"/> (<c>c_idx_4</c>)
    /// or of the range <paramref name="high"/>..<paramref name="low"/> (<c>c_idx_3_0</c>)
    /// of <paramref name="parent"/>: the value read by an indexed access and the
    /// net a bit-select read, a part-target write or a component actual meets on.
    /// </summary>
    public static string PartName(string parent, int high, int? low = null) =>
        low is int l ? $"{parent}_idx_{high}_{l}" : $"{parent}_idx_{high}";

    /// <summary>Storage span of element/bit <paramref name="index"/>, or null when it falls outside a value of <paramref name="length"/> characters.</summary>
    public static (int Start, int Width)? Element(Variable v, int index, int length)
    {
        int width = v.ElementSize > 1 ? v.ElementSize : 1;
        int units = width > 1 ? length / width : length;
        // An array without a recorded range keeps the legacy ascending slot
        // order (element k at slot k); vectors without one are downto-0.
        int slot = width > 1 && !v.HasDeclaredRange ? index : v.IndexToOffset(index, units);
        if (slot < 0 || slot >= units) return null;
        return (slot * width, width);
    }

    /// <summary>Storage span covering indices <paramref name="left"/>..<paramref name="right"/> (either direction), or null when either end is out of range.</summary>
    public static (int Start, int Width)? Range(Variable v, int left, int right, int length)
    {
        if (Element(v, left, length) is not { } a || Element(v, right, length) is not { } b) return null;
        int start = Math.Min(a.Start, b.Start);
        return (start, Math.Max(a.Start + a.Width, b.Start + b.Width) - start);
    }

    /// <summary>Number of characters a range of <paramref name="left"/>..<paramref name="right"/> occupies.</summary>
    public static int RangeWidth(Variable v, int left, int right) =>
        (Math.Abs(left - right) + 1) * (v.ElementSize > 1 ? v.ElementSize : 1);

    /// <summary>
    /// <paramref name="current"/> with the range <paramref name="left"/>..<paramref name="right"/>
    /// replaced by <paramref name="bits"/> (right-aligned: shorter values are
    /// zero-extended, longer ones keep their rightmost characters).
    /// </summary>
    /// <exception cref="InvalidOperationException">The range is outside the target's value.</exception>
    public static string Write(Variable target, string current, int left, int right, string bits)
    {
        int length = Math.Max(current.Length, target.Size);
        // Zero-extended like the engine's seeded values (LRM 'U' initial values: v3.1.5).
        if (current.Length < length) current = current.PadLeft(length, '0');
        if (Range(target, left, right, length) is not { } span)
            throw new InvalidOperationException(
                $"index {(left == right ? left.ToString() : $"{left}..{right}")} out of range of \"{target.Name}\"");
        bits = bits.Length >= span.Width ? bits[^span.Width..] : bits.PadLeft(span.Width, '0');
        return string.Concat(current.AsSpan(0, span.Start), bits, current.AsSpan(span.Start + span.Width));
    }
}
