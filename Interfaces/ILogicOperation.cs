using Parser.Models;

namespace Interpreter.Interfaces;
public interface ILogicOperation{
    object SolveLogicOperation(Variable variable);
    object SolveLogicOperation(Variable leftVariable, Variable rightVariable);
}