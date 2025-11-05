namespace Interpreter.Interfaces;
using static Parser.Models.Constants;

/// <summary>
/// Defines the contract for components that find or identify elements within the code, such as literals and their data types.
/// </summary>
public interface IFinder
{
    /// <summary>
    /// Checks if a given string item is a literal value.
    /// </summary>
    /// <param name="item">The string to check.</param>
    /// <returns>True if the item is a literal, otherwise false.</returns>
    bool IsLiteral(string item);

    /// <summary>
    /// Determines the data type of a given string item.
    /// </summary>
    /// <param name="item">The string item to analyze.</param>
    /// <returns>The DATATYPE of the item.</returns>
    DATATYPES GetDataType(string item);
}