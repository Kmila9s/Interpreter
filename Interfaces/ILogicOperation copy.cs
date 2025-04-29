using Parser.Models;

namespace Interpreter.Interfaces;
public interface IArithOperation{
    object SolveArithOperation(Variable variable);
    object SolveArithOperation(Variable leftVariable, Variable rightVariable);
}