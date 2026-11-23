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

    /// <summary>The declared type mark (lower case), as <see cref="Variable.TypeMark"/>.</summary>
    public string? TypeMark { get; set; }

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

    /// <summary>The index constraint as written (<c>(7 downto 0)</c> → (7, 0, true)); null when none could be folded.</summary>
    public (int Left, int Right, bool IsDownto)? Range { get; set; }

    /// <summary>
    /// A vector formal written without an index constraint (<c>s : std_logic_vector</c>):
    /// it takes its actual's shape at each call (G101).
    /// </summary>
    public bool IsUnconstrained { get; set; }

    /// <summary>
    /// Gets or sets the optional default value for the parameter.
    /// </summary>
    public object? DefaultValue { get; set; }

    /// <summary>
    /// Creates a Variable instance from this parameter definition with a given value.
    /// </summary>
    /// <param name="value">The value to assign to the variable.</param>
    /// <returns>A new Variable instance.</returns>
    public Variable CreateVariable(object? value = null, Variable? actual = null)
    {
        var variable = new Variable(Name, value?.ToString() ?? "", Type)
        {
            CustomTypeName = CustomTypeName,
            TypeMark = TypeMark,
        };

        if (Type != Constants.DATATYPES.STD_LOGIC_VECTOR) return variable;

        // Set once: the Size setter fits the value to it.
        if (!IsUnconstrained)
        {
            variable.Size = Math.Max(Size, 1);
            if (Range is var (left, right, downto)) variable.SetDeclaredRange(left, right, downto);
        }
        else if (actual is { Size: > 0 })
        {
            variable.Size = actual.Size;
            if (actual.HasDeclaredRange)
                variable.SetDeclaredRange(actual.LeftBound!.Value, actual.RightBound!.Value, actual.IsDownto);
        }
        else
        {
            // A value without its object (a literal actual): (length-1 downto 0).
            variable.Size = value is string bits && bits.Length > 0 && StdLogic.IsVector(bits) ? bits.Length : 1;
        }
        return variable;
    }

    /// <summary>The shape of the formal <see cref="CreateVariable"/> makes for this call: what a body synthesised for it depends on.</summary>
    public static string ShapeKey(Variable formal) => $"{formal.Size}:{formal.LeftBound}:{formal.RightBound}:{formal.IsDownto}";

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
