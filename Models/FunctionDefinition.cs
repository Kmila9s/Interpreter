using Parser.Models;

namespace Interpreter.Models;

/// <summary>
/// Represents a user-defined VHDL function.
/// </summary>
/// <remarks>
/// <para><strong>VHDL Syntax:</strong></para>
/// <code>
/// function func_name(param1 : type1; param2 : type2) return return_type is
///     -- local declarations
/// begin
///     -- statements
///     return expression;
/// end func_name;
/// </code>
/// <para><strong>Features:</strong></para>
/// <list type="bullet">
/// <item>Multiple parameters with optional modes</item>
/// <item>Local variable declarations</item>
/// <item>Return type specification</item>
/// <item>Lazy body synthesis on first call</item>
/// <item>Recursion support via execution stack</item>
/// </list>
/// </remarks>
public class FunctionDefinition
{
    /// <summary>
    /// Gets or sets the function name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets the list of parameters.
    /// </summary>
    public List<ParameterDefinition> Parameters { get; set; } = new();

    /// <summary>
    /// Gets or sets the return type.
    /// </summary>
    public Constants.DATATYPES ReturnType { get; set; } = Constants.DATATYPES.UNKNOWN;

    /// <summary>
    /// Gets or sets the custom return type name (for user-defined types).
    /// </summary>
    public string? CustomReturnTypeName { get; set; }

    /// <summary>
    /// Gets or sets the return type size (for vectors).
    /// </summary>
    public int ReturnTypeSize { get; set; } = 0;

    /// <summary>
    /// Gets or sets the raw body lines for deferred synthesis.
    /// </summary>
    public string[] BodyLines { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets or sets the raw body tokens for deferred synthesis.
    /// </summary>
    public string BodyRaw { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the synthesized body tasks (null until first call).
    /// </summary>
    public List<object>? SynthesizedBody { get; set; }

    /// <summary>
    /// Gets or sets the local variable declarations for the function.
    /// </summary>
    public List<Variable> LocalVariables { get; set; } = new();

    /// <summary>
    /// Gets or sets the package this function belongs to.
    /// </summary>
    public string? PackageName { get; set; }

    /// <summary>
    /// Gets or sets whether this is a pure function (no side effects).
    /// </summary>
    public bool IsPure { get; set; } = true;

    /// <summary>
    /// Gets or sets whether this function has been synthesized.
    /// </summary>
    public bool IsSynthesized => SynthesizedBody != null;

    /// <summary>
    /// Creates a unique signature for the function (for overload resolution).
    /// </summary>
    public string GetSignature()
    {
        var paramTypes = string.Join(", ", Parameters.Select(p => p.Type.ToString()));
        return $"{Name}({paramTypes}) : {ReturnType}";
    }

    /// <summary>
    /// Checks if the given arguments match this function's parameter list.
    /// </summary>
    /// <param name="argCount">The number of arguments provided.</param>
    /// <returns>True if the argument count matches.</returns>
    public bool MatchesArgCount(int argCount)
    {
        int requiredParams = Parameters.Count(p => p.DefaultValue == null);
        int optionalParams = Parameters.Count(p => p.DefaultValue != null);
        return argCount >= requiredParams && argCount <= Parameters.Count;
    }

    /// <summary>
    /// Returns a string representation of the function.
    /// </summary>
    public override string ToString()
    {
        var paramStr = string.Join("; ", Parameters);
        return $"function {Name}({paramStr}) return {CustomReturnTypeName ?? ReturnType.ToString().ToLower()}";
    }
}
