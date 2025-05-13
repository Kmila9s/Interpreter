using System.Collections;
using System.Text.RegularExpressions;
using Interpreter.Interfaces;
using Interpreter.Models;
using Parser.Models;
using static Parser.Models.Constants;

namespace Interpreter.Services;
public class Operator : IOperation, ILogicOperation, IArithOperation{
    public OperatorType OperatorType { get; private set; }
    public object Precedence { get; private set; } = new();
    private readonly Regex _assignationRegex, _arithRegex, _logicRegex;
    public string OperatorName { get; private set; } = string.Empty;
    public Operator(string Operator){
        this.OperatorName = Operator;
        _assignationRegex = new(ASSIGNATION_OPERATORS);
        _arithRegex = new(ARITH_OPERATORS);
        _logicRegex = new(LOGIC_OPERATORS);
        this.OperatorType = DefineOperatorType(Operator);
        DefinePrecedence(Operator);
    }
    private void DefinePrecedence(string Operator){
        switch(this.OperatorType){
            case OperatorType.LOGIC_OPERATION:
                Operator = Operator.ToLower();
                switch(Operator){
                    case "and":
                        Precedence = PrecedencesLogicOperators.AND;
                    break;
                    case "nand":
                        Precedence = PrecedencesLogicOperators.NAND;
                    break;
                    case "xor":
                        Precedence = PrecedencesLogicOperators.XOR;
                    break;
                    case "xnor":
                        Precedence = PrecedencesLogicOperators.XNOR;
                    break;
                    case "or":
                        Precedence = PrecedencesLogicOperators.OR;
                    break;
                    case "nor":
                        Precedence = PrecedencesLogicOperators.NOR;
                    break;
                    case "abs":
                        Precedence = PrecedencesLogicOperators.ABS;
                    break;
                    case "not":
                        Precedence = PrecedencesLogicOperators.NOT;
                    break;
                }
            break;
            case OperatorType.ARITH_OPERATION:
                Operator = Operator.ToLower();
                switch(Operator){
                    case "+":
                        Precedence = PrecedencesArithOperators.ADD;
                    break;
                    case "-":
                        Precedence = PrecedencesArithOperators.SUBSTRACTION;
                    break;
                    case "*":
                        Precedence = PrecedencesArithOperators.MULTIPLICATION;
                    break;
                    case "/":
                        Precedence = PrecedencesArithOperators.DIVISION;
                    break;
                    case "mod":
                        Precedence = PrecedencesArithOperators.UNARY;
                    break;
                }
            break;
            case OperatorType.UNKNOWN:
                Precedence = PrecedencesLogicOperators.PARENTHESIS;
            break;
            case OperatorType.ASSIGNATION:
                Precedence = PrecedencesLogicOperators.ASSIGNATION;
            break;
            default:
                throw new NotImplementedException();
        }
    }
    private OperatorType DefineOperatorType(string Operator) =>
        _assignationRegex.IsMatch(Operator) ? 
            OperatorType.ASSIGNATION : 
        _arithRegex.IsMatch(Operator) ?
            OperatorType.ARITH_OPERATION :
        _logicRegex.IsMatch(Operator) ?
            OperatorType.LOGIC_OPERATION :
            OperatorType.UNKNOWN;
    public object Execute() => throw new NotImplementedException();
    public object Execute(object[] array, int index) => throw new NotImplementedException();
    public object Execute(Variable variable) =>
        (PrecedencesLogicOperators)Precedence == PrecedencesLogicOperators.NOT ?
            SolveLogicOperation(variable) :
            new NotImplementedException();
    public object Execute(Variable leftSideOperator, Variable rightSideOperator){
        if(leftSideOperator.Type != rightSideOperator.Type)
            throw new ParserException("Datatypes do not match");
        object result = new();
        switch(OperatorType){
            case OperatorType.ASSIGNATION:
                result = rightSideOperator.Value;
            break;
            case OperatorType.LOGIC_OPERATION:
                switch(leftSideOperator.Type){
                    case DATATYPES.STD_LOGIC:
                        result = SolveLogicOperation(leftSideOperator, rightSideOperator);
                    break;
                    case DATATYPES.STD_LOGIC_VECTOR:
                        result = SolveVectorLogicOperation(leftSideOperator, rightSideOperator);
                    break;
                }
                
            break;
            default:
                throw new NotImplementedException();
        }
        return result;
    }
    public object SolveLogicOperation(Variable var1, Variable var2){
        char a = Convert.ToChar(var1.Value);
        char b = Convert.ToChar(var2.Value);
        char result;
        switch (OperatorName.ToLower()) {
            case "and":
                result = (a == '1' && b == '1') ? '1' : '0';
                break;
            case "or":
                result = (a == '1' || b == '1') ? '1' : '0';
                break;
            case "xor":
                result = (a != b) ? '1' : '0';
                break;
            case "nand":
                result = (a == '1' && b == '1') ? '0' : '1';
                break;
            case "nor":
                result = (a == '0' && b == '0') ? '1' : '0';
                break;
            default:
                result = 'U';
            break;
        }
        return result;
    }
    public object SolveArithOperation(Variable variable) => throw new NotImplementedException();
    public object SolveArithOperation(Variable leftVariable, Variable rightVariable){
        object result = new();
        if(leftVariable.Type != rightVariable.Type){
            if(
                (leftVariable.Type == DATATYPES.INTEGER || leftVariable.Type == DATATYPES.NATURAL) &&
                (rightVariable.Type == DATATYPES.INTEGER || rightVariable.Type == DATATYPES.NATURAL)
            ){
                result = SolveIntOperation((int)leftVariable.Value, (int)rightVariable.Value);
            }else
                throw new InvalidOperationException("The datatypes do not match");
        }else{
            switch(leftVariable.Type){
                case DATATYPES.INTEGER:
                    result = SolveIntOperation((int)leftVariable.Value, (int)rightVariable.Value);
                break;
                case DATATYPES.NATURAL:
                    result = SolveRealOperation((double)leftVariable.Value, (double)rightVariable.Value);
                break;
            }
        }
        return result;
    }
    public int SolveIntOperation(int leftVariable, int rightVariable){
        switch(OperatorName){
            case "+":
                return leftVariable + rightVariable;
            case "-":
                return leftVariable - rightVariable;
            case "*":
                return leftVariable * rightVariable;
            case "/":
                return leftVariable / rightVariable;
            case "mod":
                return leftVariable % rightVariable;
            default:
                return 0;
        }
    }
    public double SolveRealOperation(double leftVariable, double rightVariable){
        switch(OperatorName){
            case "+":
                return leftVariable + rightVariable;
            case "-":
                return leftVariable - rightVariable;
            case "*":
                return leftVariable * rightVariable;
            case "/":
                return leftVariable / rightVariable;
            case "mod":
                return leftVariable % rightVariable;
            default:
                return 0;
        }
    }
    public object SolveLogicOperation(Variable variable){
        object result = new();
        char item = Convert.ToChar(variable.Value);
        switch(OperatorName.ToLower()){
            case "not":
                result = item == '1' ? '0' : '1';
            break;
        }
        return result;
    }
    private object SolveVectorLogicOperation(Variable v1, Variable v2){
        if(v1.Size != v2.Size)
            throw new ParserException("Vectors do not match in size");
        string result = "", 
        vectorString1 = (string)v1.Value,
        vectorString2 = (string)v2.Value;
        for(int i = 0; i < v1.Size; i++){
            Variable valueA = new("tempA", vectorString1[i], DATATYPES.STD_LOGIC), 
            valueB = new("tempB", vectorString2[i], DATATYPES.STD_LOGIC);
            result += (string)SolveLogicOperation(valueA, valueB);
        }
        return result;
    }
}