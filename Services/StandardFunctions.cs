using Parser.Models;
using static Parser.Models.Constants;

namespace Interpreter.Services;

/// <summary>
/// Provides implementations of standard VHDL functions from IEEE libraries.
/// These include edge detection functions, type conversion functions, and other utilities.
/// </summary>
/// <remarks>
/// <para><strong>Implemented IEEE Functions:</strong></para>
/// <list type="bullet">
/// <item><strong>Edge Detection:</strong> rising_edge(), falling_edge()</item>
/// <item><strong>Type Conversions:</strong> to_integer(), to_unsigned(), to_signed(), std_logic_vector()</item>
/// <item><strong>Vector Operations:</strong> resize(), shift_left(), shift_right(), rotate_left(), rotate_right()</item>
/// <item><strong>Arithmetic:</strong> AddVectors(), SubtractVectors()</item>
/// <item><strong>Synthesis Functions:</strong> clog2() - ceiling of log base 2</item>
/// </list>
/// <para><strong>v0.13.0 Improvements:</strong></para>
/// <list type="bullet">
/// <item>Enhanced ToInteger() with safety checks for 'U', 'X', 'Z' meta-values</item>
/// <item>Added GetIntValue() and GetStringValue() helper methods for robust argument handling</item>
/// <item>Improved error handling with try-catch blocks</item>
/// </list>
/// <para><strong>Usage:</strong></para>
/// <para>
/// These functions are automatically recognized during expression synthesis and executed
/// via the EvaluateFunction() dispatcher method. They integrate seamlessly with the
/// Operation class for use in signal assignments and conditions.
/// </para>
/// </remarks>
public static class StandardFunctions
{
    /// <summary>
    /// Checks if a string contains VHDL meta-values ('U', 'X', 'Z', 'W', 'L', 'H', '-').
    /// </summary>
    private static bool ContainsMetaValues(string s) =>
        s.IndexOfAny(['U', 'X', 'Z', 'W', 'L', 'H', '-']) >= 0;

    /// <summary>
    /// IEEE standard rising_edge function.
    /// Returns true if signal has an event and current value is '1'.
    /// Usage: rising_edge(clk)
    /// </summary>
    public static bool RisingEdge(Variable signal)
    {
        var handler = SignalAttributeHandler.Instance;

        // Check if signal had an event
        if (!handler.GetEvent(signal))
            return false;

        // Check if current value is '1'
        string currentValue = signal.Value?.ToString()?.Replace("'", "").Replace("\"", "") ?? "";
        return currentValue == "1";
    }

    /// <summary>
    /// IEEE standard falling_edge function.
    /// Returns true if signal has an event and current value is '0'.
    /// Usage: falling_edge(clk)
    /// </summary>
    public static bool FallingEdge(Variable signal)
    {
        var handler = SignalAttributeHandler.Instance;

        // Check if signal had an event
        if (!handler.GetEvent(signal))
            return false;

        // Check if current value is '0'
        string currentValue = signal.Value?.ToString()?.Replace("'", "").Replace("\"", "") ?? "";
        return currentValue == "0";
    }

    /// <summary>
    /// Converts an integer to std_logic_vector.
    /// </summary>
    public static string ToStdLogicVector(int value, int width)
    {
        string binary = Convert.ToString(value, 2).PadLeft(width, '0');
        if (binary.Length > width)
            binary = binary.Substring(binary.Length - width);
        return binary;
    }

