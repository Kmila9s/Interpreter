namespace Interpreter.Interfaces;
using static Parser.Models.Constants;

public interface IFinder
{
    bool IsLiteral(string item);
    DATATYPES GetDataType(string item);
}