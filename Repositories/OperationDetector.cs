using System.Text.RegularExpressions;
using Interpreter.Models;

namespace Interpreter.Repositories;

/// <summary>
/// Detects the type of operation from a raw string input.
/// </summary>
public class OperationDetector {
    private readonly Regex _assingationRegex;

    /// <summary>
    /// Initializes a new instance of the <see cref="OperationDetector"/> class.
    /// </summary>
    public OperationDetector(){
        RegexOptions options = RegexOptions.Compiled;
        _assingationRegex = new(OperationsRegex.ASSIGNATION, options);
    }

    /// <summary>
    /// Gets the type of the operation.
    /// </summary>
    /// <param name="RawContent">The raw content of the operation.</param>
    /// <returns>The type of the operation.</returns>
    public OperationType GetOperationType(string RawContent) =>
        _assingationRegex.IsMatch(RawContent) ? 
            OperationType.ASSIGNATION :
            OperationType.UNKNOWN;
}