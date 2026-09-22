

using Parser.Models;

namespace Interpreter.Interfaces;

/// <summary>
/// Defines a generic contract for an executable operation within the interpreter.
/// </summary>
public interface IOperation {
    /// <summary>
    /// Executes a parameterless operation.
    /// </summary>
    /// <returns>The result of the execution.</returns>
    object Execute();

    /// <summary>
    /// Executes an operation using an array of items and a starting index.
    /// </summary>
    /// <param name="Items">An array of objects to be used in the operation.</param>
    /// <param name="CurrentIndex">The index in the array from which to start processing.</param>
    /// <returns>The result of the execution.</returns>
    object Execute(object[] Items, int CurrentIndex = 0);

    /// <summary>
    /// Executes a binary operation with a left and right operand.
    /// </summary>
    /// <param name="leftSideOperator">The left operand.</param>
    /// <param name="rightSideOperator">The right operand.</param>
    /// <returns>The result of the execution.</returns>
    object Execute(Variable leftSideOperator, Variable rightSideOperator);
}