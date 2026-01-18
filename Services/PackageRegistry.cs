using Parser.Models;

namespace Interpreter.Services;

/// <summary>
/// Registry for VHDL package constants and types.
/// Provides storage and lookup for constants defined in packages.
/// v0.15.0: Added to support package constant resolution.
/// </summary>
/// <remarks>
/// <para>This registry supports:</para>
/// <list type="bullet">
/// <item>Package constants (integer, std_logic_vector, etc.)</item>
/// <item>Generic constants with default values</item>
/// <item>Package-level type definitions</item>
/// </list>
/// <para>Common IEEE packages like ieee.numeric_std have pre-defined constants.</para>
/// </remarks>
public class PackageRegistry
{
    private static PackageRegistry? _instance;
    private static readonly object _lock = new();

    /// <summary>
    /// Gets the singleton instance of the PackageRegistry.
    /// </summary>
    public static PackageRegistry Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    _instance ??= new PackageRegistry();
                }
            }
            return _instance;
        }
    }

    private readonly Dictionary<string, PackageDefinition> _packages = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Variable> _globalConstants = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets all registered packages.
    /// </summary>
    public IReadOnlyDictionary<string, PackageDefinition> Packages => _packages;

    /// <summary>
    /// Gets all global constants from all packages.
    /// </summary>
    public IReadOnlyDictionary<string, Variable> GlobalConstants => _globalConstants;

    /// <summary>
    /// Registers a new package.
    /// </summary>
    /// <param name="package">The package definition to register.</param>
    public void RegisterPackage(PackageDefinition package)
    {
        _packages[package.Name] = package;

        // Add all constants to global constants
        foreach (var constant in package.Constants)
        {
            _globalConstants[constant.Name] = constant;
        }

#if DEBUG
        Console.WriteLine($"[PackageRegistry] Registered package: {package.Name} with {package.Constants.Count} constants");
#endif
    }

    /// <summary>
    /// Attempts to register a package without throwing on duplicates.
    /// </summary>
    /// <param name="package">The package definition to register.</param>
    /// <returns>True if registered successfully.</returns>
    public bool TryRegisterPackage(PackageDefinition package)
    {
        if (_packages.ContainsKey(package.Name))
            return false;

        RegisterPackage(package);
        return true;
    }

    /// <summary>
    /// Registers a single constant (useful for test constants or ad-hoc definitions).
    /// </summary>
    /// <param name="constant">The constant variable to register.</param>
    /// <param name="packageName">Optional package name to associate with.</param>
    public void RegisterConstant(Variable constant, string packageName = "global")
    {
        _globalConstants[constant.Name] = constant;

        if (!_packages.ContainsKey(packageName))
        {
            _packages[packageName] = new PackageDefinition { Name = packageName };
        }
        _packages[packageName].Constants.Add(constant);

#if DEBUG
        Console.WriteLine($"[PackageRegistry] Registered constant: {constant.Name} = {constant.Value} in package {packageName}");
#endif
    }

    /// <summary>
    /// Checks if a constant exists.
    /// </summary>
    /// <param name="constantName">The constant name to check.</param>
    /// <returns>True if the constant is registered.</returns>
    public bool ContainsConstant(string constantName) => _globalConstants.ContainsKey(constantName);

    /// <summary>
    /// Gets a constant by name.
    /// </summary>
    /// <param name="constantName">The constant name to look up.</param>
    /// <returns>The constant variable, or null if not found.</returns>
    public Variable? GetConstant(string constantName)
    {
        return _globalConstants.TryGetValue(constantName, out var constant) ? constant : null;
    }

    /// <summary>
    /// Attempts to get a constant by name.
    /// </summary>
    /// <param name="constantName">The constant name to look up.</param>
    /// <param name="constant">The constant variable if found.</param>
    /// <returns>True if the constant was found.</returns>
    public bool TryGetConstant(string constantName, out Variable? constant)
    {
        return _globalConstants.TryGetValue(constantName, out constant);
    }

    /// <summary>
    /// Gets the value of a constant.
    /// </summary>
    /// <param name="constantName">The constant name.</param>
    /// <returns>The constant value, or null if not found.</returns>
    public object? GetConstantValue(string constantName)
    {
        return _globalConstants.TryGetValue(constantName, out var constant) ? constant.Value : null;
    }

    /// <summary>
    /// Checks if a package exists.
    /// </summary>
    /// <param name="packageName">The package name to check.</param>
    /// <returns>True if the package is registered.</returns>
    public bool ContainsPackage(string packageName) => _packages.ContainsKey(packageName);

    /// <summary>
    /// Gets a package by name.
    /// </summary>
    /// <param name="packageName">The package name to look up.</param>
    /// <returns>The package definition, or null if not found.</returns>
    public PackageDefinition? GetPackage(string packageName)
    {
        return _packages.TryGetValue(packageName, out var package) ? package : null;
    }

    /// <summary>
    /// Clears all registered packages and constants.
    /// </summary>
    public void Clear()
    {
        _packages.Clear();
        _globalConstants.Clear();
    }

    /// <summary>
    /// Resets the singleton instance (for testing).
    /// </summary>
    public static void Reset()
    {
        lock (_lock)
        {
            _instance = null;
        }
    }

    /// <summary>
    /// Gets a summary of all registered packages and constants.
    /// </summary>
    public string GetSummary()
    {
        if (_packages.Count == 0)
            return "No packages registered.";

        var lines = new List<string> { $"Packages ({_packages.Count}):" };
        foreach (var pkg in _packages.Values)
        {
            lines.Add($"  - {pkg.Name}: {pkg.Constants.Count} constants");
            foreach (var constant in pkg.Constants)
            {
                lines.Add($"      {constant.Name} = {constant.Value}");
            }
        }
        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// Represents a VHDL package definition.
/// </summary>
public class PackageDefinition
{
    /// <summary>
    /// Gets or sets the package name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets the constants defined in this package.
    /// </summary>
    public List<Variable> Constants { get; } = new();

    /// <summary>
    /// Gets the types defined in this package.
    /// </summary>
    public TypeRegistry Types { get; } = new();

    /// <summary>
    /// Gets the functions defined in this package (names only for now).
    /// </summary>
    public List<string> Functions { get; } = new();

    /// <summary>
    /// Returns a string representation of the package.
    /// </summary>
    public override string ToString() => $"Package '{Name}': {Constants.Count} constants, {Types.Count} types";
}