    /// <summary>
    /// Converts std_logic_vector to integer.
    /// v0.13.0: Enhanced with safety checks for uninitialized and invalid values.
    /// </summary>
    /// <param name="slv">The std_logic_vector string to convert (e.g., "1010", "U", "XXXX")</param>
    /// <returns>Integer representation, or 0 for invalid/uninitialized values</returns>
    /// <remarks>
    /// VHDL std_logic can have meta-values like 'U' (uninitialized), 'X' (unknown), 'Z' (high-impedance).
    /// These are treated as 0 for simulation purposes.
    /// Supports both binary strings ("1010") and decimal strings ("10").
    /// </remarks>
    public static int ToInteger(string slv)
    {
        string cleaned = slv.Replace("\"", "").Replace("'", "").Trim();

        // v0.13.0 Fix: Handle empty or uninitialized values safely
        if (string.IsNullOrEmpty(cleaned))
            return 0;

        // v0.13.0 Fix: Handle VHDL meta-values 'U' (uninitialized), 'X' (unknown), 'Z' (high-impedance)
        // These cannot be converted to integers, so treat as 0
        if (cleaned.Contains('U') || cleaned.Contains('X') || cleaned.Contains('Z'))
            return 0;

        // v3.2 (#29) ordering fix:
        //   Binary first when the string is multi-character and all 0s/1s.
        //   The previous order tried decimal-parse first, which silently
        //   converted "01010101" to 1,010,101 instead of 85 — every
        //   STD_LOGIC_VECTOR-shaped value carrying real bit patterns
        //   came out wrong by orders of magnitude.
        //   Single-char "0"/"1" stays in either branch (binary == decimal == 0/1).
        if (cleaned.Length > 1 && cleaned.All(c => c == '0' || c == '1'))
        {
            try { return Convert.ToInt32(cleaned, 2); }
            catch { /* fall through to decimal */ }
        }

        // Decimal interpretation (integer literals, signed values, etc.).
        if (int.TryParse(cleaned, out int decimalValue))
            return decimalValue;

        // Final fallback: treat as binary even at length 1, with a try-
        // catch in case the string contains other characters.
        try { return Convert.ToInt32(cleaned, 2); }
        catch { return 0; }
    }

    /// <summary>
    /// Converts unsigned to integer.
    /// Handles VHDL meta-values ('U', 'X', 'Z') by returning 0.
    /// </summary>
    public static int ToIntegerUnsigned(string slv)
    {
        string cleaned = slv.Replace("\"", "").Replace("'", "").Trim();
        if (string.IsNullOrEmpty(cleaned)) return 0;
        if (ContainsMetaValues(cleaned)) return 0;
        try { return Convert.ToInt32(cleaned, 2); }
        catch { return 0; }
    }

    /// <summary>
    /// Converts signed to integer (2's complement).
    /// Handles VHDL meta-values ('U', 'X', 'Z') by returning 0.
    /// </summary>
    public static int ToIntegerSigned(string slv)
    {
        string cleaned = slv.Replace("\"", "").Replace("'", "").Trim();
        if (string.IsNullOrEmpty(cleaned)) return 0;
        if (ContainsMetaValues(cleaned)) return 0;

        try
        {
            // Check sign bit
            if (cleaned[0] == '1')
            {
                // Negative number - 2's complement
                int magnitude = Convert.ToInt32(cleaned, 2);
                return magnitude - (1 << cleaned.Length);
            }
            return Convert.ToInt32(cleaned, 2);
        }
        catch { return 0; }
    }

    /// <summary>
    /// Resizes a std_logic_vector/unsigned/signed to new width.
    /// </summary>
    public static string Resize(string value, int newWidth)
    {
        string cleaned = value.Replace("\"", "").Replace("'", "");

        if (cleaned.Length >= newWidth)
        {
            // Truncate from the left
            return cleaned.Substring(cleaned.Length - newWidth);
        }
        else
        {
            // Extend with zeros (for unsigned) or sign extend (for signed)
            return cleaned.PadLeft(newWidth, '0');
        }
    }

    /// <summary>
    /// Sign-extends a vector to new width.
    /// </summary>
    public static string SignExtend(string value, int newWidth)
    {
        string cleaned = value.Replace("\"", "").Replace("'", "");

        if (cleaned.Length >= newWidth)
        {
            return cleaned.Substring(cleaned.Length - newWidth);
        }

        char signBit = cleaned.Length > 0 ? cleaned[0] : '0';
        return cleaned.PadLeft(newWidth, signBit);
    }

    /// <summary>
    /// Performs logical shift left on a vector.
    /// </summary>
    public static string ShiftLeft(string value, int amount)
    {
        string cleaned = value.Replace("\"", "").Replace("'", "");
        if (amount >= cleaned.Length) return new string('0', cleaned.Length);

        return cleaned.Substring(amount) + new string('0', amount);
    }

    /// <summary>
    /// Performs logical shift right on a vector.
    /// </summary>
    public static string ShiftRight(string value, int amount)
    {
        string cleaned = value.Replace("\"", "").Replace("'", "");
        if (amount >= cleaned.Length) return new string('0', cleaned.Length);

        return new string('0', amount) + cleaned.Substring(0, cleaned.Length - amount);
    }

