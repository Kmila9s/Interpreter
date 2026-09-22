using Parser.Models;

namespace Interpreter.Interfaces;

/// <summary>
/// Defines the contract for logical operations, providing methods to solve operations
/// with one or two operands.
/// </summary>
public interface ILogicOperation{
    /// <summary>
    /// Solves a unary logical operation (e.g., NOT).
    /// </summary>
    /// <param name="variable">The single operand for the operation.</param>
    /// <returns>The result of the logical operation.</returns>
    object SolveLogicOperation(Variable variable);

    /// <summary>
    /// Solves a binary logical operation (e.g., AND, OR).
    /// </summary>
    /// <param name="leftVariable">The left operand.</param>
    /// <param name="rightVariable">The right operand.</param>
    /// <returns>The result of the logical operation.</returns>
    object SolveLogicOperation(Variable leftVariable, Variable rightVariable);
}