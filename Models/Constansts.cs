namespace Interpreter.Models;

/// <summary>
/// Defines the precedence levels for logical operators. Lower integer values indicate higher precedence.
/// </summary>
public enum PrecedencesLogicOperators : int {
    /// <summary>Comparation operators (e.g., =, /=, <, >).</summary>
    COMPARATION = 5,
    /// <summary>Signal or variable assignment (e.g., <=, :=). Lowest precedence.</summary>
    ASSIGNATION = 5, // LOWEST PRECEDENCE
    /// <summary>Parenthesis for grouping expressions.</summary>
    PARENTHESIS = 4,
    /// <summary>Exclusive OR operator.</summary>
    XOR = 3, 
    /// <summary>Exclusive NOR operator.</summary>
    XNOR = 3,
    /// <summary>OR operator.</summary>
    OR = 2,
    /// <summary>NOR operator.</summary>
    NOR = 2,
    /// <summary>AND operator.</summary>
    AND = 1,
    /// <summary>NAND operator.</summary>
    NAND = 1,
    /// <summary>Absolute value operator.</summary>
    ABS = 0,
    /// <summary>Logical NOT operator. Highest precedence.</summary>
    NOT = 0, // HIGHERST PRECEDENCE
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
    /// <summary>Regex to detect a VHDL signal assignment (<=).</summary>
    public static string ASSIGNATION = @"[A-Za-z_]+[_a-zA-Z0-9]*\s?\<\=\s?.+";
}