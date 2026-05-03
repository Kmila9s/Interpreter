using Parser.Models;

namespace Interpreter.Models;

/// <summary>
/// Represents a user-defined VHDL procedure.
/// </summary>
/// <remarks>
/// <para><strong>VHDL Syntax:</strong></para>
/// <code>
/// procedure proc_name(signal a : in std_logic; variable b : out integer) is
///     -- local declarations
/// begin
///     -- statements
/// end proc_name;
/// </code>
/// <para><strong>Differences from Functions:</strong></para>
/// <list type="bullet">
/// <item>No return value (use return; to exit early)</item>
/// <item>Can have out/inout parameters that modify caller's variables</item>
/// <item>Can have signal parameters (functions cannot)</item>
/// <item>Can have wait statements (functions cannot)</item>
/// </list>
/// </remarks>
public class ProcedureDefinition
{
    /// <summary>
    /// Gets or sets the procedure name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets the list of parameters.
    /// </summary>
    public List<ParameterDefinition> Parameters { get; set; } = new();

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
    /// Gets or sets the local variable declarations for the procedure.
    /// </summary>
    public List<Variable> LocalVariables { get; set; } = new();

    /// <summary>
    /// Gets or sets the package this procedure belongs to.
    /// </summary>
    public string? PackageName { get; set; }

    /// <summary>
    /// Gets or sets whether this procedure has been synthesized.
    /// </summary>
    public bool IsSynthesized => SynthesizedBody != null;

    /// <summary>
    /// Creates a unique signature for the procedure (for overload resolution).
    /// </summary>
    public string GetSignature()
    {
        var paramTypes = string.Join(", ", Parameters.Select(p => $"{p.Mode} {p.Type}"));
        return $"{Name}({paramTypes})";
    }

    /// <summary>
    /// Checks if the given arguments match this procedure's parameter list.
    /// </summary>
    /// <param name="argCount">The number of arguments provided.</param>
    /// <returns>True if the argument count matches.</returns>
    public bool MatchesArgCount(int argCount)
    {
        int requiredParams = Parameters.Count(p => p.DefaultValue == null);
        return argCount >= requiredParams && argCount <= Parameters.Count;
    }

    /// <summary>
    /// Gets the out/inout parameters that need to be written back to caller.
    /// </summary>
    public IEnumerable<ParameterDefinition> GetOutputParameters()
    {
        return Parameters.Where(p => p.Mode == ParameterMode.Out || p.Mode == ParameterMode.InOut);
    }

    /// <summary>
    /// Returns a string representation of the procedure.
    /// </summary>
    public override string ToString()
    {
        var paramStr = string.Join("; ", Parameters);
        return $"procedure {Name}({paramStr})";
    }
}
