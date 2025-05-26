using System.Text.RegularExpressions;
using Interpreter.Interfaces;
using static Parser.Models.Constants;

namespace Interpreter.Repositories;
public class LiteralsFinder : IFinder{
    private readonly Regex _literals, _decimalOrInteger, _quotedInteger, _singleQuotedInteger, _stdLogicChar, _stdLogicVector, _hex, _boolean, _stdLogic;
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
    public bool IsLiteral(string item) => _literals.IsMatch(item);
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