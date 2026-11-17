using Parser.Models;

namespace Interpreter.Models;

/// <summary>
/// Represents the kind of custom VHDL type.
/// </summary>
public enum TypeKind
{
    /// <summary>Enumeration type: type state_t is (IDLE, RUN, STOP);</summary>
    Enumeration,
    /// <summary>Array type: type mem_t is array (0 to 255) of std_logic;</summary>
    Array,
    /// <summary>Record type: type packet_t is record ... end record;</summary>
    Record,
    /// <summary>Subtype: subtype byte is std_logic_vector(7 downto 0);</summary>
    Subtype
}

/// <summary>
/// Represents a custom VHDL type definition.
/// Supports enumeration, array, record, and subtype declarations.
/// </summary>
/// <remarks>
/// <para>Examples of supported type declarations:</para>
/// <code>
/// -- Enumeration
/// type state_t is (IDLE, RUNNING, STOPPED);
///
/// -- Array (constrained)
/// type memory_t is array (0 to 1023) of std_logic_vector(7 downto 0);
///
/// -- Array (unconstrained) - partially supported
/// type data_array is array (natural range &lt;&gt;) of std_logic_vector(7 downto 0);
///
/// -- Record
/// type packet_t is record
///     valid : std_logic;
///     data  : std_logic_vector(31 downto 0);
/// end record;
///
/// -- Subtype
/// subtype byte is std_logic_vector(7 downto 0);
/// </code>
/// </remarks>
public class TypeDefinition
{
    /// <summary>
    /// Gets or sets the name of the custom type.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the kind of type (Enumeration, Array, Record, Subtype).
    /// </summary>
    public TypeKind Kind { get; set; }

    /// <summary>
    /// Gets or sets the base/element type for arrays and subtypes.
    /// For arrays, this is the element type (e.g., STD_LOGIC in "array of STD_LOGIC").
    /// For subtypes, this is the parent type.
    /// </summary>
    public Constants.DATATYPES BaseType { get; set; } = Constants.DATATYPES.UNKNOWN;

    /// <summary>
    /// Gets or sets the base type name as string (for custom base types).
    /// Used when BaseType is UNKNOWN but references another custom type.
    /// </summary>
    public string BaseTypeName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the enumeration values for enumeration types.
    /// Example: for "type state_t is (IDLE, RUN, STOP)", this contains ["IDLE", "RUN", "STOP"].
    /// </summary>
    public List<string> EnumValues { get; set; } = new();

    /// <summary>
    /// Gets or sets the array bounds for array types.
    /// Low is the lower bound, High is the upper bound.
    /// Example: for "array (0 to 255)", Low=0, High=255.
    /// </summary>
    public (int Low, int High) ArrayBounds { get; set; } = (0, 0);

    /// <summary>
    /// Gets or sets whether the array uses 'downto' (true) or 'to' (false) direction.
    /// </summary>
    public bool IsDownto { get; set; } = false;

    /// <summary>
    /// Gets or sets whether the array is unconstrained (uses "natural range &lt;&gt;").
    /// </summary>
    public bool IsUnconstrained { get; set; } = false;

    /// <summary>
    /// Gets or sets the element size in bits for array types.
    /// Example: for "array of std_logic_vector(7 downto 0)", ElementSize=8.
    /// For "array of std_logic", ElementSize=1.
    /// </summary>
    public int ElementSize { get; set; } = 1;

    /// <summary>
    /// Gets or sets the record fields for record types.
    /// Each Variable represents a field with its name, type, and size.
    /// </summary>
    public List<Variable> RecordFields { get; set; } = new();

    /// <summary>
    /// Gets or sets the subtype constraint range for subtypes.
    /// Example: for "subtype nibble is std_logic_vector(3 downto 0)", this is (3, 0).
    /// </summary>
    public (int High, int Low) SubtypeRange { get; set; } = (0, 0);

