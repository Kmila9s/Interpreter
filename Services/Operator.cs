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
                        Precedence = PrecedencesArithOperators.UNARY;
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
                // STD_LOGIC/BIT: use string comparison to handle meta-values ('U','X','Z','W','L','H','-')
                string s1 = var1.Value?.ToString() ?? "U";
                string s2 = var2.Value?.ToString() ?? "U";
                bool isMetaValue = !int.TryParse(s1, out _) || !int.TryParse(s2, out _);
                switch (OperatorName.ToLower())
                {
                    case "=":
                        return !isMetaValue && s1 == s2;
                    case "!=":
                    case "/=":
                        return isMetaValue || s1 != s2;
                    case ">":
                    case "<":
                    case ">=":
                    case "<=":
                        // Relational ops on meta-values always return false
                        if (isMetaValue) return false;
                        return OperatorName.ToLower() switch
                        {
                            ">" => int.Parse(s1) > int.Parse(s2),
                            "<" => int.Parse(s1) < int.Parse(s2),
                            ">=" => int.Parse(s1) >= int.Parse(s2),
                            "<=" => int.Parse(s1) <= int.Parse(s2),
                            _ => false
                        };
                    default:
                        throw new Exception($"Operator type do not detected or even supported: {OperatorName}");
                }
            }
            case DATATYPES.INTEGER:
            case DATATYPES.NATURAL:
            {
                // INTEGER/NATURAL: safe parsing with fallback to 0 for meta-values
                string s1 = var1.Value?.ToString() ?? "0";
                string s2 = var2.Value?.ToString() ?? "0";
                if (!int.TryParse(s1, out int i1)) i1 = 0;
                if (!int.TryParse(s2, out int i2)) i2 = 0;
                switch (OperatorName.ToLower())
                {
                    case "=":
                        return i1 == i2;
                    case "!=":
                    case "/=":
                        return i1 != i2;
                    case ">":
                        return i1 > i2;
                    case "<":
                        return i1 < i2;
                    case ">=":
                        return i1 >= i2;
                    case "<=":
                        return i1 <= i2;
                    default:
                        throw new Exception($"Operator type do not detected or even supported: {OperatorName}");
                }
            }
            default:
                throw new Exception($"Datatype do not detected or even supported: {OperatorName}");
        }
    }

    /// <summary>
    /// Comparison helper that handles any combination of operand types
    /// where at least one side is a STD_LOGIC_VECTOR. Both operands are
    /// normalised to integers via <see cref="ParseAsUnsignedInt"/> (binary
    /// string for vectors, plain decimal for INTEGER literals) before
    /// comparison, matching numeric_std unsigned semantics. Equality keeps
    /// string comparison so a vector with width drift compares cleanly.
    /// </summary>
    private object SolveVectorComparation(Variable var1, Variable var2)
    {
        string s1 = var1.Value?.ToString() ?? string.Empty;
        string s2 = var2.Value?.ToString() ?? string.Empty;

        switch (OperatorName.ToLower())
        {
            case "=":
                // For vector ⇆ integer equality, compare numerically — the
                // string forms ("0010" vs "2") would otherwise miscompare.
                if (var1.Type != var2.Type)
                {
                    return ParseAsUnsignedInt(var1) == ParseAsUnsignedInt(var2);
                }
                return s1.Equals(s2, StringComparison.Ordinal);
            case "!=":
            case "/=":
                if (var1.Type != var2.Type)
                {
                    return ParseAsUnsignedInt(var1) != ParseAsUnsignedInt(var2);
                }
                return !s1.Equals(s2, StringComparison.Ordinal);
            case "<":
            case ">":
            case "<=":
            case ">=":
            {
                int i1 = ParseAsUnsignedInt(var1);
                int i2 = ParseAsUnsignedInt(var2);
                return OperatorName.ToLower() switch
                {
                    "<"  => i1 <  i2,
                    ">"  => i1 >  i2,
                    "<=" => i1 <= i2,
                    ">=" => i1 >= i2,
                    _    => false
                };
            }
            default:
                throw new Exception($"Operator type do not detected or even supported for vectors: {OperatorName}");
        }
    }

    /// <summary>
    /// Reads a Variable's value as a non-negative integer, honouring the
    /// VHDL convention that STD_LOGIC_VECTOR values are written MSB-first
    /// in binary. Plain decimal integers and natural numbers parse the
    /// usual way; meta-values ('U' / 'X' / 'Z' / …) collapse to 0 so a
    /// stale uninitialised signal can't crash a comparison.
    /// </summary>
    /// <summary>
    /// VHDL & (concatenation) implementation. Joins two operands as
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
        => AsConcatBits(l) + AsConcatBits(r);

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

    private static int ParseAsUnsignedInt(Variable v)
    {
        if (v.Value == null) return 0;
        string s = v.Value.ToString() ?? string.Empty;

        // Vector-typed signals always come in as binary strings — interpret
        // them that way regardless of int.TryParse's decimal interpretation.
        if (v.Type == DATATYPES.STD_LOGIC_VECTOR &&
            s.Length > 0 && s.All(c => c == '0' || c == '1'))
        {
            try { return Convert.ToInt32(s, 2); } catch { return 0; }
        }

        if (int.TryParse(s, out int direct)) return direct;

        if (s.Length > 0 && s.All(c => c == '0' || c == '1'))
        {
            try { return Convert.ToInt32(s, 2); } catch { return 0; }
        }
        return 0;
    }

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
        if ((left.Type == DATATYPES.INTEGER && right.Type == DATATYPES.STD_LOGIC_VECTOR) ||
            (left.Type == DATATYPES.STD_LOGIC_VECTOR && right.Type == DATATYPES.INTEGER))
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

        // UNKNOWN type - allow coercion for flexibility with custom types
        if (left.Type == DATATYPES.UNKNOWN || right.Type == DATATYPES.UNKNOWN)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Solves a logic operation.
    /// </summary>
    /// <param name="var1">The first variable.</param>
    /// <param name="var2">The second variable.</param>
    /// <returns>The result of the logic operation.</returns>
    public object SolveLogicOperation(Variable var1, Variable var2)
    {
        char a = Convert.ToChar(var1.Value?.ToString()?.Replace("'", "") ?? throw new Exception("Malformed data"));
        char b = Convert.ToChar(var2.Value?.ToString()?.Replace("'", "") ?? throw new Exception("Malformed data"));
        char result;
        switch (OperatorName.ToLower())
        {
            case "and":
                result = (a == '1' && b == '1') ? '1' : '0';
                break;
            case "or":
                result = (a == '1' || b == '1') ? '1' : '0';
                break;
            case "xor":
                result = (a != b) ? '1' : '0';
                break;
            case "xnor":
                // Previously fell through to 'U' — the precedence / token
                // recognition was fine (Operator.cs:63), but the bit-level
                // evaluator never had a case for it, so `a xnor b` always
                // produced undefined. This restores the missing branch;
                // vectors pick it up transparently via SolveVectorLogicOperation.
                result = (a == b) ? '1' : '0';
                break;
            case "nand":
                result = (a == '1' && b == '1') ? '0' : '1';
                break;
            case "nor":
                result = (a == '0' && b == '0') ? '1' : '0';
                break;
            default:
                result = 'U';
                break;
        }
        return result;
    }

    /// <summary>
    /// Bug #17 fix: logic ops (and/or/nand/nor/xor/xnor/not) over BOOLEAN
    /// operands. Reuses the bit-level SolveLogicOperation by mapping
    /// true → '1' and false → '0'. Returns a bool so downstream consumers
    /// (IfStructure condition, Operation.Execute) see the right type.
    /// </summary>
    public object SolveBooleanLogicOperation(Variable var1, Variable var2)
    {
        bool b1 = ToBool(var1.Value);
        bool b2 = ToBool(var2.Value);
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

    private static bool ToBool(object? value)
    {
        if (value is bool b) return b;
        string s = value?.ToString() ?? "";
        if (bool.TryParse(s, out bool parsed)) return parsed;
        return s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase);
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
    public object SolveArithOperation(Variable leftVariable, Variable rightVariable){
        object result = new();
        // Helper to safely get integer from variable value, handling VHDL meta-values
        int GetInt(Variable v) {
            if (v.Value == null || v.Value.GetType() == typeof(object))
            {
                // Uninitialized variable - try to use 0 or parse name if it's a literal
                if (int.TryParse(v.Name?.Replace("Literal-", "").Split('-')[0], out int parsed))
                    return parsed;
                return 0;
            }
            string str = v.Value.ToString() ?? "0";
            // Vector-typed signals carry their value as a binary string
            // ("00000010" = 2). We MUST try binary BEFORE decimal because
            // int.TryParse("00000010") happily returns 10 (decimal), which
            // silently corrupted unsigned arithmetic the moment a counter
            // crossed any value whose binary representation looks like a
            // valid decimal — bench Phase D / bug #6 (2026-06-02).
            // BUT: don't apply the heuristic to INTEGER/NATURAL types —
            // their decimal value can incidentally consist of only 0/1
            // digits ("10" = ten, not two; "11" = eleven, not three) and
            // forcing a binary parse silently corrupted intra-loop
            // `count := count + 1` accumulation in 034 hamming once the
            // count crossed 9. Bug #14 follow-up.
            bool vectorish = v.Type == DATATYPES.STD_LOGIC_VECTOR ||
                (v.Type != DATATYPES.INTEGER && v.Type != DATATYPES.NATURAL &&
                 str.Length > 1 && str.All(c => c == '0' || c == '1'));
            if (vectorish)
            {
                try { return Convert.ToInt32(str, 2); }
                catch { /* fall through to decimal */ }
            }
            // Handle VHDL meta-values ('U', 'X', 'Z', etc.) and integer literals
            if (int.TryParse(str, out int intVal))
                return intVal;
            return 0;
        }
        double GetDouble(Variable v) {
            if (v.Value == null || v.Value.GetType() == typeof(object))
            {
                if (double.TryParse(v.Name?.Replace("Literal-", "").Split('-')[0], out double parsed))
                    return parsed;
                return 0.0;
            }
            string str = v.Value.ToString() ?? "0";
            if (double.TryParse(str, out double dblVal))
                return dblVal;
            return 0.0;
        }

        // v3.3 (numeric_std runtime): when either operand is a vector, do
        // width-aware modular arithmetic and return a STD_LOGIC_VECTOR string
        // of the wider operand's width. This is the simulation-side mirror
        // of the synth path's ADD/SUB/MUL cells — `count <= count + 1` where
        // `count : unsigned(3 downto 0)` now produces a 4-bit string the
        // signal scheduler can write back without a type round-trip,
        // including correct wraparound at the bit boundary.
        bool leftVec  = leftVariable.Type == DATATYPES.STD_LOGIC_VECTOR;
        bool rightVec = rightVariable.Type == DATATYPES.STD_LOGIC_VECTOR;
        if (leftVec || rightVec)
        {
            if (leftVariable.IsStub || rightVariable.IsStub)
            {
                // Stubs preserve the legacy "return 0" behaviour — we don't
                // know the intended width, so any vector-formatted answer
                // would be a guess.
                result = 0;
            }
            else
            {
                int width = Math.Max(
                    leftVec  ? Math.Max(1, leftVariable.Size)  : 1,
                    rightVec ? Math.Max(1, rightVariable.Size) : 1);
                int li  = GetInt(leftVariable);
                int ri  = GetInt(rightVariable);
                int raw = SolveIntOperation(li, ri);

                // Modular wrap to the result width — matches numeric_std
                // unsigned semantics where overflow rolls over inside the
                // declared bit width.
                if (width >= 31)
                {
                    result = Convert.ToString(raw, 2).PadLeft(width, '0');
                }
                else
                {
                    int mask    = (1 << width) - 1;
                    int wrapped = raw & mask;
                    result = Convert.ToString(wrapped, 2).PadLeft(width, '0');
                }
            }
            return result;
        }

        if(leftVariable.Type != rightVariable.Type){
            if(
                (leftVariable.Type == DATATYPES.INTEGER || leftVariable.Type == DATATYPES.NATURAL) &&
                (rightVariable.Type == DATATYPES.INTEGER || rightVariable.Type == DATATYPES.NATURAL)
            ){
                result = SolveIntOperation(GetInt(leftVariable), GetInt(rightVariable));
            }
            // v0.13.0: Skip type check for stub variables (undeclared identifiers)
            else if (leftVariable.IsStub || rightVariable.IsStub)
            {
                // Return a default value for operations involving stub variables
                result = 0;
            }
            else
                throw new InvalidOperationException($"The datatypes do not match: {leftVariable.Name}:{leftVariable.Type} - {rightVariable.Name}:{rightVariable.Name}");
        }else{
            switch(leftVariable.Type){
                case DATATYPES.INTEGER:
                    result = SolveIntOperation(GetInt(leftVariable), GetInt(rightVariable));
                break;
                case DATATYPES.NATURAL:
                    result = SolveRealOperation(GetDouble(leftVariable), GetDouble(rightVariable));
                break;
                default:
                    // Default to integer operation or return 0 for unhandled types
                    result = 0;
                break;
            }
        }
        return result;
    }

    /// <summary>
    /// Solves an integer operation.
    /// </summary>
    /// <param name="leftVariable">The left variable.</param>
    /// <param name="rightVariable">The right variable.</param>
    /// <returns>The result of the integer operation.</returns>
    public int SolveIntOperation(int leftVariable, int rightVariable){
        switch(OperatorName){
            case "+":
                return leftVariable + rightVariable;
            case "-":
                return leftVariable - rightVariable;
            case "*":
                return leftVariable * rightVariable;
            case "**":
                return (int)Math.Pow(leftVariable, rightVariable);
            case "/":
                return leftVariable / rightVariable;
            case "mod":
                return leftVariable % rightVariable;
            default:
                return 0;
        }
    }

    /// <summary>
    /// Solves a real operation.
    /// </summary>
    /// <param name="leftVariable">The left variable.</param>
    /// <param name="rightVariable">The right variable.</param>
    /// <returns>The result of the real operation.</returns>
    public double SolveRealOperation(double leftVariable, double rightVariable){
        switch(OperatorName){
            case "+":
                return leftVariable + rightVariable;
            case "-":
                return leftVariable - rightVariable;
            case "*":
                return leftVariable * rightVariable;
            case "**":
                return Math.Pow(leftVariable, rightVariable);
            case "/":
                return leftVariable / rightVariable;
            case "mod":
                return leftVariable % rightVariable;
            default:
                return 0;
        }
    }

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

        string s = variable.Value?.ToString() ?? string.Empty;
        if (string.Equals(s, "True",  StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals(s, "False", StringComparison.OrdinalIgnoreCase)) return true;
        if (s.Length == 0) return '1';   // uninitialised — treat as `not 0` = '1'

        // Vector `not`: invert every bit and return the inverted string.
        // The previous path only looked at s[0], so `not "111111"` returned
        // '0' (a single scalar) instead of "000000". Detect vector either
        // by the variable's declared type or by the string holding 2+ bits.
        bool isVector = variable.Type == DATATYPES.STD_LOGIC_VECTOR ||
                        (s.Length > 1 && s.All(c => c == '0' || c == '1' || c == 'u' || c == 'x' || c == 'z' || c == 'U' || c == 'X' || c == 'Z'));
        if (isVector && OperatorName.Equals("not", StringComparison.OrdinalIgnoreCase))
        {
            var chars = new char[s.Length];
            for (int i = 0; i < s.Length; i++)
            {
                chars[i] = s[i] switch
                {
                    '0' => '1',
                    '1' => '0',
                    _   => 'x',
                };
            }
            return new string(chars);
        }

        char item = s[0];
        return OperatorName.ToLower() switch
        {
            "not" => item == '1' ? '0' : '1',
            _     => (object)'0'
        };
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

        if (v1.Size != v2.Size)
            throw new ParserException(
                $"Vector size mismatch in '{OperatorName}' operation: '{v1.Name}' has {v1.Size} bits, '{v2.Name}' has {v2.Size} bits");
        string result = "",
        vectorString1 = (string)v1.Value,
        vectorString2 = (string)v2.Value;
        for (int i = 0; i < v1.Size; i++)
        {
            Variable valueA = new("tempA", vectorString1[i], DATATYPES.STD_LOGIC),
            valueB = new("tempB", vectorString2[i], DATATYPES.STD_LOGIC);
            result += Convert.ToString(SolveLogicOperation(valueA, valueB));
        }
        return result;
    }

    /// <summary>
    /// Solves shift and rotate operations (sll, srl, sla, sra, rol, ror).
    /// </summary>
    /// <param name="vector">The vector to shift/rotate.</param>
    /// <param name="amount">The shift/rotate amount variable.</param>
    /// <returns>The shifted/rotated vector as string.</returns>
    private object SolveShiftOperation(Variable vector, Variable amount)
    {
        string vecStr = vector.Value?.ToString() ?? new string('0', vector.Size);
        int shiftAmt;

        // Get shift amount from the variable (safe parsing for meta-values)
        if (amount.Type == DATATYPES.INTEGER || amount.Type == DATATYPES.NATURAL)
        {
            if (!int.TryParse(amount.Value?.ToString() ?? "0", out shiftAmt))
                shiftAmt = 0;
        }
        else
        {
            // Try to parse from value
            if (!int.TryParse(amount.Value?.ToString() ?? "0", out shiftAmt))
                shiftAmt = 0;
        }

        int len = vecStr.Length;
        if (len == 0) return vecStr;

        // Clamp shift amount
        shiftAmt = Math.Abs(shiftAmt) % len;
        if (shiftAmt == 0) return vecStr;

        string result;
        switch (OperatorName.ToLower())
        {
            case "sll": // Shift Left Logical - fill with '0' on right
                result = vecStr.Substring(shiftAmt) + new string('0', shiftAmt);
                break;
            case "srl": // Shift Right Logical - fill with '0' on left
                result = new string('0', shiftAmt) + vecStr.Substring(0, len - shiftAmt);
                break;
            case "sla": // Shift Left Arithmetic - fill with rightmost bit on right
                char fillRight = vecStr[len - 1];
                result = vecStr.Substring(shiftAmt) + new string(fillRight, shiftAmt);
                break;
            case "sra": // Shift Right Arithmetic - fill with leftmost bit (sign) on left
                char signBit = vecStr[0];
                result = new string(signBit, shiftAmt) + vecStr.Substring(0, len - shiftAmt);
                break;
            case "rol": // Rotate Left
                result = vecStr.Substring(shiftAmt) + vecStr.Substring(0, shiftAmt);
                break;
            case "ror": // Rotate Right
                result = vecStr.Substring(len - shiftAmt) + vecStr.Substring(0, len - shiftAmt);
                break;
            default:
                result = vecStr;
                break;
        }
        return result;
    }
}