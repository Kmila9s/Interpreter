using Parser.Models;

namespace Interpreter.Models;

/// <summary>
/// Defines the mode of a parameter in a function or procedure.
/// </summary>
public enum ParameterMode
{
    /// <summary>Input parameter (default, read-only).</summary>
    In,
    /// <summary>Output parameter (write-only, modified by callee).</summary>
    Out,
    /// <summary>Input/Output parameter (read-write, modified by callee).</summary>
    InOut
}

/// <summary>
/// Represents a parameter definition for a VHDL function or procedure.
/// </summary>
/// <remarks>
/// <para><strong>VHDL Syntax Examples:</strong></para>
/// <code>
/// function add(a, b : integer) return integer;
/// procedure swap(variable x, y : inout integer);
/// function to_vector(val : integer; size : natural) return std_logic_vector;
/// </code>
/// </remarks>
public class ParameterDefinition
{
    /// <summary>
    /// Gets or sets the parameter name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the parameter data type.
    /// </summary>
    public Constants.DATATYPES Type { get; set; } = Constants.DATATYPES.UNKNOWN;

    /// <summary>
    /// Gets or sets the custom type name (for user-defined types).
    /// </summary>
    public string? CustomTypeName { get; set; }

    /// <summary>
    /// Gets or sets the parameter mode (In, Out, InOut).
    /// </summary>
    public ParameterMode Mode { get; set; } = ParameterMode.In;

    /// <summary>
    /// F-G-14 (Session 23, 2026-07-18): true when the formal is declared
    /// with the <c>signal</c> object class. Signals passed as procedure
    /// arguments must be aliased to the caller's Variable object at call
    /// time so <c>&lt;=</c> inside the procedure body schedules against
    /// the caller's driver via the delta cycle rather than writing to a
    /// local shadow. The <c>signal</c> / <c>variable</c> keyword before
    /// the name list is captured in <see cref="Services.VariableSyntetizer"/>'s
    /// parameter parser (previously silently dropped).
    /// </summary>
    public bool IsSignal { get; set; } = false;

    /// <summary>
    /// Gets or sets the size for vector types.
    /// </summary>
    public int Size { get; set; } = 0;

    /// <summary>
    /// Gets or sets the vector high bound (for range types).
    /// </summary>
    public int HighBound { get; set; } = 0;

    /// <summary>
    /// Gets or sets the vector low bound (for range types).
    /// </summary>
    public int LowBound { get; set; } = 0;

    /// <summary>
    /// Gets or sets the optional default value for the parameter.
    /// </summary>
    public object? DefaultValue { get; set; }

    /// <summary>
    /// Creates a Variable instance from this parameter definition with a given value.
    /// </summary>
    /// <param name="value">The value to assign to the variable.</param>
    /// <returns>A new Variable instance.</returns>
    public Variable CreateVariable(object? value = null)
    {
        var variable = new Variable(Name, value?.ToString() ?? "", Type)
        {
            CustomTypeName = CustomTypeName
        };

        if (Type == Constants.DATATYPES.STD_LOGIC_VECTOR)
        {
            variable.Size = Size > 0 ? Size : (HighBound - LowBound + 1);
        }

        return variable;
    }

    /// <summary>
    /// Returns a string representation of the parameter.
    /// </summary>
    public override string ToString()
    {
        string modeStr = Mode switch
        {
            ParameterMode.Out => "out ",
            ParameterMode.InOut => "inout ",
            _ => ""
        };
        string typeStr = CustomTypeName ?? Type.ToString().ToLower();
        return $"{modeStr}{Name} : {typeStr}";
    }
}
