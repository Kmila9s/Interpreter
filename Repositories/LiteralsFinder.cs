using System.Text.RegularExpressions;
using Interpreter.Interfaces;
using static Parser.Models.Constants;

namespace Interpreter.Repositories;

/// <summary>
/// Provides functionality to identify and determine the data type of literal values using regular expressions.
/// </summary>
public class LiteralsFinder : IFinder{
    private readonly Regex _literals, _decimalOrInteger, _quotedInteger, _singleQuotedInteger, _stdLogicChar, _stdLogicVector, _hex, _boolean, _stdLogic;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiteralsFinder"/> class, compiling all necessary regular expressions.
    /// </summary>
    public LiteralsFinder(){
        _literals = new(LITERALS);
        _decimalOrInteger = new(LITERAL_DECIMAL_OR_INTEGER);
        _quotedInteger = new(LITERAL_QUOTED_INTEGER);
        _boolean = new(LITERAL_BOOLEAN);
        _singleQuotedInteger = new(LITERAL_SINGLE_QUOTED_INTEGER);
        _stdLogicChar = new(LITERAL_STD_LOGIC_CHAR);
        _stdLogicVector = new(LITERAL_STD_LOGIC_VECTOR);
        _hex = new(LITERAL_HEX_STRING);
        _stdLogic = new(LITERAL_STD_LOGIC);
    }

    /// <summary>
    /// Checks if a given string item is a recognized literal value.
    /// </summary>
    /// <param name="item">The string to check.</param>
    /// <returns>True if the item matches the general literal pattern, otherwise false.</returns>
    public bool IsLiteral(string item) => _literals.IsMatch(item);

    /// <summary>
    /// Determines the specific data type of a given literal string.
    /// </summary>
    /// <param name="item">The literal string to analyze.</param>
    /// <returns>The corresponding <see cref="DATATYPES"/> enum value, or <see cref="DATATYPES.UNKNOWN"/> if it cannot be determined.</returns>
    public DATATYPES GetDataType(string item) =>
        _literals.IsMatch(item) ?
            (
                _stdLogic.IsMatch(item) ?
                    DATATYPES.STD_LOGIC :
                _stdLogicVector.IsMatch(item) ?
                    DATATYPES.STD_LOGIC_VECTOR :
                _decimalOrInteger.IsMatch(item) ?
                (
                    item.Contains('.') ?
                        DATATYPES.NATURAL :
                        DATATYPES.INTEGER 
                ):
                _boolean.IsMatch(item) ?
                    DATATYPES.BOOLEAN :
                _hex.IsMatch(item) ?
                    DATATYPES.HEX :
                    DATATYPES.UNKNOWN 
            ):
        DATATYPES.UNKNOWN;
}