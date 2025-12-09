using Interpreter.Models;
using Parser.Models;

namespace Interpreter.Services;

/// <summary>
/// Registry for custom VHDL type definitions.
/// Provides storage and lookup for user-defined types within an architecture scope.
/// </summary>
/// <remarks>
/// <para>This registry supports:</para>
/// <list type="bullet">
/// <item>Enumeration types</item>
/// <item>Array types (constrained and unconstrained)</item>
/// <item>Record types</item>
/// <item>Subtypes</item>
/// </list>
/// <para>Types are scoped to their containing architecture.</para>
/// </remarks>
public class TypeRegistry
{
    private readonly Dictionary<string, TypeDefinition> _types = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets all registered type definitions.
    /// </summary>
    public IReadOnlyDictionary<string, TypeDefinition> Types => _types;

    /// <summary>
    /// Gets the number of registered types.
    /// </summary>
    public int Count => _types.Count;

    /// <summary>
    /// Registers a new type definition.
    /// </summary>
    /// <param name="typeDef">The type definition to register.</param>
    /// <exception cref="ArgumentException">Thrown if a type with the same name already exists.</exception>
    public void Register(TypeDefinition typeDef)
    {
        if (_types.ContainsKey(typeDef.Name))
        {
            throw new ArgumentException($"Type '{typeDef.Name}' is already defined in this scope.");
        }
        _types[typeDef.Name] = typeDef;
#if DEBUG
        Console.WriteLine($"[TypeRegistry] Registered type: {typeDef}");
#endif
    }

    /// <summary>
    /// Attempts to register a type definition without throwing on duplicates.
    /// </summary>
    /// <param name="typeDef">The type definition to register.</param>
    /// <returns>True if registered successfully, false if type already exists.</returns>
    public bool TryRegister(TypeDefinition typeDef)
    {
        if (_types.ContainsKey(typeDef.Name))
            return false;

        _types[typeDef.Name] = typeDef;
#if DEBUG
        Console.WriteLine($"[TypeRegistry] Registered type: {typeDef}");
#endif
        return true;
    }

    /// <summary>
    /// Checks if a type with the given name is registered.
    /// </summary>
    /// <param name="typeName">The type name to check.</param>
    /// <returns>True if the type is registered.</returns>
    public bool Contains(string typeName) => _types.ContainsKey(typeName);

    /// <summary>
    /// Gets a type definition by name.
    /// </summary>
    /// <param name="typeName">The type name to look up.</param>
    /// <returns>The type definition, or null if not found.</returns>
    public TypeDefinition? Get(string typeName)
    {
        return _types.TryGetValue(typeName, out var typeDef) ? typeDef : null;
    }

    /// <summary>
    /// Attempts to get a type definition by name.
    /// </summary>
    /// <param name="typeName">The type name to look up.</param>
    /// <param name="typeDef">The type definition if found.</param>
    /// <returns>True if the type was found.</returns>
    public bool TryGet(string typeName, out TypeDefinition? typeDef)
    {
        return _types.TryGetValue(typeName, out typeDef);
    }

    /// <summary>
    /// Checks if a type name represents a known type (built-in or custom).
    /// </summary>
    /// <param name="typeName">The type name to check.</param>
    /// <returns>True if it's a known type.</returns>
    public bool IsKnownType(string typeName)
    {
        // Check built-in types first
        var builtinType = Variable.GetDatatype(typeName);
        if (builtinType != Constants.DATATYPES.UNKNOWN)
            return true;

        // Then check custom types
        return _types.ContainsKey(typeName);
    }

    /// <summary>
    /// Gets the effective datatype for a type name.
    /// Returns the built-in type if it's a standard type, or UNKNOWN if it's a custom type
    /// (caller should then use GetCustomType to get full definition).
    /// </summary>
    /// <param name="typeName">The type name.</param>
    /// <returns>The datatype enum value.</returns>
    public Constants.DATATYPES GetDatatype(string typeName)
    {
        var builtinType = Variable.GetDatatype(typeName);
        if (builtinType != Constants.DATATYPES.UNKNOWN)
            return builtinType;

        // For custom types, we could return a special value
        // but for now we'll return UNKNOWN and let caller check registry
        return Constants.DATATYPES.UNKNOWN;
    }

    /// <summary>
    /// Gets the size in bits for a type.
    /// </summary>
    /// <param name="typeName">The type name.</param>
    /// <returns>Size in bits, or 0 if unknown.</returns>
    public int GetTypeSize(string typeName)
    {
        // Check custom types first
        if (_types.TryGetValue(typeName, out var typeDef))
        {
            return typeDef.TotalSize;
        }

        // Built-in type sizes
        var builtinType = Variable.GetDatatype(typeName);
        return builtinType switch
        {
            Constants.DATATYPES.STD_LOGIC => 1,
            Constants.DATATYPES.BIT => 1,
            Constants.DATATYPES.BOOLEAN => 1,
            Constants.DATATYPES.INTEGER => 32,
            Constants.DATATYPES.NATURAL => 32,
            // Note: REAL type not in Parser Constants yet
            // Vectors need their range parsed separately
            _ => 0
        };
    }

    /// <summary>
    /// Creates a default value for a type.
    /// </summary>
    /// <param name="typeName">The type name.</param>
    /// <returns>Default value for the type.</returns>
    public object GetDefaultValue(string typeName)
    {
        // Check custom types first
        if (_types.TryGetValue(typeName, out var typeDef))
        {
            return typeDef.GetDefaultValue();
        }

        // Built-in type defaults
        var builtinType = Variable.GetDatatype(typeName);
        return builtinType switch
        {
            Constants.DATATYPES.STD_LOGIC => "U",
            Constants.DATATYPES.BIT => "0",
            Constants.DATATYPES.BOOLEAN => false,
            Constants.DATATYPES.INTEGER => 0,
            Constants.DATATYPES.NATURAL => 0,
            Constants.DATATYPES.STD_LOGIC_VECTOR => "U",
            _ => "U"
        };
    }

    /// <summary>
    /// Clears all registered types.
    /// </summary>
    public void Clear() => _types.Clear();

    /// <summary>
    /// Gets all enumeration types.
    /// </summary>
    public IEnumerable<TypeDefinition> GetEnumerationTypes() =>
        _types.Values.Where(t => t.Kind == TypeKind.Enumeration);

    /// <summary>
    /// Gets all array types.
    /// </summary>
    public IEnumerable<TypeDefinition> GetArrayTypes() =>
        _types.Values.Where(t => t.Kind == TypeKind.Array);

    /// <summary>
    /// Gets all record types.
    /// </summary>
    public IEnumerable<TypeDefinition> GetRecordTypes() =>
        _types.Values.Where(t => t.Kind == TypeKind.Record);

    /// <summary>
    /// Gets a summary of all registered types.
    /// </summary>
    public string GetSummary()
    {
        if (_types.Count == 0)
            return "No custom types defined.";

        var lines = new List<string> { $"Custom Types ({_types.Count}):" };
        foreach (var typeDef in _types.Values)
        {
            lines.Add($"  - {typeDef}");
        }
        return string.Join(Environment.NewLine, lines);
    }
}
