using System.Collections;
using System.Text.RegularExpressions;
using Interpreter.Interfaces;
using Interpreter.Models;
using Parser.Models;
using static Parser.Models.Constants;

namespace Interpreter.Services;

/// <summary>
/// Represents an operator.
/// </summary>
public class Operator : IOperation, ILogicOperation, IArithOperation{
    /// <summary>
    /// Gets the type of the operator.
    /// </summary>
    public OperatorType OperatorType { get; private set; }

    /// <summary>
    /// Gets the precedence of the operator.
    /// </summary>
    public object Precedence { get; private set; } = new();
    private readonly Regex _assignationRegex, _arithRegex, _logicRegex, _comparationRegex;

    /// <summary>
    /// Gets the name of the operator.
    /// </summary>
    public string OperatorName { get; private set; } = string.Empty;

    private Func<char, char, char>? _logicFunction;
    private bool _logicResolved;

    /// <summary>The IEEE 1164 function of this logical operator (resolved once; the name never changes).</summary>
    private Func<char, char, char>? LogicFunction
    {
        get
        {
            if (!_logicResolved) (_logicFunction, _logicResolved) = (StdLogic.Binary(OperatorName), true);
            return _logicFunction;
        }
    }

    /// <summary>True for a logical operator used as a VHDL-2008 unary reduction (<c>and v</c>).</summary>
    public bool IsReduction { get; private set; }

