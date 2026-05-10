namespace Interpreter.Models;

/// <summary>
/// Defines the precedence levels for logical operators. Lower integer values
/// indicate higher precedence (tighter binding).
/// </summary>
/// <remarks>
/// v3.4: corrected to match the VHDL operator-precedence chart. Previously
/// <see cref="AND"/> et al. had value 1 (highest precedence) while
/// <see cref="COMPARATION"/> had 5 (lowest), which inverted the standard:
/// `d = '1' and d_prev = '0'` parsed as `d = ('1' and d_prev) = '0'` and
/// then threw "Type mismatch: expected BOOLEAN, got STD_LOGIC" downstream.
/// The fix flips the bands so `=` binds tighter than `and`.
///
/// Reference (VHDL spec, highest precedence to lowest):
/// 1. **, abs, not                                 — value 0
/// 2. *, /, mod, rem                               — value 1 (PrecedencesArithOperators)
/// 3. + (sign), - (sign)                           — value 0 (PrecedencesArithOperators.UNARY)
/// 4. + (binary), - (binary), &amp; (concatenate)  — value 2
/// 5. sll, srl, sla, sra, rol, ror                 — value 3
/// 6. =, /=, &lt;, &lt;=, &gt;, &gt;=              — value 4
/// 7. and, or, nand, nor, xor, xnor                — value 5
/// 8. assignment (&lt;=, :=)                       — value 6
/// </remarks>
public enum PrecedencesLogicOperators : int {
    /// <summary>Absolute value operator. Highest precedence.</summary>
    ABS = 0,
    /// <summary>Logical NOT operator. Highest precedence.</summary>
    NOT = 0,
    /// <summary>Concatenation (&amp;) — sits at adder-precedence in VHDL.</summary>
    CONCAT = 2,
    /// <summary>Shift left logical operator (sll).</summary>
    SLL = 3,
    /// <summary>Shift right logical operator (srl).</summary>
    SRL = 3,
    /// <summary>Shift left arithmetic operator (sla).</summary>
    SLA = 3,
    /// <summary>Shift right arithmetic operator (sra).</summary>
    SRA = 3,
    /// <summary>Rotate left operator (rol).</summary>
    ROL = 3,
    /// <summary>Rotate right operator (ror).</summary>
    ROR = 3,
    /// <summary>Comparation operators (e.g., =, /=, less than, greater than).</summary>
    COMPARATION = 4,
    /// <summary>AND operator.</summary>
    AND = 5,
    /// <summary>NAND operator.</summary>
    NAND = 5,
    /// <summary>OR operator.</summary>
    OR = 5,
    /// <summary>NOR operator.</summary>
    NOR = 5,
    /// <summary>Exclusive OR operator.</summary>
    XOR = 5,
    /// <summary>Exclusive NOR operator.</summary>
    XNOR = 5,
    /// <summary>Parenthesis for grouping expressions. Value is irrelevant — ShuntingYard never pops `(` from the operator stack via precedence comparison.</summary>
    PARENTHESIS = 6,
    /// <summary>Signal or variable assignment (e.g., &lt;=, :=). Lowest precedence.</summary>
    ASSIGNATION = 6,
}

/// <summary>
/// Defines the precedence levels for arithmetic operators. Lower integer values indicate higher precedence.
/// </summary>
public enum PrecedencesArithOperators : int {
    /// <summary>Unary operators (e.g., +A, -B).</summary>
    UNARY = 0,
    /// <summary>Multiplication operator (*).</summary>
    MULTIPLICATION = 1,
    /// <summary>Division operator (/).</summary>
    DIVISION = 1,
    /// <summary>Addition operator (+).</summary>
    ADD = 2,
    /// <summary>Subtraction operator (-).</summary>
    SUBSTRACTION = 2,
}

/// <summary>
/// Specifies the type of a given operation.
/// </summary>
public enum OperationType : int {
    /// <summary>An assignment operation.</summary>
    ASSIGNATION,
    /// <summary>An unknown or unidentified operation.</summary>
    UNKNOWN
}

/// <summary>
/// Contains regular expressions used to identify different types of operations in the code.
/// </summary>
public class OperationsRegex {
    /// <summary>Regex to detect a VHDL signal assignment (<=) or variable assignment (:=).</summary>
    /// <remarks>v0.16.1: Added support for := variable assignments used in processes and procedures.</remarks>
    public static string ASSIGNATION = @"[A-Za-z_]+[_a-zA-Z0-9]*\s?(\<\=|:\=)\s?.+";
}