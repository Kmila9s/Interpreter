using Interpreter.Models;
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
    private readonly Dictionary<string, FunctionDefinition> _globalFunctions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ProcedureDefinition> _globalProcedures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// v1.17.0 (P3): every identifier that appears in a declaration position in
    /// the source under analysis (signals, constants, variables, ports, generics,
    /// subprogram names and their parameters, types/subtypes, aliases, components,
    /// loop variables, record fields, enum literals). Populated up-front by
    /// <see cref="RegisterDeclaredNamesFromSource"/>. The undeclared-identifier
    /// check consults this so it rejects only names that are declared NOWHERE in
    /// the source (matching GHDL's "no declaration for X"), not names that Kmila's
    /// scope-builder merely failed to evaluate into the live Variable set (e.g. a
    /// local constant whose initializer references a generic).
    /// </summary>
    private readonly HashSet<string> _declaredNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How <c>std_logic_vector</c> operands read as numbers in arithmetic and
    /// relational operators: the package the design's <c>use</c> clauses make
    /// visible — <c>std_logic_signed</c> (signed), <c>std_logic_unsigned</c> /
    /// <c>numeric_std_unsigned</c> (unsigned, also the default). <c>signed</c> and
    /// <c>unsigned</c> operands always follow their own type.
    /// </summary>
    public Signedness VectorArithmetic { get; private set; } = Signedness.Unsigned;

    /// <summary>Instance prefix (<c>u1__</c>; empty for the top) → vector arithmetic of the unit inlined there.</summary>
    private readonly Dictionary<string, Signedness> _unitArithmetic = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The numeric package the <c>use</c> clauses of <paramref name="context"/> make
    /// visible for std_logic_vector — the last one written, as a context clause
    /// applies to the unit right after it — or null when there is none.
    /// </summary>
    public static Signedness? ArithmeticOf(string context)
    {
        var matches = System.Text.RegularExpressions.Regex.Matches(VhdlText.StripLineComments(context ?? string.Empty),
            @"\buse\s+ieee\s*\.\s*(std_logic_signed|std_logic_unsigned|numeric_std_unsigned)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (matches.Count == 0) return null;
        return matches[^1].Groups[1].Value.Equals("std_logic_signed", StringComparison.OrdinalIgnoreCase)
            ? Signedness.Signed : Signedness.Unsigned;
    }

    /// <summary>Records the numeric package of a design source's <c>use</c> clauses as the default (<see cref="VectorArithmetic"/>).</summary>
    public void RegisterContextClauses(string source)
    {
        if (ArithmeticOf(source) is { } s) VectorArithmetic = s;
    }

    /// <summary>Records the vector arithmetic of the unit inlined under <paramref name="prefix"/> (its context clause <paramref name="context"/>).</summary>
    public void RegisterUnitArithmetic(string prefix, string context) =>
        _unitArithmetic[prefix] = ArithmeticOf(context) ?? Signedness.Unsigned;

    /// <summary>
    /// How a std_logic_vector object named <paramref name="name"/> reads as a number:
    /// the package of the unit it belongs to (longest instance prefix), else the default.
    /// </summary>
    public Signedness ArithmeticFor(string? name)
    {
        string best = string.Empty;
        Signedness? found = null;
        foreach (var (prefix, s) in _unitArithmetic)
            if ((name ?? string.Empty).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && prefix.Length >= best.Length)
            {
                best = prefix;
                found = s;
            }
        return found ?? VectorArithmetic;
    }

    /// <summary>
    /// Gets all registered packages.
    /// </summary>
    public IReadOnlyDictionary<string, PackageDefinition> Packages => _packages;

    /// <summary>
    /// Gets all global constants from all packages.
    /// </summary>
    public IReadOnlyDictionary<string, Variable> GlobalConstants => _globalConstants;

    /// <summary>
    /// Gets all global functions from all packages.
    /// </summary>
    public IReadOnlyDictionary<string, FunctionDefinition> GlobalFunctions => _globalFunctions;

    /// <summary>
    /// Gets all global procedures from all packages.
    /// </summary>
    public IReadOnlyDictionary<string, ProcedureDefinition> GlobalProcedures => _globalProcedures;

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
    /// Registers a function globally.
    /// </summary>
    /// <param name="function">The function definition to register.</param>
    /// <param name="packageName">Optional package name to associate with.</param>
    public void RegisterFunction(FunctionDefinition function, string packageName = "global")
    {
        _globalFunctions[function.Name] = function;
        function.PackageName = packageName;

        if (!_packages.ContainsKey(packageName))
        {
            _packages[packageName] = new PackageDefinition { Name = packageName };
        }
        _packages[packageName].RegisterFunction(function);

#if DEBUG
        Console.WriteLine($"[PackageRegistry] Registered function: {function.Name} in package {packageName}");
#endif
    }

    /// <summary>
    /// Registers a procedure globally.
    /// </summary>
    /// <param name="procedure">The procedure definition to register.</param>
    /// <param name="packageName">Optional package name to associate with.</param>
    public void RegisterProcedure(ProcedureDefinition procedure, string packageName = "global")
    {
        _globalProcedures[procedure.Name] = procedure;
        procedure.PackageName = packageName;

        if (!_packages.ContainsKey(packageName))
        {
            _packages[packageName] = new PackageDefinition { Name = packageName };
        }
        _packages[packageName].RegisterProcedure(procedure);

#if DEBUG
        Console.WriteLine($"[PackageRegistry] Registered procedure: {procedure.Name} in package {packageName}");
#endif
    }

    /// <summary>
    /// Checks if a function exists.
    /// </summary>
    /// <param name="functionName">The function name to check.</param>
    /// <returns>True if the function is registered.</returns>
    public bool ContainsFunction(string functionName) => _globalFunctions.ContainsKey(functionName);

    /// <summary>
    /// Gets a function by name.
    /// </summary>
    /// <param name="functionName">The function name to look up.</param>
    /// <returns>The function definition, or null if not found.</returns>
    public FunctionDefinition? GetFunction(string functionName)
    {
        return _globalFunctions.TryGetValue(functionName, out var func) ? func : null;
    }

    /// <summary>
    /// Attempts to get a function by name.
    /// </summary>
    /// <param name="functionName">The function name to look up.</param>
    /// <param name="function">The function definition if found.</param>
    /// <returns>True if the function was found.</returns>
    public bool TryGetFunction(string functionName, out FunctionDefinition? function)
    {
        return _globalFunctions.TryGetValue(functionName, out function);
    }

    /// <summary>
    /// Checks if a procedure exists.
    /// </summary>
    /// <param name="procedureName">The procedure name to check.</param>
    /// <returns>True if the procedure is registered.</returns>
    public bool ContainsProcedure(string procedureName) => _globalProcedures.ContainsKey(procedureName);

    /// <summary>
    /// Gets a procedure by name.
    /// </summary>
    /// <param name="procedureName">The procedure name to look up.</param>
    /// <returns>The procedure definition, or null if not found.</returns>
    public ProcedureDefinition? GetProcedure(string procedureName)
    {
        return _globalProcedures.TryGetValue(procedureName, out var proc) ? proc : null;
    }

    /// <summary>
    /// Attempts to get a procedure by name.
    /// </summary>
    /// <param name="procedureName">The procedure name to look up.</param>
    /// <param name="procedure">The procedure definition if found.</param>
    /// <returns>True if the procedure was found.</returns>
    public bool TryGetProcedure(string procedureName, out ProcedureDefinition? procedure)
    {
        return _globalProcedures.TryGetValue(procedureName, out procedure);
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
    /// The constant named by a selected name (<c>work.pkg.c</c>, <c>pkg.c</c>):
    /// <paramref name="constantName"/> declared in package <paramref name="packageName"/>, or null.
    /// </summary>
    public Variable? GetConstant(string packageName, string constantName) =>
        GetPackage(packageName)?.Constants.FirstOrDefault(c =>
            c.Name.Equals(constantName, StringComparison.OrdinalIgnoreCase));

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
    /// Clears all registered packages, constants, functions, and procedures.
    /// </summary>
    public void Clear()
    {
        _packages.Clear();
        _globalConstants.Clear();
        _globalFunctions.Clear();
        _globalProcedures.Clear();
        _declaredNames.Clear();
        VectorArithmetic = Signedness.Unsigned;
        _unitArithmetic.Clear();
    }

    /// <summary>
    /// True when <paramref name="name"/> names a predefined/IEEE function, a
    /// registered subprogram, a package function/procedure/constant, or anything
    /// declared in the analysed source — the single "is it declared?" rule behind
    /// <c>no declaration for "x"</c> (expressions and procedure calls).
    /// </summary>
    public static bool IsKnownName(string name) =>
        StandardFunctions.IsStandardFunction(name)
        || FunctionRegistry.Instance.IsKnown(name)
        || Instance.ContainsFunction(name)
        || Instance.ContainsProcedure(name)
        || Instance.ContainsConstant(name)
        || Instance.IsDeclaredName(name);

    /// <summary>
    /// v1.17.0 (P3): true when <paramref name="name"/> was seen in a declaration
    /// position in the analysed source. See <see cref="_declaredNames"/>.
    /// </summary>
    public bool IsDeclaredName(string name) =>
        !string.IsNullOrEmpty(name) && _declaredNames.Contains(name);

    /// <summary>
    /// v1.17.0 (P3): scan one source file's text for declaration positions and
    /// record every declared identifier in <see cref="_declaredNames"/>. The scan
    /// is deliberately generous — it collects any identifier that follows a
    /// declaration keyword (<c>signal/constant/variable/type/subtype/function/
    /// procedure/component/entity/architecture/alias/file/for</c>) or sits
    /// immediately before a <c>:</c> (ports, generics, parameters, record fields,
    /// labels) or inside an enum <c>type … is ( … )</c> list. Over-collecting is
    /// safe here: it can only suppress a rejection, never create one, so a
    /// genuinely-undeclared identifier (which appears in no declaration position)
    /// is still caught.
    /// </summary>
    public void RegisterDeclaredNamesFromSource(string source)
    {
        if (string.IsNullOrEmpty(source)) return;

        void Add(string n)
        {
            if (!string.IsNullOrWhiteSpace(n)) _declaredNames.Add(n.Trim());
        }

        // identifier(s) before a ':' that is NOT ':=' — ports, generics,
        // parameters, signals/constants/variables, record fields, labels.
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(
                     source,
                     @"([A-Za-z_]\w*(?:\s*,\s*[A-Za-z_]\w*)*)\s*:(?!=)"))
        {
            foreach (var part in m.Groups[1].Value.Split(','))
                Add(part);
        }

        // identifier directly following a declaration keyword.
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(
                     source,
                     @"\b(?:signal|constant|variable|file|alias|type|subtype|component|entity|architecture|function|procedure|package|for)\s+([A-Za-z_]\w*)",
                     System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            Add(m.Groups[1].Value);
        }

        // enum literals: `type NAME is ( a, b, c )`.
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(
                     source,
                     @"\btype\s+[A-Za-z_]\w*\s+is\s*\(([^)]*)\)",
                     System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            foreach (var part in m.Groups[1].Value.Split(','))
                Add(part);
        }
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
    /// Gets the function names defined in this package (for backwards compatibility).
    /// </summary>
    public List<string> FunctionNames { get; } = new();

    /// <summary>
    /// Gets the function definitions in this package.
    /// </summary>
    public Dictionary<string, FunctionDefinition> Functions { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the procedure definitions in this package.
    /// </summary>
    public Dictionary<string, ProcedureDefinition> Procedures { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers a function definition.
    /// </summary>
    /// <param name="function">The function definition to register.</param>
    public void RegisterFunction(FunctionDefinition function)
    {
        function.PackageName = Name;
        Functions[function.Name] = function;
        if (!FunctionNames.Contains(function.Name, StringComparer.OrdinalIgnoreCase))
        {
            FunctionNames.Add(function.Name);
        }
    }

    /// <summary>
    /// Registers a procedure definition.
    /// </summary>
    /// <param name="procedure">The procedure definition to register.</param>
    public void RegisterProcedure(ProcedureDefinition procedure)
    {
        procedure.PackageName = Name;
        Procedures[procedure.Name] = procedure;
    }

    /// <summary>
    /// Gets a function by name.
    /// </summary>
    /// <param name="name">The function name.</param>
    /// <returns>The function definition or null if not found.</returns>
    public FunctionDefinition? GetFunction(string name)
    {
        return Functions.TryGetValue(name, out var func) ? func : null;
    }

    /// <summary>
    /// Gets a procedure by name.
    /// </summary>
    /// <param name="name">The procedure name.</param>
    /// <returns>The procedure definition or null if not found.</returns>
    public ProcedureDefinition? GetProcedure(string name)
    {
        return Procedures.TryGetValue(name, out var proc) ? proc : null;
    }

    /// <summary>
    /// Returns a string representation of the package.
    /// </summary>
    public override string ToString() => $"Package '{Name}': {Constants.Count} constants, {Functions.Count} functions, {Procedures.Count} procedures, {Types.Count} types";
}