    /// <summary>
    /// Performs arithmetic shift right (preserves sign bit).
    /// </summary>
    public static string ShiftRightArithmetic(string value, int amount)
    {
        string cleaned = value.Replace("\"", "").Replace("'", "");
        if (cleaned.Length == 0) return cleaned;

        char signBit = cleaned[0];
        if (amount >= cleaned.Length) return new string(signBit, cleaned.Length);

        return new string(signBit, amount) + cleaned.Substring(0, cleaned.Length - amount);
    }

    /// <summary>
    /// Rotates bits left.
    /// </summary>
    public static string RotateLeft(string value, int amount)
    {
        string cleaned = value.Replace("\"", "").Replace("'", "");
        if (cleaned.Length == 0) return cleaned;

        amount = amount % cleaned.Length;
        return cleaned.Substring(amount) + cleaned.Substring(0, amount);
    }

    /// <summary>
    /// Rotates bits right.
    /// </summary>
    public static string RotateRight(string value, int amount)
    {
        string cleaned = value.Replace("\"", "").Replace("'", "");
        if (cleaned.Length == 0) return cleaned;

        amount = amount % cleaned.Length;
        return cleaned.Substring(cleaned.Length - amount) + cleaned.Substring(0, cleaned.Length - amount);
    }

    /// <summary>
    /// Vector addition with carry.
    /// </summary>
    public static string AddVectors(string a, string b)
    {
        string cleanA = a.Replace("\"", "").Replace("'", "");
        string cleanB = b.Replace("\"", "").Replace("'", "");

        int maxLen = Math.Max(cleanA.Length, cleanB.Length);
        // Handle meta-values: treat as zero
        if (ContainsMetaValues(cleanA)) cleanA = new string('0', maxLen);
        if (ContainsMetaValues(cleanB)) cleanB = new string('0', maxLen);
        cleanA = cleanA.PadLeft(maxLen, '0');
        cleanB = cleanB.PadLeft(maxLen, '0');

        try
        {
            int valA = Convert.ToInt32(cleanA, 2);
            int valB = Convert.ToInt32(cleanB, 2);
            int result = valA + valB;
            return ToStdLogicVector(result, maxLen);
        }
        catch { return new string('0', maxLen); }
    }

    /// <summary>
    /// Vector subtraction.
    /// Handles VHDL meta-values ('U', 'X', 'Z') by treating them as zero.
    /// </summary>
    public static string SubtractVectors(string a, string b)
    {
        string cleanA = a.Replace("\"", "").Replace("'", "");
        string cleanB = b.Replace("\"", "").Replace("'", "");

        int maxLen = Math.Max(cleanA.Length, cleanB.Length);
        // Handle meta-values: treat as zero
        if (ContainsMetaValues(cleanA)) cleanA = new string('0', maxLen);
        if (ContainsMetaValues(cleanB)) cleanB = new string('0', maxLen);
        cleanA = cleanA.PadLeft(maxLen, '0');
        cleanB = cleanB.PadLeft(maxLen, '0');

        try
        {
            int valA = Convert.ToInt32(cleanA, 2);
            int valB = Convert.ToInt32(cleanB, 2);
            int result = valA - valB;

            // Handle negative results (2's complement)
            if (result < 0)
                result += (1 << maxLen);

            return ToStdLogicVector(result, maxLen);
        }
        catch { return new string('0', maxLen); }
    }

    /// <summary>
    /// Computes ceiling of log base 2 (number of bits needed to represent val).
    /// Common VHDL/synthesis function.
    /// </summary>
    public static int Clog2(int val)
    {
        if (val <= 1) return 0;
        int result = 0;
        int temp = val - 1;
        while (temp > 0)
        {
            result++;
            temp >>= 1;
        }
        return result;
    }

    /// <summary>
    /// Checks if a function name is a standard function.
    /// </summary>
    public static bool IsStandardFunction(string name)
    {
        return name.ToLower() switch
        {
            "rising_edge" => true,
            "falling_edge" => true,
            "to_integer" => true,
            "to_unsigned" => true,
            "to_signed" => true,
            "unsigned" => true,
            "signed" => true,
            "resize" => true,
            "shift_left" => true,
            "shift_right" => true,
            "rotate_left" => true,
            "rotate_right" => true,
            "conv_std_logic_vector" => true,
            "conv_integer" => true,
            "std_logic_vector" => true,
            "clog2" => true,
            _ => false
        };
    }