    /// <summary>This logical operator as a unary reduction: one operand, highest precedence.</summary>
    public Operator AsReduction()
    {
        IsReduction = true;
        Precedence = PrecedencesLogicOperators.NOT;
        return this;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="Operator"/> class.
    /// </summary>
    /// <param name="Operator">The name of the operator.</param>
    public Operator(string Operator){
        this.OperatorName = Operator;
        RegexOptions options = RegexOptions.Compiled | RegexOptions.IgnoreCase;
        _assignationRegex = new(ASSIGNATION_OPERATORS, options);
        _arithRegex = new(ARITH_OPERATORS, options);
        _comparationRegex = new(COMPARATION_OPERATORS, options);
        _logicRegex = new(LOGIC_OPERATORS, options);
        this.OperatorType = DefineOperatorType(Operator);
        DefinePrecedence(Operator);
    }

    /// <summary>
    /// Defines the precedence of the operator.
    /// </summary>
    /// <param name="Operator">The name of the operator.</param>
    private void DefinePrecedence(string Operator){
        switch(this.OperatorType){
            case OperatorType.LOGIC_OPERATION:
                Operator = Operator.ToLower();
                switch(Operator){
                    case "and":
                        Precedence = PrecedencesLogicOperators.AND;
                    break;
                    case "nand":
                        Precedence = PrecedencesLogicOperators.NAND;
                    break;
                    case "xor":
                        Precedence = PrecedencesLogicOperators.XOR;
                    break;
                    case "xnor":
                        Precedence = PrecedencesLogicOperators.XNOR;
                    break;
                    case "or":
                        Precedence = PrecedencesLogicOperators.OR;
                    break;
                    case "nor":
                        Precedence = PrecedencesLogicOperators.NOR;
                    break;
                    case "abs":
                        Precedence = PrecedencesLogicOperators.ABS;
                    break;
                    case "not":
                        Precedence = PrecedencesLogicOperators.NOT;
                    break;
                    case "sll":
                        Precedence = PrecedencesLogicOperators.SLL;
                    break;
                    case "srl":
                        Precedence = PrecedencesLogicOperators.SRL;
                    break;
                    case "sla":
                        Precedence = PrecedencesLogicOperators.SLA;
                    break;
                    case "sra":
                        Precedence = PrecedencesLogicOperators.SRA;
                    break;
                    case "rol":
                        Precedence = PrecedencesLogicOperators.ROL;
                    break;
                    case "ror":
                        Precedence = PrecedencesLogicOperators.ROR;
                    break;
                    case "?=":
                    case "?/=":
                        Precedence = PrecedencesLogicOperators.COMPARATION;
                    break;
                    case "??":
                        Precedence = PrecedencesLogicOperators.NOT;
                    break;
                }
            break;
            case OperatorType.ARITH_OPERATION:
                Operator = Operator.ToLower();
                switch(Operator){
                    case "+":
                        Precedence = PrecedencesArithOperators.ADD;
                    break;
                    case "-":
                        Precedence = PrecedencesArithOperators.SUBSTRACTION;
                    break;
                    case "*":
                        Precedence = PrecedencesArithOperators.MULTIPLICATION;
                    break;
                    case "**":
                        Precedence = PrecedencesArithOperators.UNARY; // Power has high precedence
                    break;
                    case "/":
                        Precedence = PrecedencesArithOperators.DIVISION;
                    break;
                    case "mod":
                    case "rem":
                        Precedence = PrecedencesArithOperators.MULTIPLICATION;
                    break;
                }
            break;
            case OperatorType.COMPARATION:
                Precedence = PrecedencesLogicOperators.COMPARATION;
            break;
            case OperatorType.UNKNOWN:
                // `&` (concatenation) and the parens land here — they don't
                // match any of the four operator regexes. `&` needs a
                // precedence at adder-level so a chain like
                // `'0' & x + y` orders correctly; `(` and `)` get a low
                // precedence value but ShuntingYard skips `(` regardless,
                // so the value doesn't matter for them.
                Precedence = Operator == "&"
                    ? PrecedencesLogicOperators.CONCAT
                    : PrecedencesLogicOperators.PARENTHESIS;
            break;
            case OperatorType.ASSIGNATION:
                Precedence = PrecedencesLogicOperators.ASSIGNATION;
            break;
            default:
                throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Defines the type of the operator.
    /// </summary>
    /// <param name="Operator">The name of the operator.</param>
    /// <returns>The type of the operator.</returns>
    private OperatorType DefineOperatorType(string Operator) =>
        _comparationRegex.IsMatch(Operator) ?
            OperatorType.COMPARATION :
        _assignationRegex.IsMatch(Operator) ?
            OperatorType.ASSIGNATION :
        _arithRegex.IsMatch(Operator) ?
            OperatorType.ARITH_OPERATION :
        _logicRegex.IsMatch(Operator) ?
            OperatorType.LOGIC_OPERATION :
        
            OperatorType.UNKNOWN;

    /// <summary>
    /// Executes the operation.
    /// </summary>
    /// <returns>The result of the operation.</returns>
    public object Execute() => throw new NotImplementedException();

    /// <summary>
    /// Executes the operation.
    /// </summary>
    /// <param name="array">The array of items.</param>
    /// <param name="index">The index of the item.</param>
    /// <returns>The result of the operation.</returns>
    public object Execute(object[] array, int index) => throw new NotImplementedException();

    /// <summary>
    /// Executes the operation.
    /// </summary>
    /// <param name="variable">The variable.</param>
    /// <returns>The result of the operation.</returns>
    public object Execute(Variable variable) =>
        (PrecedencesLogicOperators)Precedence == PrecedencesLogicOperators.NOT ?
            SolveLogicOperation(variable) :
            new NotImplementedException();

    /// <summary>
    /// Executes the operation.
    /// </summary>
    /// <param name="leftSideOperator">The left side operator.</param>
    /// <param name="rightSideOperator">The right side operator.</param>
    /// <returns>The result of the operation.</returns>
    public object Execute(Variable leftSideOperator, Variable rightSideOperator){
        // VHDL & (concatenation) — bypass the matching-types check below.
        // The whole point of & is to glue STD_LOGIC and STD_LOGIC_VECTOR
        // operands together: `'0' & unsigned(a)` widens an N-bit vector
        // by 1 bit, `r(6 downto 0) & din` shifts a register left, etc.
        // The result is always a STD_LOGIC_VECTOR-shaped bit string;
        // the wrapping caller (Operation.Execute / ScheduledOperation.Execute)
        // is responsible for retagging the resulting Variable's Type to
        // STD_LOGIC_VECTOR — see the OperatorName == "&" branch there.
        if (string.Equals(OperatorName, "&", StringComparison.Ordinal))
        {
            return ConcatenateBits(leftSideOperator, rightSideOperator);
        }

        // v0.13.0: Relax type checking when one operand is a stub (undeclared identifier)
        // This allows parsing to continue even with missing package definitions
        // v0.16.1: Add type coercion support for INTEGER <-> STD_LOGIC_VECTOR
        if(leftSideOperator.Type != rightSideOperator.Type &&
           !leftSideOperator.IsStub && !rightSideOperator.IsStub)
        {
            // Try to coerce types before throwing error
            if (!TryCoerceTypes(ref leftSideOperator, ref rightSideOperator))
            {
                throw ParserException.TypeMismatch(
                    leftSideOperator.Type.ToString(),
                    rightSideOperator.Type.ToString(),
                    null);
            }
        }
        object result = new();
        switch(OperatorType){
            case OperatorType.ASSIGNATION:
                result = rightSideOperator.Value;
            break;
            case OperatorType.COMPARATION:
                result = SolveComparation(leftSideOperator, rightSideOperator);
            break;
            case OperatorType.LOGIC_OPERATION:
                switch(leftSideOperator.Type){
                    case DATATYPES.STD_LOGIC:
                    case DATATYPES.BIT:
                        result = SolveLogicOperation(leftSideOperator, rightSideOperator);
                    break;
                    case DATATYPES.STD_LOGIC_VECTOR:
                        result = SolveVectorLogicOperation(leftSideOperator, rightSideOperator);
                    break;
                    case DATATYPES.BOOLEAN:
                        // Bug #17: AND/OR/etc over BOOLEAN operands
                        // (post-comparison) previously fell through to
                        // `result = new object()`. Funnel through bool ops.
                        result = SolveBooleanLogicOperation(leftSideOperator, rightSideOperator);
                    break;
                }

            break;
            case OperatorType.ARITH_OPERATION:
                result = SolveArithOperation(leftSideOperator, rightSideOperator);
                break;
            default:
                throw new NotImplementedException();
        }
        return result;
    }

    /// <summary>
    /// Solves a comparation operation.
    /// </summary>
    /// <param name="var1">The first variable.</param>
    /// <param name="var2">The second variable.</param>
    /// <returns>The result of the comparation.</returns>
    public object SolveComparation(Variable var1, Variable var2)
    {
        // string / character: ordinal (lexicographic) comparison of the text.
        if (IsText(var1) || IsText(var2))
        {
            return Relational(string.CompareOrdinal(TextOf(var1), TextOf(var2)));
        }
        // v3.3: when EITHER side is a vector, route through the vector
        // comparison path so a literal like `8` doesn't get compared against
        // a binary string like "0010" via int.TryParse — which would parse
        // the vector as decimal 10 instead of the actual unsigned value 2.
        if (var1.Type == DATATYPES.STD_LOGIC_VECTOR || var2.Type == DATATYPES.STD_LOGIC_VECTOR)
        {
            return SolveVectorComparation(var1, var2);
        }
        switch (var1.Type)
        {
            case DATATYPES.BOOLEAN:
                switch (OperatorName.ToLower())
                {
                    case "=":
                    {
                        bool b1Ok = bool.TryParse(var1.Value?.ToString(), out bool b1);
                        bool b2Ok = bool.TryParse(var2.Value?.ToString(), out bool b2);
                        if (!b1Ok || !b2Ok) return false;
                        return b1 == b2;
                    }
                    case "!=":
                    case "/=":
                    {
                        bool b1Ok = bool.TryParse(var1.Value?.ToString(), out bool b1);
                        bool b2Ok = bool.TryParse(var2.Value?.ToString(), out bool b2);
                        if (!b1Ok || !b2Ok) return true;
                        return b1 != b2;
                    }
                    default:
                        throw new Exception($"Operator type do not detected or even supported: {OperatorName}");
                }
            case DATATYPES.STD_LOGIC:
            case DATATYPES.BIT:
            {
                // Enumeration semantics (IEEE 1164): `=` is equality of the value and the
                // relational operators follow the declaration order U X 0 1 Z W L H -.
                string s1 = StripLiteralQuotes(var1.Value), s2 = StripLiteralQuotes(var2.Value);
                if (s1.Length != 1 || s2.Length != 1)
                    return OperatorName is "=" ? s1 == s2 : OperatorName is "/=" or "!=" && s1 != s2;
                return Relational(StdLogic.Values.IndexOf(StdLogic.Norm(s1[0])).CompareTo(StdLogic.Values.IndexOf(StdLogic.Norm(s2[0]))));
            }
            case DATATYPES.INTEGER:
            case DATATYPES.NATURAL:
            {
                // INTEGER/NATURAL: safe parsing with fallback to 0 for meta-values.
                // Bench bug #21 (P2A HALT detect): enum-typed signals are
                // stored as INTEGER but their `.Value` is the literal name
                // string (e.g. "S_HALT"). The legacy code parsed both sides
                // to 0 on failure, so every enum comparison returned true.
                // When both sides fail to parse, fall back to ordinal string
                // equality so `state = S_HALT` resolves correctly.
                string s1 = var1.Value?.ToString() ?? "0";
                string s2 = var2.Value?.ToString() ?? "0";
                bool ok1 = TryInteger(var1, out var i1);
                bool ok2 = TryInteger(var2, out var i2);
                if (!ok1 && !ok2)
                {
                    string op = OperatorName.ToLower();
                    if (op == "=")  return s1.Equals(s2, StringComparison.Ordinal);
                    if (op == "!=" || op == "/=") return !s1.Equals(s2, StringComparison.Ordinal);
                    // Order operators on two non-numeric enum values are
                    // ambiguous (depends on declaration order, which we
                    // don't track). Default to false.
                    return false;
                }
                return Relational(i1.CompareTo(i2));
            }
            default:
                throw new Exception($"Datatype do not detected or even supported: {OperatorName}");
        }
    }

    /// <summary>
    /// Comparison with at least one vector side: numeric (any width, by each
    /// operand's signedness) against an integer, for the relational operators
    /// and for numeric_std operands; the predefined element-wise equality for
    /// two plain vectors.
    /// </summary>
    private object SolveVectorComparation(Variable var1, Variable var2)
    {
        string s1 = StripLiteralQuotes(var1.Value);
        string s2 = StripLiteralQuotes(var2.Value);

        // Numeric comparison: vector against an integer, the relational operators,
        // and `=`/`/=` of numeric_std signed/unsigned operands (resized, H = 1). Plain
        // std_logic_vector equality is the predefined element-wise one.
        bool relational = OperatorName is not ("=" or "/=" or "!=");
        bool numeric = var1.Type != var2.Type || relational
            || IsNumericStd(var1) || IsNumericStd(var2);
        if (!numeric)
            return Relational(string.Equals(s1, s2, StringComparison.Ordinal) ? 0 : 1);

        // numeric_std: a metavalue in a numeric comparison returns FALSE (TRUE for `/=`) with a warning.
        if (HasMeta(var1) || HasMeta(var2))
        {
            bool ne = OperatorName is "/=" or "!=";
            NumericStdWarning($"\"{(ne ? "/=" : OperatorName)}\": metavalue detected, returning {(ne ? "TRUE" : "FALSE")}");
            return ne;
        }
        if (!TryNumber(var1, out var a) || !TryNumber(var2, out var b)) return OperatorName is "/=" or "!=";
        return Relational(a.CompareTo(b));
    }

    /// <summary>True for an operand typed by numeric_std (<c>signed</c>/<c>unsigned</c>).</summary>
    private static bool IsNumericStd(Variable v) => TypeMarks.SignednessOf(v.TypeMark) != Signedness.None;

    /// <summary>
    /// How a vector operand reads as a number: its own type when signed/unsigned,
    /// else the package the design uses for std_logic_vector (<see cref="PackageRegistry.VectorArithmetic"/>).
    /// </summary>
    internal static Signedness SignednessOf(Variable v) =>
        TypeMarks.SignednessOf(v.TypeMark) is var s and not Signedness.None ? s : PackageRegistry.Instance.ArithmeticFor(v.Name);

    /// <summary>The integer value of an integer operand (a number, not bits); false when it is not one (an enum literal).</summary>
    internal static bool TryInteger(Variable v, out System.Numerics.BigInteger value)
    {
        switch (v.Value)
        {
            case int i: value = i; return true;
            case long l: value = l; return true;
            case System.Numerics.BigInteger b: value = b; return true;
            case bool: value = 0; return false;
        }
        return System.Numerics.BigInteger.TryParse(v.Value?.ToString()?.Trim(), out value);
    }

    /// <summary>The number an operand stands for: a vector by its signedness, an integer by its value.</summary>
    internal static bool TryNumber(Variable v, out System.Numerics.BigInteger value) =>
        v.Type == DATATYPES.STD_LOGIC_VECTOR
            ? LogicVector.TryToInteger(StripLiteralQuotes(v.Value), SignednessOf(v), out value)
            : TryInteger(v, out value);

    /// <summary>
    /// Reads a Variable's value as a non-negative integer, honouring the
    /// VHDL convention that STD_LOGIC_VECTOR values are written MSB-first
    /// in binary. Plain decimal integers and natural numbers parse the
    /// usual way; meta-values ('U' / 'X' / 'Z' / …) collapse to 0 so a
    /// stale uninitialised signal can't crash a comparison.
    /// </summary>
    /// <summary>
    /// VHDL &amp; (concatenation) implementation. Joins two operands as
    /// MSB-first bit strings and returns the combined string. The result
    /// length is always (left bits + right bits) so chained patterns like
    /// <c>'0' &amp; unsigned(a) &amp; '1'</c> grow predictably; the caller
    /// retags the wrapping Variable as STD_LOGIC_VECTOR with this length
    /// as its Size.
    /// </summary>
    /// <remarks>
    /// Operand encoding: STD_LOGIC ports / signals carry their value as a
    /// 1-character string ("0", "1", "U", …). STD_LOGIC_VECTOR signals
    /// carry the full N-character bit string. Either way we read
    /// <c>.Value.ToString()</c> and strip any quote chars left over from
    /// VHDL literals like <c>'0'</c> or <c>"0011"</c>. INTEGER operands
    /// (rare in real concat usage but supported defensively) encode as
    /// the integer's binary representation.
    /// </remarks>
    private static string ConcatenateBits(Variable l, Variable r)
        => IsText(l) || IsText(r) ? TextOf(l) + TextOf(r) : AsConcatBits(l) + AsConcatBits(r);

    private static string AsConcatBits(Variable v)
    {
        if (v?.Value == null) return "0";
        var s = v.Value.ToString() ?? string.Empty;

        // Strip VHDL literal quotes so '1' → "1" and "0011" → "0011".
        s = s.Replace("\"", string.Empty).Replace("'", string.Empty);

        // INTEGER literal coming through the concat path — render as
        // unsigned binary using the variable's declared Size when set,
        // else the minimum width to represent the value.
        if (v.Type == DATATYPES.INTEGER || v.Type == DATATYPES.NATURAL)
        {
            if (int.TryParse(s, out int iv))
            {
                int width = v.Size > 0 ? v.Size : Math.Max(1, (int)Math.Ceiling(Math.Log2(iv + 1)));
                return Convert.ToString(iv, 2).PadLeft(width, '0');
            }
        }

        return s.Length == 0 ? "0" : s;
    }

    /// <summary>The relational operator applied to a comparison result (<paramref name="c"/> &lt; 0, 0 or &gt; 0).</summary>
    private bool Relational(int c) => OperatorName.ToLower() switch
    {
        "=" => c == 0,
        "!=" or "/=" => c != 0,
        "<" => c < 0,
        "<=" => c <= 0,
        ">" => c > 0,
        ">=" => c >= 0,
        _ => throw new Exception($"Operator type do not detected or even supported: {OperatorName}"),
    };

    private static bool IsText(Variable v) => v.Type is DATATYPES.STRING or DATATYPES.CHARACTER;

    private static string TextOf(Variable v) => v.Value?.ToString() ?? string.Empty;

    private static bool HasMeta(Variable v) =>
        v.Type == DATATYPES.STD_LOGIC_VECTOR && StdLogic.IsX(StripLiteralQuotes(v.Value));

    /// <summary>Publishes a numeric_std assertion warning (<c>NUMERIC_STD.&lt;what&gt;</c>), as GHDL prints it.</summary>
    internal static void NumericStdWarning(string what) =>
        ReportBus.Instance.Emit($"[assert-warning] NUMERIC_STD.{what}");

    /// <summary>
    /// Attempts to coerce types for type-mismatched operands.
    /// v0.16.1: Added to support implicit type conversion for educational purposes.
    /// </summary>
    /// <param name="left">The left operand (may be modified).</param>
    /// <param name="right">The right operand (may be modified).</param>
    /// <returns>True if coercion was successful, false otherwise.</returns>
    private bool TryCoerceTypes(ref Variable left, ref Variable right)
    {
        // INTEGER <-> STD_LOGIC_VECTOR coercion
        if ((left.Type is DATATYPES.INTEGER or DATATYPES.NATURAL && right.Type == DATATYPES.STD_LOGIC_VECTOR) ||
            (left.Type == DATATYPES.STD_LOGIC_VECTOR && right.Type is DATATYPES.INTEGER or DATATYPES.NATURAL))
        {
            // For educational purposes, allow this - convert INTEGER to vector representation
            // or treat vector as integer for arithmetic
            return true; // Allow the operation to proceed with mixed types
        }

        // INTEGER <-> NATURAL coercion (NATURAL is a subset of INTEGER)
        if ((left.Type == DATATYPES.INTEGER && right.Type == DATATYPES.NATURAL) ||
            (left.Type == DATATYPES.NATURAL && right.Type == DATATYPES.INTEGER))
        {
            return true;
        }

        // STD_LOGIC <-> BIT coercion
        if ((left.Type == DATATYPES.STD_LOGIC && right.Type == DATATYPES.BIT) ||
            (left.Type == DATATYPES.BIT && right.Type == DATATYPES.STD_LOGIC))
        {
            return true;
        }

        // string/character against a literal another type was given (`s = "0101"`, `c = '1'`)
        if (IsText(left) || IsText(right))
        {
            return true;
        }

        // UNKNOWN type - allow coercion for flexibility with custom types
        if (left.Type == DATATYPES.UNKNOWN || right.Type == DATATYPES.UNKNOWN)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Scalar <c>and/or/xor/nand/nor/xnor</c> on std_logic or bit values (IEEE 1164 tables, <see cref="StdLogic"/>).
    /// </summary>
    public object SolveLogicOperation(Variable var1, Variable var2)
    {
        char a = ScalarChar(var1), b = ScalarChar(var2);
        return LogicFunction is { } op ? op(a, b) : 'U';
    }

    /// <summary>The single std_logic character a scalar operand holds (literal quotes stripped).</summary>
    private static char ScalarChar(Variable v)
    {
        string s = StripLiteralQuotes(v.Value ?? throw new Exception("Malformed data"));
        return s.Length == 1 ? s[0] : Convert.ToChar(s);
    }

    /// <summary>
    /// Bug #17 fix: logic ops (and/or/nand/nor/xor/xnor/not) over BOOLEAN
    /// operands. Reuses the bit-level SolveLogicOperation by mapping
    /// true → '1' and false → '0'. Returns a bool so downstream consumers
    /// (IfStructure condition, Operation.Execute) see the right type.
    /// </summary>
    public object SolveBooleanLogicOperation(Variable var1, Variable var2)
    {
        bool b1 = Truth.Of(var1.Value);
        bool b2 = Truth.Of(var2.Value);
        return OperatorName.ToLower() switch
        {
            "and"  => b1 && b2,
            "or"   => b1 || b2,
            "nand" => !(b1 && b2),
            "nor"  => !(b1 || b2),
            "xor"  => b1 != b2,
            "xnor" => b1 == b2,
            _      => false
        };
    }

    /// <summary>
    /// Solves an arithmetic operation.
    /// </summary>
    /// <param name="variable">The variable.</param>
    /// <returns>The result of the arithmetic operation.</returns>
    public object SolveArithOperation(Variable variable) => throw new NotImplementedException();

    /// <summary>
    /// Solves an arithmetic operation.
    /// </summary>
    /// <param name="leftVariable">The left variable.</param>
    /// <param name="rightVariable">The right variable.</param>
    /// <returns>The result of the arithmetic operation.</returns>
    public object SolveArithOperation(Variable leftVariable, Variable rightVariable)
    {
        string op = OperatorName.ToLowerInvariant();
        bool leftVec = leftVariable.Type == DATATYPES.STD_LOGIC_VECTOR;
        bool rightVec = rightVariable.Type == DATATYPES.STD_LOGIC_VECTOR;

        // Vectors (numeric_std, std_logic_arith/signed/unsigned): any width, signed or
        // unsigned, result width by numeric_std's rules, wrapped to it.
        if (leftVec || rightVec)
        {
            if (leftVariable.IsStub || rightVariable.IsStub) return 0;
            string lb = StripLiteralQuotes(leftVariable.Value), rb = StripLiteralQuotes(rightVariable.Value);
            int width = LogicVector.ResultWidth(op, leftVec ? Math.Max(1, lb.Length) : null, rightVec ? Math.Max(1, rb.Length) : null);
            if (HasMeta(leftVariable) || HasMeta(rightVariable))
            {
                // numeric_std returns all 'X' silently; the Synopsys packages warn.
                if (!IsNumericStd(leftVariable) && !IsNumericStd(rightVariable))
                    ReportBus.Instance.Emit("[assert-warning] There is an 'U'|'X'|'W'|'Z'|'-' in an arithmetic operand, the result will be 'X'(es).");
                return new string('X', width);
            }
            if (!TryNumber(leftVariable, out var a) || !TryNumber(rightVariable, out var b)) return new string('X', width);
            return LogicVector.FromInteger(LogicVector.Apply(op, a, b), width);
        }

        if (leftVariable.IsStub || rightVariable.IsStub) return 0;
        if (leftVariable.Type is not (DATATYPES.INTEGER or DATATYPES.NATURAL) || rightVariable.Type is not (DATATYPES.INTEGER or DATATYPES.NATURAL))
            throw new InvalidOperationException($"The datatypes do not match: {leftVariable.Name}:{leftVariable.Type} - {rightVariable.Name}:{rightVariable.Type}");

        // Reals (`2.5`) share the NATURAL classification; two integral operands stay integer (`7/2 = 3`).
        if (!IsIntegral(leftVariable) || !IsIntegral(rightVariable))
            return SolveRealOperation(GetDouble(leftVariable), GetDouble(rightVariable));
        TryInteger(leftVariable, out var x);
        TryInteger(rightVariable, out var y);
        var result = LogicVector.Apply(op, x, y);
        // Physical time values (fs) are 64-bit; integer wraps at 32 bits as in GHDL.
        return IsTime(leftVariable) || IsTime(rightVariable) ? (object)(long)result : LogicVector.ToInt32(result);
    }

    private static bool IsTime(Variable v) => string.Equals(v.TypeMark, "time", StringComparison.OrdinalIgnoreCase);

    private static bool IsIntegral(Variable v) => v.Value is int or long or System.Numerics.BigInteger || !(v.Value?.ToString() ?? "").Contains('.');

    private static double GetDouble(Variable v) =>
        double.TryParse(v.Value?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d) ? d : 0.0;

    /// <summary>
    /// Solves an integer operation.
    /// </summary>
    /// <param name="leftVariable">The left variable.</param>
    /// <param name="rightVariable">The right variable.</param>
    /// <returns>The result of the integer operation.</returns>
    public int SolveIntOperation(int leftVariable, int rightVariable) =>
        (int)IntegerArithmetic.Apply(OperatorName, leftVariable, rightVariable);

    /// <summary>
    /// Solves a real operation.
    /// </summary>
    /// <param name="leftVariable">The left variable.</param>
    /// <param name="rightVariable">The right variable.</param>
    /// <returns>The result of the real operation.</returns>
    public double SolveRealOperation(double leftVariable, double rightVariable) =>
        IntegerArithmetic.ApplyReal(OperatorName, leftVariable, rightVariable);

    /// <summary>
    /// Solves a logic operation.
    /// </summary>
    /// <param name="variable">The variable.</param>
    /// <returns>The result of the logic operation.</returns>
    public object SolveLogicOperation(Variable variable){
        // Unary `not` — works on any of:
        //   • BOOLEAN (true / false)             — `if not found then …`
        //   • STD_LOGIC ('0' / '1' / meta)        — `q <= not q`
        //   • STD_LOGIC_VECTOR (full bit string)  — handled by SolveVectorLogicOperation
        // The previous Convert.ToChar(variable.Value) blew up with
        // InvalidCastException whenever the Value was a `bool` or an
        // uninitialised `new object()` — the arbiter exercises hit this
        // every time `not found` was evaluated.
        if (variable.Value is bool b)
        {
            return !b;
        }

        string s = StripLiteralQuotes(variable.Value);
        if (string.Equals(s, "True",  StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals(s, "False", StringComparison.OrdinalIgnoreCase)) return true;
        if (IsReduction)
        {
            var (baseOp, invert) = OperatorName.ToLowerInvariant() switch
            {
                "nand" => ("and", true), "nor" => ("or", true), "xnor" => ("xor", true), var n => (n, false),
            };
            char r = StdLogic.Reduce(StdLogic.Binary(baseOp)!, s, baseOp == "and" ? '1' : '0');
            return invert ? StdLogic.Not(r) : r;
        }
        if (OperatorName == "??") return s.Length == 1 && StdLogic.Condition(s[0]);
        if (OperatorName.Equals("abs", StringComparison.OrdinalIgnoreCase))
        {
            if (variable.Type == DATATYPES.STD_LOGIC_VECTOR)
                return StdLogic.IsX(s) ? new string('X', s.Length)
                    : LogicVector.TryToInteger(s, SignednessOf(variable), out var v) ? LogicVector.FromInteger(System.Numerics.BigInteger.Abs(v), s.Length) : s;
            return TryInteger(variable, out var n) ? LogicVector.ToInt32(System.Numerics.BigInteger.Abs(n))
                : throw new ParserException($"abs is not defined for '{s}'");
        }
        if (s.Length == 0) return '1';   // uninitialised — treat as `not 0` = '1'
        if (!OperatorName.Equals("not", StringComparison.OrdinalIgnoreCase)) return '0';

        // Vector `not` inverts every element; a scalar inverts its one value (IEEE 1164).
        bool isVector = variable.Type == DATATYPES.STD_LOGIC_VECTOR || (s.Length > 1 && StdLogic.IsVector(s));
        return isVector ? StdLogic.Not(s) : StdLogic.Not(s[0]);
    }

    /// <summary>
    /// Solves a vector logic operation.
    /// </summary>
    /// <param name="v1">The first vector.</param>
    /// <param name="v2">The second vector.</param>
    /// <returns>The result of the vector logic operation.</returns>
    private object SolveVectorLogicOperation(Variable v1, Variable v2)
    {
        // Check for shift/rotate operations (v2 is shift amount, not a vector)
        string opLower = OperatorName.ToLower();
        if (opLower == "sll" || opLower == "srl" || opLower == "sla" || opLower == "sra" ||
            opLower == "rol" || opLower == "ror")
        {
            return SolveShiftOperation(v1, v2);
        }

        // Pass 23 (2026-07-30): Operation.Syntetize wraps a std_logic_vector
        // literal like `"0011"` in a Variable with the raw quoted string
        // as Value and Size=0. That was safe as long as literals only
        // reached signal-assign paths (which strip quotes downstream),
        // but SolveVectorLogicOperation's bit-by-bit loop keys on v.Size
        // — with Size=0 the loop runs 0 iterations and the result is an
        // empty string. Downstream `Result.Value = ""` then propagates
        // 0000 (padded) even when the operator would have produced a
        // real value. Fix: strip quotes and derive the effective width
        // from the unquoted string length; use the max as the operand
        // width (padding shorter vectors with '0's if necessary, but
        // still throwing on true size mismatches).
        string clean1 = StripLiteralQuotes(v1.Value);
        string clean2 = StripLiteralQuotes(v2.Value);
        int size1 = v1.Size > 0 ? v1.Size : clean1.Length;
        int size2 = v2.Size > 0 ? v2.Size : clean2.Length;
        if (size1 != size2)
            throw new ParserException(
                $"Vector size mismatch in '{OperatorName}' operation: '{v1.Name}' has {size1} bits, '{v2.Name}' has {size2} bits");
        // Left-pad each string to its declared width so a Value like "10"
        // stored against a 4-bit Variable still bit-aligns correctly.
        if (clean1.Length < size1) clean1 = clean1.PadLeft(size1, '0');
        if (clean2.Length < size2) clean2 = clean2.PadLeft(size2, '0');
        if (OperatorName is "?=" or "?/=")
        {
            // Vector matching equality: the AND of the element matches.
            char eq = StdLogic.MatchEq(clean1, clean2);
            return OperatorName == "?=" ? eq : StdLogic.Not(eq);
        }
        return LogicFunction is { } op
            ? StdLogic.Vector(op, clean1, clean2)
            : new string('U', size1);
    }

    private static string StripLiteralQuotes(object? value)
    {
        var s = value?.ToString() ?? string.Empty;
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') return s.Substring(1, s.Length - 2);
        if (s.Length >= 2 && s[0] == '\'' && s[^1] == '\'') return s.Substring(1, s.Length - 2);
        return s;
    }

    /// <summary>
    /// Solves shift and rotate operations (sll, srl, sla, sra, rol, ror).
    /// </summary>
    /// <param name="vector">The vector to shift/rotate.</param>
    /// <param name="amount">The shift/rotate amount variable.</param>
    /// <returns>The shifted/rotated vector as string.</returns>
    private object SolveShiftOperation(Variable vector, Variable amount)
    {
        string bits = StripLiteralQuotes(vector.Value);
        TryInteger(amount, out var n);
        return LogicVector.Shift(bits, OperatorName, (long)n, SignednessOf(vector));
    }
}