    /// <summary>
    /// Gets the total size in bits for this type.
    /// </summary>
    public int TotalSize
    {
        get
        {
            return Kind switch
            {
                TypeKind.Enumeration => (int)Math.Ceiling(Math.Log2(Math.Max(EnumValues.Count, 1))),
                TypeKind.Array => (ArrayBounds.High - ArrayBounds.Low + 1) * ElementSize,
                TypeKind.Record => RecordFields.Sum(f => f.Size),
                TypeKind.Subtype => SubtypeRange.High - SubtypeRange.Low + 1,
                _ => 1
            };
        }
    }

    /// <summary>
    /// Gets the array length (number of elements) for array types.
    /// </summary>
    public int ArrayLength => ArrayBounds.High - ArrayBounds.Low + 1;

    /// <summary>The array's bounds as written: <c>(0 to 3)</c> → (0, 3), <c>(7 downto 0)</c> → (7, 0).</summary>
    public (int Left, int Right) DeclaredRange =>
        IsDownto ? (ArrayBounds.High, ArrayBounds.Low) : (ArrayBounds.Low, ArrayBounds.High);

    /// <summary>
    /// Storage slot of element <paramref name="index"/> (slot 0 is the leftmost
    /// element, as <see cref="Variable.IndexToOffset"/> maps it), or -1 when the
    /// index is outside the array.
    /// </summary>
    public int SlotOf(int index)
    {
        int slot = Variable.OffsetInRange(DeclaredRange.Left, IsDownto, index);
        return slot >= 0 && slot < ArrayLength ? slot : -1;
    }

    /// <summary>
    /// Creates a default value for this type.
    /// </summary>
    /// <returns>Default value appropriate for the type kind.</returns>
    public object GetDefaultValue()
    {
        return Kind switch
        {
            TypeKind.Enumeration => EnumValues.Count > 0 ? EnumValues[0] : "UNKNOWN",
            TypeKind.Array => CreateDefaultArrayValue(),
            TypeKind.Record => CreateDefaultRecordValue(),
            TypeKind.Subtype => new string('U', TotalSize),
            _ => "U"
        };
    }

    /// <summary>
    /// Creates a default array value (all elements set to default).
    /// </summary>
    private object CreateDefaultArrayValue()
    {
        // For simple arrays of std_logic, return string of 'U's
        if (BaseType == Constants.DATATYPES.STD_LOGIC ||
            BaseType == Constants.DATATYPES.BIT)
        {
            return new string('U', ArrayLength);
        }

        // For arrays of vectors, create array of default vectors
        if (BaseType == Constants.DATATYPES.STD_LOGIC_VECTOR)
        {
            var defaultElement = new string('U', ElementSize);
            return Enumerable.Repeat(defaultElement, ArrayLength).ToArray();
        }

        // For other types, return placeholder
        return $"(array:{Name})";
    }

    /// <summary>
    /// Creates a default record value (all fields set to default).
    /// </summary>
    private object CreateDefaultRecordValue()
    {
        var values = new Dictionary<string, object>();
        foreach (var field in RecordFields)
        {
            values[field.Name] = field.Value ?? new string('U', Math.Max(field.Size, 1));
        }
        return values;
    }

    /// <summary>
    /// Returns a string representation of this type definition.
    /// </summary>
    public override string ToString()
    {
        return Kind switch
        {
            TypeKind.Enumeration => $"type {Name} is ({string.Join(", ", EnumValues)});",
            TypeKind.Array => $"type {Name} is array ({ArrayBounds.Low} {(IsDownto ? "downto" : "to")} {ArrayBounds.High}) of {BaseTypeName};",
            TypeKind.Record => $"type {Name} is record ({RecordFields.Count} fields);",
            TypeKind.Subtype => $"subtype {Name} is {BaseTypeName}({SubtypeRange.High} downto {SubtypeRange.Low});",
            _ => $"type {Name};"
        };
    }
}
