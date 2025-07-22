namespace Interpreter.Models;
public enum PrecedencesLogicOperators : int {
    COMPARATION = 5,
    ASSIGNATION = 5, // LOWEST PRECEDENCE
    PARENTHESIS = 4,
    XOR = 3, 
    XNOR = 3,
    OR = 2,
    NOR = 2,
    AND = 1,
    NAND = 1,
    ABS = 0,
    NOT = 0, // HIGHERST PRECEDENCE
}
public enum PrecedencesArithOperators : int {
    UNARY = 0,
    MULTIPLICATION = 1,
    DIVISION = 1,
    ADD = 2,
    SUBSTRACTION = 2,
}
public enum OperationType : int {
    ASSIGNATION,
    UNKNOWN
}
public class OperationsRegex {
    public static string ASSIGNATION = @"[A-Za-z_]+[_a-zA-Z0-9]*\s?\<\=\s?.+";
}