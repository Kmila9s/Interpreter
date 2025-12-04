using Interpreter.Services;
using Parser.Models;

namespace Interpreter.Repositories;

/// <summary>
/// Implements the Shunting-yard algorithm to convert infix notation to postfix notation.
/// </summary>
public class ShuntingYard {
    private Stack<Operator> _operators = new();
    private readonly List<object> _items = new();

    /// <summary>
    /// Gets the result of the Shunting-yard algorithm in postfix notation.
    /// </summary>
    public List<object> Result { get; private set; } = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ShuntingYard"/> class.
    /// </summary>
    /// <param name="BaseItems">The list of items in infix notation.</param>
    public ShuntingYard(List<object> BaseItems) { 
        _items = BaseItems; 
        OrderPrecedenceList();
    }

    /// <summary>
    /// Orders the precedence of the operators in the list.
    /// </summary>
    private void OrderPrecedenceList(){
        foreach (var item in _items){
            if(item is Variable) Result.Add(item);
            else if(item is Operator operation ){
                if(operation.OperatorName == "(")
                    _operators.Push(operation);
                else if(operation.OperatorName == ")"){
                    while(
                        _operators.Count > 0 &&
                        _operators.Peek().OperatorName != "("
                    )
                        Result.Add(_operators.Pop());
                    if(_operators.Count > 0 && _operators.Peek().OperatorName == "(")
                        _operators.Pop(); // Discard the "("
                }else{
                    // v0.15.0 Fix: Use Convert.ToInt32() instead of direct cast for enum precedence
                    // The Precedence property is typed as 'object' and can be either:
                    // - PrecedencesLogicOperators (for AND, OR, NOT, etc.)
                    // - PrecedencesArithOperators (for +, -, *, /, **, mod)
                    // Direct casting (int) fails when the object contains an enum value
                    while(
                        _operators.Count > 0 &&
                        Convert.ToInt32(_operators.Peek().Precedence) <= Convert.ToInt32(operation.Precedence)
                    )
                        Result.Add(_operators.Pop());
                    _operators.Push(operation);
                }
            }
        }
        while (_operators.Count > 0)
            Result.Add(_operators.Pop());
    }
}