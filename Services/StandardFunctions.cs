using Interpreter.Models;
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
    /// <summary>IEEE <c>rising_edge(s)</c> (see <see cref="SignalAttributeHandler.IsRisingEdge"/>).</summary>
    public static bool RisingEdge(Variable signal) => SignalAttributeHandler.Instance.IsRisingEdge(signal);

    /// <summary>IEEE <c>falling_edge(s)</c> (see <see cref="SignalAttributeHandler.IsFallingEdge"/>).</summary>
    public static bool FallingEdge(Variable signal) => SignalAttributeHandler.Instance.IsFallingEdge(signal);

    /// <summary>
    /// <c>to_integer</c>/<c>conv_integer</c> of <paramref name="arg"/>: a vector read by its
    /// signedness (any width, wrapped to the 32-bit integer as GHDL does); a metavalue
    /// gives 0 with the package's warning (numeric_std or the Synopsys one).
    /// </summary>
    public static int ToInteger(Variable arg, bool synopsys = false)
    {
        if (arg.Type != DATATYPES.STD_LOGIC_VECTOR)
            return Operator.TryInteger(arg, out var n) ? LogicVector.ToInt32(n) : 0;
        string bits = Bits(arg);
        if (bits.Length == 0) return 0;
        if (LogicVector.TryToInteger(bits, Operator.SignednessOf(arg), out var value)) return LogicVector.ToInt32(value);
        if (synopsys)
            ReportBus.Instance.Emit("[assert-warning] CONV_INTEGER: There is an 'U'|'X'|'W'|'Z'|'-' in an arithmetic operand, and it has been converted to 0.");
        else
            Operator.NumericStdWarning("TO_INTEGER: metavalue detected, returning 0");
        return 0;
    }

    /// <summary>The bits of a vector operand (literal quotes removed).</summary>
    private static string Bits(Variable v) => Operator.StripLiteralQuotes(v.Value);

    /// <summary>The storage type of a standard function's result (the single table for every call path).</summary>
    public static DATATYPES ResultType(string functionName, IReadOnlyList<object> args) => functionName.ToLowerInvariant() switch
    {
        "rising_edge" or "falling_edge" or "is_x" or "std_match" or "endfile" => DATATYPES.BOOLEAN,
        "to_integer" or "conv_integer" or "clog2" => DATATYPES.INTEGER,
        "to_x01" or "to_x01z" or "to_ux01" when args.Count > 0 && args[0] is Variable { Type: DATATYPES.STD_LOGIC } => DATATYPES.STD_LOGIC,
        "to_stdulogic" => DATATYPES.STD_LOGIC,
        "to_bit" => DATATYPES.BIT,
        _ => DATATYPES.STD_LOGIC_VECTOR,
    };

    /// <summary>
    /// The result of a standard function call as a value with its type: booleans and
    /// integers keep their value (an integer is a number, never text that could be
    /// misread as bits), anything else is its text; a vector carries its width and
    /// type mark.
    /// </summary>
    public static Variable Result(string functionName, object? result, IReadOnlyList<object> args)
    {
        var type = ResultType(functionName, args);
        object value = type == DATATYPES.BOOLEAN ? (result ?? false)
            : type == DATATYPES.INTEGER && result is int or long ? result!
            : (result?.ToString() ?? "false");
        var v = new Variable($"Function-{functionName}", value, type)
        {
            PortType = PortType.OUT,
            TypeMark = ResultTypeMark(functionName, args),
        };
        if (type == DATATYPES.STD_LOGIC_VECTOR && value is string bits && bits.Length > 0) v.Size = bits.Length;
        return v;
    }

    /// <summary>
    /// The type mark of a standard function's result: the numeric_std/std_logic_arith
    /// conversions name it; <c>resize</c>, the shifts and rotations keep their argument's.
    /// </summary>
    public static string? ResultTypeMark(string functionName, IReadOnlyList<object> args) => functionName.ToLowerInvariant() switch
    {
        "unsigned" or "to_unsigned" or "conv_unsigned" => "unsigned",
        "signed" or "to_signed" or "conv_signed" => "signed",
        "std_logic_vector" or "conv_std_logic_vector" or "to_stdlogicvector" or "ext" or "sxt" => "std_logic_vector",
        "resize" or "shift_left" or "shift_right" or "rotate_left" or "rotate_right" or "shl" or "shr"
            when args.Count > 0 && args[0] is Variable v => v.TypeMark,
        _ => null,
    };

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
    /// v3.1.5 IEEE functions evaluated only while simulating (never folded at analysis): the
    /// metavalue conversions/predicates, bit ↔ std_ulogic and the std_logic_arith helpers.
    /// </summary>
    public static readonly HashSet<string> LiveFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "to_x01", "to_x01z", "to_ux01", "to_01", "is_x", "std_match",
        "to_stdulogic", "to_stdlogicvector", "to_stdulogicvector", "to_bit", "to_bitvector",
        "shl", "shr", "ext", "sxt", "conv_unsigned", "conv_signed", "endfile",
    };

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
            var other => LiveFunctions.Contains(other)
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

            case "endfile":
                return arguments.Length >= 1 && ArgVariable(arguments[0], variables) is { } file && TextioFiles.EndOfFile(file);

            case "to_integer":
            case "conv_integer":
                return arguments.Length >= 1
                    ? ToInteger(ArgVariable(arguments[0], variables), functionName.Equals("conv_integer", StringComparison.OrdinalIgnoreCase))
                    : 0;

            case "resize":
                if (arguments.Length >= 2)
                {
                    var v = ArgVariable(arguments[0], variables);
                    return LogicVector.Resize(Bits(v), GetIntValue(arguments[1], variables), Operator.SignednessOf(v));
                }
                return "";

            // numeric_std shifts and rotations (shift_right of a signed value keeps the sign).
            case "shift_left":
            case "shift_right":
            case "rotate_left":
            case "rotate_right":
                if (arguments.Length >= 2)
                {
                    var v = ArgVariable(arguments[0], variables);
                    return LogicVector.Shift(Bits(v), functionName, GetIntValue(arguments[1], variables), Operator.SignednessOf(v));
                }
                return "";

            case "to_unsigned":
            case "to_signed":
            case "conv_unsigned":
            case "conv_signed":
            case "unsigned":
            case "signed":
            case "conv_std_logic_vector":
            case "std_logic_vector":
                if (arguments.Length >= 2)
                {
                    var v = ArgVariable(arguments[0], variables);
                    int width = GetIntValue(arguments[1], variables);
                    return v.Type == DATATYPES.STD_LOGIC_VECTOR
                        ? LogicVector.Resize(Bits(v), width, Operator.SignednessOf(v))
                        : LogicVector.FromInteger(Operator.TryInteger(v, out var n) ? n : 0, width);
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
            // std_logic_arith: shl/shr (shr keeps the sign of a signed argument),
            // ext/sxt (zero/sign extension); conv_unsigned/conv_signed are with to_unsigned.
            case "shl":
            case "shr":
                if (arguments.Length >= 2)
                {
                    var v = ArgVariable(arguments[0], variables);
                    return LogicVector.Shift(Bits(v), functionName.Equals("shl", StringComparison.OrdinalIgnoreCase) ? "shift_left" : "shift_right",
                        GetIntValue(arguments[1], variables), Operator.SignednessOf(v));
                }
                return "";

            case "ext":
            case "sxt":
                if (arguments.Length >= 2)
                    return LogicVector.Resize(Bits(ArgVariable(arguments[0], variables)), GetIntValue(arguments[1], variables),
                        functionName.Equals("sxt", StringComparison.OrdinalIgnoreCase) ? Signedness.Signed : Signedness.Unsigned);
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

            // IEEE 1164 / numeric_std metavalue functions (StdLogic holds the tables).
            case "to_x01":
            case "to_x01z":
            case "to_ux01":
                if (arguments.Length >= 1)
                {
                    string name = functionName.ToLowerInvariant();
                    return new string(GetStringValue(arguments[0], variables).Select(c => StdLogic.Norm(c) switch
                    {
                        'Z' when name == "to_x01z" => 'Z',
                        'U' when name == "to_ux01" => 'U',
                        _ => StdLogic.ToX01(c),
                    }).ToArray());
                }
                return "";

            // bit ↔ std_ulogic conversions: to_bit maps 1/H to '1', anything else to '0'.
            case "to_stdulogic":
            case "to_stdlogicvector":
            case "to_stdulogicvector":
                return arguments.Length >= 1 ? GetStringValue(arguments[0], variables) : "";
            case "to_bit":
            case "to_bitvector":
                return arguments.Length >= 1
                    ? new string(GetStringValue(arguments[0], variables).Select(c => StdLogic.ToX01(c) == '1' ? '1' : '0').ToArray())
                    : "";

            case "to_01":
                return arguments.Length >= 1 ? StdLogic.To01(GetStringValue(arguments[0], variables)) : "";

            case "is_x":
                return arguments.Length >= 1 && StdLogic.IsX(GetStringValue(arguments[0], variables));

            case "std_match":
                if (arguments.Length >= 2)
                {
                    string a = GetStringValue(arguments[0], variables), b = GetStringValue(arguments[1], variables);
                    return a.Length == b.Length && a.Zip(b).All(p => StdLogic.Match(p.First, p.Second));
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

    /// <summary>
    /// An argument as a value with its type: a variable, a deferred read or call
    /// (evaluated now), a name in scope, an integer literal, or other literal text.
    /// </summary>
    private static Variable ArgVariable(object arg, HashSet<Variable> variables)
    {
        switch (arg)
        {
            case Variable v: return v;
            case int i: return new Variable("__ArgInt", i, DATATYPES.INTEGER);
            case string s:
                if (variables.TryGetValue(new Variable { Name = s }, out var named)) return named;
                return long.TryParse(s, out long n)
                    ? new Variable("__ArgInt", n, DATATYPES.INTEGER)
                    : new Variable("__ArgRaw", s.Replace("\"", ""), DATATYPES.STD_LOGIC_VECTOR);
            default:
                return Operation.ToArgVariablePublic(arg);
        }
    }

    /// <summary>An integer argument (a width, a shift count…); a vector argument is read unsigned.</summary>
    private static int GetIntValue(object arg, HashSet<Variable> variables)
    {
        if (arg is int literal) return literal;
        if (arg is Variable { Value: int known }) return known;
        var v = ArgVariable(arg, variables);
        if (v.Type == DATATYPES.STD_LOGIC_VECTOR && LogicVector.TryToInteger(Bits(v), Signedness.Unsigned, out var bits))
            return LogicVector.ToInt32(bits);
        return Operator.TryInteger(v, out var n) ? LogicVector.ToInt32(n) : 0;
    }
}
