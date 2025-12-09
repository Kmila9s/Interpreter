using System.Collections;
using System.Text.RegularExpressions;
using Interpreter.Interfaces;
using Interpreter.Models;
using Parser.Models;
using static Parser.Models.Constants;

namespace Interpreter.Services;

/// <summary>
/// Represents an operator.
/// </summary>
public class Operator : IOperation, ILogicOperation, IArithOperation{
    /// <summary>
    /// Gets the type of the operator.
    /// </summary>
    public OperatorType OperatorType { get; private set; }

    /// <summary>
    /// Gets the precedence of the operator.
    /// </summary>
    public object Precedence { get; private set; } = new();
    private readonly Regex _assignationRegex, _arithRegex, _logicRegex, _comparationRegex;

    /// <summary>
    /// Gets the name of the operator.
    /// </summary>
    public string OperatorName { get; private set; } = string.Empty;

    /// <summary>
    /// Initializes a new instance of the <see cref="Operator"/> class.
    /// </summary>
    /// <param name="Operator">The name of the operator.</param>
    public Operator(string Operator){
        this.OperatorName = Operator;
        RegexOptions options = RegexOptions.Compiled | RegexOptions.IgnoreCase;
        _assignationRegex = new(ASSIGNATION_OPERATORS, options);
        _arithRegex = new(ARITH_OPERATORS, options);
        _comparationRegex = new(COMPARATION_OPERATORS, options);
        _logicRegex = new(LOGIC_OPERATORS, options);
        this.OperatorType = DefineOperatorType(Operator);
        DefinePrecedence(Operator);
    }

