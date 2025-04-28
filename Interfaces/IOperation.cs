

using Parser.Models;

namespace Interpreter.Interfaces;
public interface IOperation {
    object Execute();
    object Execute(object[] Items, int CurrentIndex = 0);
    object Execute(Variable leftSideOperator, Variable rightSideOperator);
}