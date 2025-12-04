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

        // Handle decimal numbers directly (e.g., from integer literals)
        if (int.TryParse(cleaned, out int decimalValue))
            return decimalValue;

        // Try to convert from binary (e.g., "1010" -> 10)
        // v0.13.0 Fix: Added try-catch for robustness
        try
        {
            return Convert.ToInt32(cleaned, 2);
        }
        catch
        {
            return 0; // Invalid binary format
        }
    }

    /// <summary>
    /// Converts unsigned to integer.
    /// </summary>
    public static int ToIntegerUnsigned(string slv)
    {
        string cleaned = slv.Replace("\"", "").Replace("'", "");
        return Convert.ToInt32(cleaned, 2);
    }

    /// <summary>
    /// Converts signed to integer (2's complement).
    /// </summary>
    public static int ToIntegerSigned(string slv)
    {
        string cleaned = slv.Replace("\"", "").Replace("'", "");
        if (cleaned.Length == 0) return 0;

        // Check sign bit
        if (cleaned[0] == '1')
        {
            // Negative number - 2's complement
            int magnitude = Convert.ToInt32(cleaned, 2);
            return magnitude - (1 << cleaned.Length);
        }
        return Convert.ToInt32(cleaned, 2);
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
        cleanA = cleanA.PadLeft(maxLen, '0');
        cleanB = cleanB.PadLeft(maxLen, '0');

        int valA = Convert.ToInt32(cleanA, 2);
        int valB = Convert.ToInt32(cleanB, 2);
        int result = valA + valB;

        return ToStdLogicVector(result, maxLen);
    }

    /// <summary>
    /// Vector subtraction.
    /// </summary>
    public static string SubtractVectors(string a, string b)
    {
        string cleanA = a.Replace("\"", "").Replace("'", "");
        string cleanB = b.Replace("\"", "").Replace("'", "");

        int maxLen = Math.Max(cleanA.Length, cleanB.Length);
        cleanA = cleanA.PadLeft(maxLen, '0');
        cleanB = cleanB.PadLeft(maxLen, '0');

        int valA = Convert.ToInt32(cleanA, 2);
        int valB = Convert.ToInt32(cleanB, 2);
        int result = valA - valB;

        // Handle negative results (2's complement)
        if (result < 0)
            result += (1 << maxLen);

        return ToStdLogicVector(result, maxLen);
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

            default:
                return null!;
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
    private static int GetIntValue(object arg, HashSet<Variable> variables)
    {
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