    /// <summary>
    /// Checks if a token could be a function call (for user-defined functions).
    /// Returns true if the name looks like an identifier and is followed by parentheses.
    /// </summary>
    public static bool CouldBeFunction(string name)
    {
        // Check if name matches identifier pattern (starts with letter/underscore, alphanumeric)
        if (string.IsNullOrEmpty(name))
            return false;
        char first = name[0];
        if (!char.IsLetter(first) && first != '_')
            return false;
        return name.All(c => char.IsLetterOrDigit(c) || c == '_');
    }

    /// <summary>
    /// Evaluates a standard function call.
    /// </summary>
    public static object EvaluateFunction(string functionName, object[] arguments, HashSet<Variable> variables)
    {
        switch (functionName.ToLower())
        {
            case "rising_edge":
                if (arguments.Length >= 1)
                {
                    var signal = GetVariableFromArg(arguments[0], variables);
                    if (!ReferenceEquals(signal, null))
                        return RisingEdge(signal);
                }
                return false;

            case "falling_edge":
                if (arguments.Length >= 1)
                {
                    var signal = GetVariableFromArg(arguments[0], variables);
                    if (!ReferenceEquals(signal, null))
                        return FallingEdge(signal);
                }
                return false;

            case "to_integer":
            case "conv_integer":
                if (arguments.Length >= 1)
                {
                    string val = GetStringValue(arguments[0], variables);
                    return ToInteger(val);
                }
                return 0;

            case "resize":
                if (arguments.Length >= 2)
                {
                    string val = GetStringValue(arguments[0], variables);
                    int width = GetIntValue(arguments[1], variables);
                    return Resize(val, width);
                }
                return "";

            case "shift_left":
                if (arguments.Length >= 2)
                {
                    string val = GetStringValue(arguments[0], variables);
                    int amount = GetIntValue(arguments[1], variables);
                    return ShiftLeft(val, amount);
                }
                return "";

            case "shift_right":
                if (arguments.Length >= 2)
                {
                    string val = GetStringValue(arguments[0], variables);
                    int amount = GetIntValue(arguments[1], variables);
                    return ShiftRight(val, amount);
                }
                return "";

            // numeric_std rotate functions — declared in the bundled stub
            // but previously absent from the runtime evaluator, so any user
            // code that called rotate_left/rotate_right would receive a
            // null result and downstream "datatypes do not match" failures
            // when the outer expression tried to consume that null.
            case "rotate_left":
                if (arguments.Length >= 2)
                {
                    string val = GetStringValue(arguments[0], variables);
                    int amount = GetIntValue(arguments[1], variables);
                    return RotateLeft(val, amount);
                }
                return "";

            case "rotate_right":
                if (arguments.Length >= 2)
                {
                    string val = GetStringValue(arguments[0], variables);
                    int amount = GetIntValue(arguments[1], variables);
                    return RotateRight(val, amount);
                }
                return "";

            case "to_unsigned":
            case "unsigned":
            case "conv_std_logic_vector":
            case "std_logic_vector":
                if (arguments.Length >= 2)
                {
                    int val = GetIntValue(arguments[0], variables);
                    int width = GetIntValue(arguments[1], variables);
                    return ToStdLogicVector(val, width);
                }
                else if (arguments.Length >= 1)
                {
                    return GetStringValue(arguments[0], variables);
                }
                return "";

            case "to_signed":
            case "signed":
                if (arguments.Length >= 2)
                {
                    int val = GetIntValue(arguments[0], variables);
                    int width = GetIntValue(arguments[1], variables);
                    return ToStdLogicVector(val, width);
                }
                else if (arguments.Length >= 1)
                {
                    return GetStringValue(arguments[0], variables);
                }
                return "";

            case "clog2":
                if (arguments.Length >= 1)
                {
                    int val = GetIntValue(arguments[0], variables);
                    return Clog2(val);
                }
                return 0;

            // Phase 5 (2026-07-22): std_logic_arith arithmetic aliases.
            // Previously registered by name only in the bundled stub, so
            // callers received null! and the caller-coerced "0" masqueraded
            // as valid data. All six delegate to existing helpers.
            case "shl":
                if (arguments.Length >= 2)
                    return ShiftLeft(GetStringValue(arguments[0], variables),
                                     GetIntValue(arguments[1], variables));
                return "";

            case "shr":
                if (arguments.Length >= 2)
                    return ShiftRight(GetStringValue(arguments[0], variables),
                                      GetIntValue(arguments[1], variables));
                return "";

            case "ext": // zero-extend
                if (arguments.Length >= 2)
                    return Resize(GetStringValue(arguments[0], variables),
                                  GetIntValue(arguments[1], variables));
                return "";

            case "sxt": // sign-extend — same code path as Resize;
                       // signed shape is tracked separately.
                if (arguments.Length >= 2)
                    return Resize(GetStringValue(arguments[0], variables),
                                  GetIntValue(arguments[1], variables));
                return "";

            case "conv_unsigned":
                if (arguments.Length >= 2)
                    return ToStdLogicVector(GetIntValue(arguments[0], variables),
                                            GetIntValue(arguments[1], variables));
                return "";

            case "conv_signed":
                if (arguments.Length >= 2)
                    return ToStdLogicVector(GetIntValue(arguments[0], variables),
                                            GetIntValue(arguments[1], variables));
                return "";

            // Phase 5: math_real transcendentals. Constant-init contexts
            // only (VHDL prohibits math_real inside synthesized designs);
            // return the result formatted as a string so it flows through
            // GetIntValue / GetStringValue as expected downstream.
            case "floor":
                if (arguments.Length >= 1)
                    return ((long)Math.Floor(GetDoubleValue(arguments[0], variables))).ToString();
                return "0";

            case "ceil":
                if (arguments.Length >= 1)
                    return ((long)Math.Ceiling(GetDoubleValue(arguments[0], variables))).ToString();
                return "0";

            case "round":
                if (arguments.Length >= 1)
                    return ((long)Math.Round(GetDoubleValue(arguments[0], variables))).ToString();
                return "0";

            case "sqrt":
                if (arguments.Length >= 1)
                {
                    double x = GetDoubleValue(arguments[0], variables);
                    if (x < 0) return "0";
                    return Math.Sqrt(x).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                return "0";

            // Phase 5: meta-value normalization from std_logic_1164.
            // to_x01: map U/X/Z/W/-/L/H → X/X/X/X/X/0/1; keep 0/1.
            // is_x:   returns true iff any bit is one of the meta-values.
            case "to_x01":
                if (arguments.Length >= 1)
                {
                    string s = GetStringValue(arguments[0], variables);
                    var sb = new System.Text.StringBuilder(s.Length);
                    foreach (char c in s)
                    {
                        sb.Append(c switch {
                            '0' or 'L' or 'l' => '0',
                            '1' or 'H' or 'h' => '1',
                            'U' or 'u' or 'X' or 'x' or 'Z' or 'z'
                                or 'W' or 'w' or '-' => 'X',
                            _ => c,
                        });
                    }
                    return sb.ToString();
                }
                return "";

            case "is_x":
                if (arguments.Length >= 1)
                {
                    string s = GetStringValue(arguments[0], variables);
                    foreach (char c in s)
                    {
                        if (c is 'U' or 'u' or 'X' or 'x' or 'Z' or 'z'
                             or 'W' or 'w' or '-')
                            return true;
                    }
                    return false;
                }
                return false;

            default:
                // Phase 4 (2026-07-22): surface a diagnostic instead of
                // returning null! silently. Callers coerce null → "0"/""
                // downstream, but the user never sees WHY their function
                // returned a stub. IEEE-STUB entries appear in the shared
                // SkipDiagnosticLog alongside VHD-PROC-PARTIAL and friends.
                SkipDiagnosticLog.Instance.AddWarning(
                    "IEEE-STUB",
                    functionName,
                    $"Function '{functionName}' is name-registered but not implemented — " +
                    "returning \"0\" placeholder. Add a case in " +
                    "StandardFunctions.EvaluateFunction to implement.");
                return "0";
        }
    }

