using System.Text.RegularExpressions;
using Interpreter.Models;

namespace Interpreter.Repositories;
public class OperationDetector {
    private readonly Regex _assingationRegex;
    public OperationDetector(){
        RegexOptions options = RegexOptions.Compiled;
        _assingationRegex = new(OperationsRegex.ASSIGNATION, options);
    }
    public OperationType GetOperationType(string RawContent) =>
        _assingationRegex.IsMatch(RawContent) ? 
            OperationType.ASSIGNATION :
            OperationType.UNKNOWN;
}