using System.Globalization;
using Interpreter.Services;
using Parser.Models;

namespace Interpreter.Models;

/// <summary>
/// The single value↔bits encoding of record fields (<see cref="FieldSlot"/>):
/// logic/vectors as stored, integers in binary (two's complement when signed),
/// enumerations by literal index, booleans as one bit. Used by field reads and
/// writes, record aggregates, default values and the VCD writer.
/// </summary>
public static class FieldCodec
{
    /// <summary>
    /// Default bits of a record: <c>'U'</c> for logic/vector fields and zero
    /// for integer/enum/boolean fields — the same values Kmila gives scalar
    /// objects of those types today (LRM <c>'left</c> values arrive with the
    /// initial-value policy, v3.1.5).
    /// </summary>
    public static string DefaultBits(RecordLayout layout) =>
        string.Concat(layout.Fields.Select(DefaultBits));

    /// <summary>Default bits of one field (see <see cref="DefaultBits(RecordLayout)"/>).</summary>
    public static string DefaultBits(FieldSlot field) => field.Kind switch
    {
        FieldKind.Record => DefaultBits(field.Shape.Layout!),
        FieldKind.Vector when field.Shape.ElementShape?.Layout is { } element =>
            string.Concat(Enumerable.Repeat(DefaultBits(element), field.Width / Math.Max(element.Width, 1))),
        FieldKind.Logic or FieldKind.Vector => new string('U', field.Width),
        _ => new string('0', field.Width),
    };

    /// <summary>Encodes <paramref name="value"/> as the <see cref="FieldSlot.Width"/> characters of <paramref name="field"/>.</summary>
    public static string Encode(FieldSlot field, object? value) => field.Kind switch
    {
        FieldKind.Integer => IntegerBits(IntegerOf(value), field.Width),
        FieldKind.Boolean => Operator.ToBool(value) ? "1" : "0",
        FieldKind.Enum => IntegerBits(EnumIndex(field.Shape.EnumLiterals, value), field.Width),
        _ => VectorIndexing.Fit(LogicBits(value, field.Width), field.Width),
    };

    /// <summary>
    /// Reads the field's characters back as a typed value: <c>INTEGER</c>
    /// (number or enum literal), <c>BOOLEAN</c>, or the bits as
    /// <c>STD_LOGIC</c>/<c>STD_LOGIC_VECTOR</c>.
    /// </summary>
    public static Variable Decode(FieldSlot field, string bits, string name)
    {
        var v = Template(field, name);
        v.Value = DecodeValue(field, bits);
        return v;
    }

    /// <summary>The value <see cref="Decode"/> would hold, without building a <see cref="Variable"/> (per-sample views).</summary>
    public static object DecodeValue(FieldSlot field, string bits) => field.Kind switch
    {
        FieldKind.Integer => IntegerFrom(bits, field.Signed),
        FieldKind.Boolean => bits == "1",
        FieldKind.Enum => IntegerFrom(bits, signed: false) is var index
                          && field.Shape.EnumLiterals is { } literals && index >= 0 && index < literals.Count
            ? literals[(int)index]
            : bits,
        _ => bits,
    };

    /// <summary>
    /// A typed object for <paramref name="field"/> named <paramref name="name"/>
    /// (type, width, enum literals, vector shape) — the template of field views
    /// and stimulus inputs; its value is the field's default.
    /// </summary>
    public static Variable Template(FieldSlot field, string name)
    {
        var v = field.Kind switch
        {
            FieldKind.Integer => new Variable(name, 0L, Constants.DATATYPES.INTEGER),
            FieldKind.Boolean => new Variable(name, false, Constants.DATATYPES.BOOLEAN),
            FieldKind.Enum => new Variable(name, string.Empty, Constants.DATATYPES.INTEGER),
            FieldKind.Logic => new Variable(name, "U", Constants.DATATYPES.STD_LOGIC),
            _ => new Variable(name, DefaultBits(field), Constants.DATATYPES.STD_LOGIC_VECTOR, field.Width),
        };
        if (field.Kind is FieldKind.Enum or FieldKind.Vector or FieldKind.Record) v.CopyShapeFrom(field.Shape);
        v.Value = DecodeValue(field, DefaultBits(field));
        return v;
    }

    /// <summary>The characters of a leaf at <paramref name="offset"/> in a record value, or null when the value is shorter.</summary>
    public static string? Slice(object? recordValue, int offset, int width) =>
        recordValue?.ToString() is { } all && all.Length >= offset + width ? all.Substring(offset, width) : null;

    /// <summary>Integer value of an evaluated operand (decimal text, number or boolean-free object).</summary>
    public static long IntegerOf(object? value) => value switch
    {
        null => 0,
        int i => i,
        long l => l,
        bool b => b ? 1 : 0,
        IConvertible c when value is not string => Convert.ToInt64(c, CultureInfo.InvariantCulture),
        _ => long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) ? n : 0,
    };

    /// <summary><paramref name="value"/> as <paramref name="width"/> binary characters (two's complement, truncated).</summary>
    public static string IntegerBits(long value, int width)
    {
        var chars = new char[width];
        for (int i = width - 1; i >= 0; i--, value >>= 1)
            chars[i] = (value & 1) == 1 ? '1' : '0';
        return new string(chars);
    }

    private static long IntegerFrom(string bits, bool signed)
    {
        long value = 0;
        foreach (char c in bits)
        {
            if (c is not ('0' or '1')) return 0;
            value = (value << 1) | (uint)(c - '0');
        }
        if (signed && bits.Length is > 0 and < 64 && bits[0] == '1')
            value -= 1L << bits.Length;
        return value;
    }

    /// <summary>Position of the literal <paramref name="value"/> in <paramref name="literals"/> (a raw number passes through), or 0.</summary>
    public static long EnumIndex(IReadOnlyList<string>? literals, object? value)
    {
        string text = value?.ToString() ?? string.Empty;
        literals ??= [];
        for (int i = 0; i < literals.Count; i++)
            if (literals[i].Equals(text, StringComparison.OrdinalIgnoreCase)) return i;
        return HdlLiteral.TryParseInt(text, out long raw) ? raw : 0;
    }

    /// <summary>
    /// Bits of a logic/vector value: an <c>(others =&gt; c)</c> marker expands to
    /// <paramref name="width"/> characters; literals lose their quotes.
    /// </summary>
    public static string LogicBits(object? value, int width)
    {
        string text = value?.ToString()?.Trim() ?? string.Empty;
        return HdlLiteral.TryOthersFill(text, out char fill)
            ? new string(fill, width)
            : text.Trim('\'').Trim('"');
    }
}
