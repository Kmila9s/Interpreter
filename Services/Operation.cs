using System.Text.RegularExpressions;
using Interpreter.Interfaces;
using Interpreter.Repositories;
using Parser.Models;
using Parser.Repositories;

namespace Interpreter.Services;
public class Operation : IOperation {
    public object Result { get; private set; } = new();
    public object[] Items { get; private set; }
    private string[] _data;
    private int _currentSyntetizeIndex = 0;
    public HashSet<Variable> Variables { get; private set; }
    private readonly Regex _variableRegex, _operators, _logicOperators;
    private readonly LiteralsFinder _finder;
    public Operation(string RawData, HashSet<Variable> Variables){
        RegexOptions optionsCompiled = RegexOptions.Compiled;
        this.Variables = Variables;
        _variableRegex = new(Constants.IDENTIFIERS, optionsCompiled);
        _operators = new(Constants.OPERATORS, optionsCompiled);
        _logicOperators = new(Constants.LOGIC_OPERATORS, optionsCompiled);
        _finder = new();
        Tokenizer tokenizer = new(string.Empty);
        _data = tokenizer.TokenizeLineReturn(RawData);
        Items = Syntetize();
    }
    public object Execute() => Execute(Items, 0);
    public object Execute(object[] Items, int CurrentIndex = 0){
        object result = new();
        while(Items.Length > CurrentIndex){
            var item = Items[CurrentIndex];
            if(item is Variable variable){
                var nextItem = Items[CurrentIndex + 1];
                if(nextItem is Operator assigment){
                    switch(assigment.OperatorType){
                        case Constants.OperatorType.ASSIGNATION:
                            CurrentIndex += 2;
                            result = Execute(Items, CurrentIndex);
                            return result;
                        case Constants.OperatorType.LOGIC_OPERATION:
                            var lastItem = Items[CurrentIndex + 2];
                            CurrentIndex += 3;
                            if(lastItem is Variable nextVar){
                                result = new Variable("Temp", assigment.Execute(variable, nextVar), variable.Type);
                            }else if(lastItem is object[] array){
                                Variable arrayResult = (Variable)Execute(array, 0);
                                result = new Variable("Temp", arrayResult.Value, variable.Type);
                            }
                        break;
                    }
                }
            }else if(item is Operator operatorIn){
                switch(operatorIn.OperatorType){
                    case Constants.OperatorType.LOGIC_OPERATION:
                        var lastItem = Items[CurrentIndex + 1];
                        CurrentIndex += 2;
                        if(lastItem is Variable nextVar){
                            result = new Variable("Temp", operatorIn.Execute((Variable)result, nextVar), nextVar.Type);
                        }else if(lastItem is object[] array){
                            Variable Result = (Variable)result;
                            Variable ArrayResult = (Variable)Execute(array, 0);
                            result = new Variable("Temp", operatorIn.Execute(Result, ArrayResult), Result.Type);
                        }
                    break;
                    default:
                        CurrentIndex++;
                    break;
                }
            }else if(item is object[] array){
                result = Execute(array, 0);
                CurrentIndex ++;
            }
        }
        return result;
    }
    public object Execute(Variable leftSideOperator, Variable rightSideOperator) => throw new NotImplementedException();
    
    public object[] Syntetize(){
        List<object> items = new();
        while(_currentSyntetizeIndex < _data.Length){
            var currentItem = _data[_currentSyntetizeIndex];
            object item = new();
            if(_operators.IsMatch(currentItem.ToLower()) || _logicOperators.IsMatch(currentItem.ToLower())){
                item = new Operator(currentItem);
            }else if(_variableRegex.IsMatch(currentItem) && Variables.Contains(currentItem)){
                item = Variables.First(p => p.Name.ToLower().Equals(currentItem.ToLower()));
            }else if(_finder.IsLiteral(currentItem)){
                Constants.DATATYPES type = _finder.GetDataType(currentItem);
                item = new Variable($"Literal-{Guid.NewGuid()}", currentItem, type);
            }else if(currentItem.Equals("(")){
                _currentSyntetizeIndex ++;
                item = Syntetize();
            }else if(currentItem.Equals(")")){
                return items.ToArray();
            }
            items.Add(item);
            _currentSyntetizeIndex++;
        }
        return items.ToArray();
    }
}