    /// <summary>
    /// Defines the precedence of the operator.
    /// </summary>
    /// <param name="Operator">The name of the operator.</param>
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
                    case "**":
                        Precedence = PrecedencesArithOperators.UNARY; // Power has high precedence
                    break;
                    case "/":
                        Precedence = PrecedencesArithOperators.DIVISION;
                    break;
                    case "mod":
                        Precedence = PrecedencesArithOperators.UNARY;
                    break;
                }
            break;
            case OperatorType.COMPARATION:
                Precedence = PrecedencesLogicOperators.COMPARATION;
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

    /// <summary>
    /// Defines the type of the operator.
    /// </summary>
    /// <param name="Operator">The name of the operator.</param>
    /// <returns>The type of the operator.</returns>
    private OperatorType DefineOperatorType(string Operator) =>
        _comparationRegex.IsMatch(Operator) ?
            OperatorType.COMPARATION :
        _assignationRegex.IsMatch(Operator) ?
            OperatorType.ASSIGNATION :
        _arithRegex.IsMatch(Operator) ?
            OperatorType.ARITH_OPERATION :
        _logicRegex.IsMatch(Operator) ?
            OperatorType.LOGIC_OPERATION :
        
            OperatorType.UNKNOWN;

    /// <summary>
    /// Executes the operation.
    /// </summary>
    /// <returns>The result of the operation.</returns>
    public object Execute() => throw new NotImplementedException();

    /// <summary>
    /// Executes the operation.
    /// </summary>
    /// <param name="array">The array of items.</param>
    /// <param name="index">The index of the item.</param>
    /// <returns>The result of the operation.</returns>
    public object Execute(object[] array, int index) => throw new NotImplementedException();

    /// <summary>
    /// Executes the operation.
    /// </summary>
    /// <param name="variable">The variable.</param>
    /// <returns>The result of the operation.</returns>
    public object Execute(Variable variable) =>
        (PrecedencesLogicOperators)Precedence == PrecedencesLogicOperators.NOT ?
            SolveLogicOperation(variable) :
            new NotImplementedException();

    /// <summary>
    /// Executes the operation.
    /// </summary>
    /// <param name="leftSideOperator">The left side operator.</param>
    /// <param name="rightSideOperator">The right side operator.</param>
    /// <returns>The result of the operation.</returns>
    public object Execute(Variable leftSideOperator, Variable rightSideOperator){
        if(leftSideOperator.Type != rightSideOperator.Type)
            throw ParserException.TypeMismatch(
                leftSideOperator.Type.ToString(),
                rightSideOperator.Type.ToString(),
                null);
        object result = new();
        switch(OperatorType){
            case OperatorType.ASSIGNATION:
                result = rightSideOperator.Value;
            break;
            case OperatorType.COMPARATION:
                result = SolveComparation(leftSideOperator, rightSideOperator);
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
            case OperatorType.ARITH_OPERATION:
                result = SolveArithOperation(leftSideOperator, rightSideOperator);
                break;
            default:
                throw new NotImplementedException();
        }
        return result;
    }

    /// <summary>
    /// Solves a comparation operation.
    /// </summary>
    /// <param name="var1">The first variable.</param>
    /// <param name="var2">The second variable.</param>
    /// <returns>The result of the comparation.</returns>
    public object SolveComparation(Variable var1, Variable var2)
    {
        switch (var1.Type)
        {
            case DATATYPES.BOOLEAN:
                switch (OperatorName.ToLower())
                {
                    case "=":
                        return bool.Parse((string)var1.Value) == bool.Parse((string)var2.Value);
                    case "!=":
                        return bool.Parse((string)var1.Value) != bool.Parse((string)var2.Value);
                    default:
                        throw new Exception($"Operator type do not detected or even supported: {OperatorName}");
                }
            case DATATYPES.STD_LOGIC:
            case DATATYPES.INTEGER:
            case DATATYPES.BIT:
            case DATATYPES.NATURAL:
                switch (OperatorName.ToLower())
                {
                    case "=":
                        return int.Parse((string)var1.Value) == int.Parse((string)var2.Value);
                    case "!=":
                    case "/=":
                        return int.Parse((string)var1.Value) != int.Parse((string)var2.Value);
                    case ">":
                        return int.Parse((string)var1.Value) > int.Parse((string)var2.Value);
                    case "<":
                        return int.Parse((string)var1.Value) < int.Parse((string)var2.Value);
                    case ">=":
                        return int.Parse((string)var1.Value) >= int.Parse((string)var2.Value);
                    case "<=":
                        return int.Parse((string)var1.Value) <= int.Parse((string)var2.Value);
                    default:
                        throw new Exception($"Operator type do not detected or even supported: {OperatorName}");
                }
            case DATATYPES.STD_LOGIC_VECTOR:
                switch (OperatorName.ToLower())
                {
                    case "=":
                        return ((string)var1.Value).Equals((string)var2.Value);
                    case "!=":
                    case "/=":
                        return !((string)var1.Value).Equals((string)var2.Value);
                    default:
                        throw new Exception($"Operator type do not detected or even supported for vectors: {OperatorName}");
                }
            default:
                throw new Exception($"Datatype do not detected or even supported: {OperatorName}");
        }
    }

    /// <summary>
    /// Solves a logic operation.
    /// </summary>
    /// <param name="var1">The first variable.</param>
    /// <param name="var2">The second variable.</param>
    /// <returns>The result of the logic operation.</returns>
    public object SolveLogicOperation(Variable var1, Variable var2)
    {
        char a = Convert.ToChar(var1.Value?.ToString()?.Replace("'", "") ?? throw new Exception("Malformed data"));
        char b = Convert.ToChar(var2.Value?.ToString()?.Replace("'", "") ?? throw new Exception("Malformed data"));
        char result;
        switch (OperatorName.ToLower())
        {
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

    /// <summary>
    /// Solves an arithmetic operation.
    /// </summary>
    /// <param name="variable">The variable.</param>
    /// <returns>The result of the arithmetic operation.</returns>
    public object SolveArithOperation(Variable variable) => throw new NotImplementedException();

    /// <summary>
    /// Solves an arithmetic operation.
    /// </summary>
    /// <param name="leftVariable">The left variable.</param>
    /// <param name="rightVariable">The right variable.</param>
    /// <returns>The result of the arithmetic operation.</returns>
    public object SolveArithOperation(Variable leftVariable, Variable rightVariable){
        object result = new();
        // Helper to safely get integer from variable value
        int GetInt(Variable v) {
            if (v.Value == null || v.Value.GetType() == typeof(object))
            {
                // Uninitialized variable - try to use 0 or parse name if it's a literal
                if (int.TryParse(v.Name?.Replace("Literal-", "").Split('-')[0], out int parsed))
                    return parsed;
                return 0;
            }
            return Convert.ToInt32(v.Value);
        }
        double GetDouble(Variable v) {
            if (v.Value == null || v.Value.GetType() == typeof(object))
            {
                if (double.TryParse(v.Name?.Replace("Literal-", "").Split('-')[0], out double parsed))
                    return parsed;
                return 0.0;
            }
            return Convert.ToDouble(v.Value);
        }

        if(leftVariable.Type != rightVariable.Type){
            if(
                (leftVariable.Type == DATATYPES.INTEGER || leftVariable.Type == DATATYPES.NATURAL) &&
                (rightVariable.Type == DATATYPES.INTEGER || rightVariable.Type == DATATYPES.NATURAL)
            ){
                result = SolveIntOperation(GetInt(leftVariable), GetInt(rightVariable));
            }else
                throw new InvalidOperationException($"The datatypes do not match: {leftVariable.Name}:{leftVariable.Type} - {rightVariable.Name}:{rightVariable.Name}");
        }else{
            switch(leftVariable.Type){
                case DATATYPES.INTEGER:
                    result = SolveIntOperation(GetInt(leftVariable), GetInt(rightVariable));
                break;
                case DATATYPES.NATURAL:
                    result = SolveRealOperation(GetDouble(leftVariable), GetDouble(rightVariable));
                break;
            }
        }
        return result;
    }

    /// <summary>
    /// Solves an integer operation.
    /// </summary>
    /// <param name="leftVariable">The left variable.</param>
    /// <param name="rightVariable">The right variable.</param>
    /// <returns>The result of the integer operation.</returns>
    public int SolveIntOperation(int leftVariable, int rightVariable){
        switch(OperatorName){
            case "+":
                return leftVariable + rightVariable;
            case "-":
                return leftVariable - rightVariable;
            case "*":
                return leftVariable * rightVariable;
            case "**":
                return (int)Math.Pow(leftVariable, rightVariable);
            case "/":
                return leftVariable / rightVariable;
            case "mod":
                return leftVariable % rightVariable;
            default:
                return 0;
        }
    }

    /// <summary>
    /// Solves a real operation.
    /// </summary>
    /// <param name="leftVariable">The left variable.</param>
    /// <param name="rightVariable">The right variable.</param>
    /// <returns>The result of the real operation.</returns>
    public double SolveRealOperation(double leftVariable, double rightVariable){
        switch(OperatorName){
            case "+":
                return leftVariable + rightVariable;
            case "-":
                return leftVariable - rightVariable;
            case "*":
                return leftVariable * rightVariable;
            case "**":
                return Math.Pow(leftVariable, rightVariable);
            case "/":
                return leftVariable / rightVariable;
            case "mod":
                return leftVariable % rightVariable;
            default:
                return 0;
        }
    }

    /// <summary>
    /// Solves a logic operation.
    /// </summary>
    /// <param name="variable">The variable.</param>
    /// <returns>The result of the logic operation.</returns>
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

    /// <summary>
    /// Solves a vector logic operation.
    /// </summary>
    /// <param name="v1">The first vector.</param>
    /// <param name="v2">The second vector.</param>
    /// <returns>The result of the vector logic operation.</returns>
    private object SolveVectorLogicOperation(Variable v1, Variable v2)
    {
        if (v1.Size != v2.Size)
            throw new ParserException(
                $"Vector size mismatch in '{OperatorName}' operation: '{v1.Name}' has {v1.Size} bits, '{v2.Name}' has {v2.Size} bits");
        string result = "",
        vectorString1 = (string)v1.Value,
        vectorString2 = (string)v2.Value;
        for (int i = 0; i < v1.Size; i++)
        {
            Variable valueA = new("tempA", vectorString1[i], DATATYPES.STD_LOGIC),
            valueB = new("tempB", vectorString2[i], DATATYPES.STD_LOGIC);
            result += Convert.ToString(SolveLogicOperation(valueA, valueB));
        }
        return result;
    }
}