    /// <summary>
    /// Gets a Variable from an argument.
    /// </summary>
    private static Variable? GetVariableFromArg(object arg, HashSet<Variable> variables)
    {
        if (arg is Variable v)
            return v;

        if (arg is string name)
        {
            return variables.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }

    /// <summary>
    /// Gets a string value from an argument.
    /// </summary>
    private static string GetStringValue(object arg, HashSet<Variable> variables)
    {
        // Bench bug #20: a DeferredIndex arg (slice / single-bit access on
        // a Variable) carries live source + bounds. Evaluate every call
        // so `unsigned(mbr_reg(7 downto 0))` sees the current slice value.
        if (arg is Models.DeferredIndex di)
            return di.Evaluate().Value?.ToString() ?? "";

        // Sub-expression inside a function-call arg (2026-07-05 DIP-adder
        // fix). Re-fold live so `std_logic_vector(unsigned(a)+unsigned(b))`
        // tracks a/b changes tick-by-tick.
        if (arg is Models.DeferredExpression de)
            return de.Evaluate().Value?.ToString() ?? "";

        if (arg is Variable v)
            return v.Value?.ToString() ?? "";

        if (arg is string s)
        {
            var variable = variables.Where(x => !ReferenceEquals(x, null))
                .FirstOrDefault(x => !string.IsNullOrEmpty(x.Name) && x.Name.Equals(s, StringComparison.OrdinalIgnoreCase));
            if (!ReferenceEquals(variable, null))
                return variable.Value?.ToString() ?? "";
            return s;
        }

        return arg?.ToString() ?? "";
    }

    /// <summary>
    /// Gets an integer value from an argument.
    /// </summary>
    /// <summary>
    /// Phase 5 (2026-07-22): read an argument as a double. Used by
    /// math_real transcendentals (floor/ceil/round/sqrt). Accepts:
    /// - a numeric literal string via <c>double.TryParse</c>,
    /// - an integer via <see cref="GetIntValue"/> as fallback,
    /// - a <see cref="Variable"/> whose Value is a numeric string.
    /// Falls back to 0.0 if none parse.
    /// </summary>
    private static double GetDoubleValue(object arg, HashSet<Variable> variables)
    {
        string s = GetStringValue(arg, variables);
        if (double.TryParse(s, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double d))
            return d;
        return GetIntValue(arg, variables);
    }

    private static int GetIntValue(object arg, HashSet<Variable> variables)
    {
        // Bench bug #20: evaluate DeferredIndex slices live so callers like
        // `to_integer(unsigned(addr(3 downto 0)))` see the right bits.
        if (arg is Models.DeferredIndex di)
        {
            var ev = di.Evaluate();
            string s2 = ev.Value?.ToString() ?? "";
            if (s2.Length > 1 && s2.All(c => c == '0' || c == '1'))
            {
                try { return System.Convert.ToInt32(s2, 2); } catch { /* fall through */ }
            }
            if (int.TryParse(s2, out int dip)) return dip;
            return 0;
        }

        // Sub-expression arg (see GetStringValue equivalent). Live-fold
        // then interpret the folded string as int with the same binary-
        // then-decimal precedence used for other vector reads.
        if (arg is Models.DeferredExpression de)
        {
            var ev = de.Evaluate();
            string sd = ev.Value?.ToString() ?? "";
            if (sd.Length > 1 && sd.All(c => c == '0' || c == '1'))
            {
                try { return System.Convert.ToInt32(sd, 2); } catch { /* fall through */ }
            }
            if (int.TryParse(sd, out int dd)) return dd;
            return 0;
        }

        if (arg is Variable v)
        {
            if (v.Value is int i)
                return i;
            if (int.TryParse(v.Value?.ToString(), out int parsed))
                return parsed;
            return 0;
        }

        if (arg is int intVal)
            return intVal;

        if (arg is string s)
        {
            var variable = variables.Where(x => !ReferenceEquals(x, null))
                .FirstOrDefault(x => !string.IsNullOrEmpty(x.Name) && x.Name.Equals(s, StringComparison.OrdinalIgnoreCase));
            if (!ReferenceEquals(variable, null))
            {
                if (variable.Value is int vi)
                    return vi;
                if (int.TryParse(variable.Value?.ToString(), out int vp))
                    return vp;
                return 0;
            }
            if (int.TryParse(s, out int sp))
                return sp;
            return 0;
        }

        try
        {
            return Convert.ToInt32(arg);
        }
        catch
        {
            return 0;
        }
    }
}
