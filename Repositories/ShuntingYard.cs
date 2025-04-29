using Interpreter.Services;
using Parser.Models;

namespace Interpreter.Repositories;
public class ShuntingYard {
    private Stack<Operator> _operators = new();
    private readonly List<object> _items = new();
    public List<object> Result { get; private set; } = new();
    public ShuntingYard(List<object> BaseItems) { 
        _items = BaseItems; 
        OrderPrecedenceList();
    }
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
                    while(
                        _operators.Count > 0 &&
                        (int)_operators.Peek().Precedence <= (int)operation.Precedence
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