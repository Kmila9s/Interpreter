using Parser.Models;

namespace Interpreter.Interfaces;
public interface IArithOperation{
    object SolveArithOperation(Variable variable);
    object SolveArithOperation(Variable leftVariable, Variable rightVariable);
    int SolveIntOperation(int leftVariable, int rightVariable);
    double SolveRealOperation(double leftVariable, double rightVariable);
     
}
