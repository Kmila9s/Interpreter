using System.Globalization;
using Parser.Models;

namespace Interpreter.Models;

/// <summary>
/// VHDL's physical type <c>time</c>: values are integers of femtoseconds (the
/// primary unit, as in GHDL), held in a <c>long</c> with type mark <c>time</c>.
/// The single table of time units and the single place time values are made.
/// </summary>
public static class PhysicalTime
{
    /// <summary>The type mark of time values.</summary>
    public const string TypeMark = "time";

    /// <summary>Time units in femtoseconds.</summary>
    public static readonly IReadOnlyDictionary<string, long> UnitFs = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
    {
        ["fs"] = 1, ["ps"] = 1_000, ["ns"] = 1_000_000, ["us"] = 1_000_000_000, ["ms"] = 1_000_000_000_000,
        ["sec"] = 1_000_000_000_000_000, ["min"] = 60_000_000_000_000_000, ["hr"] = 3_600_000_000_000_000_000,
    };

    /// <summary>True when <paramref name="token"/> is a time unit.</summary>
    public static bool IsUnit(string token) => UnitFs.ContainsKey(token);

    /// <summary>True for a value of type <c>time</c>.</summary>
    public static bool IsTime(Variable v) => string.Equals(v.TypeMark, TypeMark, StringComparison.OrdinalIgnoreCase);

    /// <summary>A time value of <paramref name="fs"/> femtoseconds.</summary>
    public static Variable Value(long fs, string name = "Time") =>
        new(name, fs, Constants.DATATYPES.INTEGER) { TypeMark = TypeMark, IsConstant = true };

    /// <summary>The physical literal <c>quantity unit</c> (<c>10 ns</c>, <c>2.5 us</c>) in femtoseconds; false when it is not one.</summary>
    public static bool TryLiteral(string quantity, string unit, out long fs)
    {
        fs = 0;
        if (!UnitFs.TryGetValue(unit, out long scale)) return false;
        if (long.TryParse(quantity.Replace("_", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out long q)) { fs = q * scale; return true; }
        if (double.TryParse(quantity.Replace("_", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) { fs = (long)Math.Round(d * scale); return true; }
        return false;
    }

    /// <summary><c>time'image</c>: GHDL prints the value in its primary unit (<c>15000000 fs</c>).</summary>
    public static string Image(long fs) => $"{fs.ToString(CultureInfo.InvariantCulture)} fs";

    /// <summary>Femtoseconds of a time value (a number, or an integer object); false when it is not one.</summary>
    public static bool TryFs(object? value, out long fs)
    {
        switch (value)
        {
            case long l: fs = l; return true;
            case int i: fs = i; return true;
        }
        return long.TryParse(value?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out fs);
    }
}
