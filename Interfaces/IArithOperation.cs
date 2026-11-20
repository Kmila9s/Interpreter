using Parser.Models;

namespace Interpreter.Interfaces;

/// <summary>
/// Defines the contract for arithmetic operations, providing methods to solve operations
/// with different numbers of operands and for specific integer and real number types.
/// </summary>
public interface IArithOperation{
    /// <summary>
    /// Solves a unary arithmetic operation.
    /// </summary>
    /// <param name="variable">The single operand for the operation.</param>
    /// <returns>The result of the arithmetic operation.</returns>
    object SolveArithOperation(Variable variable);

    /// <summary>
    /// Solves a binary arithmetic operation.
    /// </summary>
    /// <param name="leftVariable">The left operand.</param>
    /// <param name="rightVariable">The right operand.</param>
    /// <returns>The result of the arithmetic operation.</returns>
    object SolveArithOperation(Variable leftVariable, Variable rightVariable);

    /// <summary>
    /// Solves a binary arithmetic operation specifically for real numbers (doubles).
    /// </summary>
    /// <param name="leftVariable">The left real number operand.</param>
    /// <param name="rightVariable">The right real number operand.</param>
    /// <returns>The double result of the operation.</returns>
    double SolveRealOperation(double leftVariable, double rightVariable);
     
}
