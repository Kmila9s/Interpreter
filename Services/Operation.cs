using System.Text.RegularExpressions;
using Interpreter.Interfaces;
using Interpreter.Models;
using Interpreter.Repositories;
using Parser.Models;
using Parser.Repositories;

namespace Interpreter.Services;
public class Operation : IOperation {
    public Variable Result { get; private set; } = new();
    public object[] Items { get; private set; }
    private string[] _data;
    private int _currentSyntetizeIndex = 0;
    public HashSet<Variable> Variables { get; private set; }
    private readonly Regex _variableRegex, _operators, _logicOperators, _arithOperators;
    private readonly LiteralsFinder _finder;
    private string _rawData = string.Empty;
    public Operation(string RawData, HashSet<Variable> Variables)
    {
        RegexOptions optionsCompiled = RegexOptions.Compiled;
        this.Variables = Variables;
        _variableRegex = new(Constants.IDENTIFIERS, optionsCompiled);
        _operators = new(Constants.OPERATORS, optionsCompiled);
        _logicOperators = new(Constants.LOGIC_OPERATORS, optionsCompiled);
        _arithOperators = new(Constants.ARITH_OPERATORS, optionsCompiled);
        _finder = new();
        RawData = _rawData;
        Tokenizer tokenizer = new(string.Empty);
        if (RawData.Contains("<="))
        {
            var splitted = RawData.Trim().Split("<=");
            Result = this.Variables.First(p => p.Name.Equals(splitted[0].Trim()));
            RawData = splitted[1].Trim();
        }
        _data = tokenizer.TokenizeLineReturn(RawData);
        ShuntingYard shuntingYard = new(Syntetize().ToList());
        Items = shuntingYard.Result.ToArray();
    }
    public object Execute(){
        Stack<Variable> variables = new();
        if (Result.PortType == Constants.PortType.IN)
            throw new InvalidOperationException($"Input ports are not elegible for assignations: {Result.Name}");

        foreach(var item in Items){
            if(item is Variable newVar){
                variables.Push(newVar);
            }
            else if(item is Operator op){
                if((Models.PrecedencesLogicOperators)op.Precedence == Models.PrecedencesLogicOperators.NOT){
                    var value = variables.Pop();
                    variables.Push(new("Temp", op.Execute(value), value.Type));
                } else {
                    var right = variables.Pop();
                    var left = variables.Pop();
                    Variable vari = new("Temp", op.Execute(left, right), left.Type);
                    if (op.OperatorType == Constants.OperatorType.COMPARATION)
                    {
                        vari.IsPort = false;
                        vari.Type = Constants.DATATYPES.BOOLEAN;
                        vari.Value = bool.Parse((string)vari.Value);
                    }
                    variables.Push(vari);
                }
            }
        }
        if (variables.Count != 1)
            throw new InvalidOperationException($"Malformed expression: stack not reduced to a single result: {_rawData}");
        Result.Value = variables.Pop().Value;
        return Result;
    }

    public object Execute(object[] Items, int CurrentIndex = 0) => throw new NotImplementedException();
    public object Execute(Variable leftSideOperator, Variable rightSideOperator) => throw new NotImplementedException();
    
    public object[] Syntetize(){
        List<object> items = new();
        bool isComparation = false;
        while (_currentSyntetizeIndex < _data.Length)
        {
            var currentItem = _data[_currentSyntetizeIndex];
            object item = new();
            if (currentItem == ";")
            {
                _currentSyntetizeIndex++;
                continue;
            }
            if (_operators.IsMatch(currentItem.ToLower()))
            {
                isComparation = true;
                item = new Operator(currentItem);
            }
            else if (_arithOperators.IsMatch(currentItem) || _logicOperators.IsMatch(currentItem.ToLower()))
            {
                item = new Operator(currentItem);
            }
            else if (_finder.IsLiteral(currentItem))
            {
                Constants.DATATYPES type = _finder.GetDataType(currentItem);
                item = new Variable($"Literal-{Guid.NewGuid()}", currentItem, type);
            }
            else if (_variableRegex.IsMatch(currentItem))
            {
                if (!Variables.Contains(currentItem)) throw new ParserException(Constants.ParseExceptions.MISSING_DECLARATION);
                item = Variables.First(p => p.Name.ToLower().Equals(currentItem.ToLower()));
            }
            else
                item = new Operator(currentItem);

            items.Add(item);
            _currentSyntetizeIndex++;
        }
        if (isComparation)
        {
            Result.PortType = Constants.PortType.OUT;
            List<object> newItems = new();
            foreach (var item in items)
            {
                if (item is Variable vari && vari.Name.Contains("Literal"))
                    vari.PortType = Constants.PortType.OUT;
                newItems.Add(item);
            }
            items = newItems;
        }
        return items.ToArray();
    }
}