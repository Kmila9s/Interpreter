using Interpreter.Models;

namespace Interpreter.Services;

/// <summary>
/// Process-wide catalogue of function and procedure declarations the parser
/// has seen. Populated by <see cref="VariableSyntetizer"/>'s skip-pass when
/// it consumes a <c>function ... end;</c> or <c>procedure ... end;</c> block,
/// and consulted by:
///   • <see cref="VariableSyntetizer.ParseInitializationOperand"/> /
///     assignment parsing — to recognise calls cleanly instead of falling
///     into the "Unexpected end of input" path.
///   • The synthesis layer (in a later pass) — to emit black-box cells for
///     calls until the bodies themselves are synthesised.
/// </summary>
/// <remarks>
/// Singleton-style by convention to mirror <c>EntityRegistry</c> and
/// <c>PackageRegistry</c>. Bodies are never stored — only signatures.
/// </remarks>
public sealed class FunctionRegistry
{
    private static readonly FunctionRegistry _instance = new();
    /// <summary>Process-wide singleton.</summary>
    public static FunctionRegistry Instance => _instance;

    private readonly Dictionary<string, FunctionDefinition> _byName = new(StringComparer.OrdinalIgnoreCase);

    private FunctionRegistry() { }

    /// <summary>
    /// Records a signature. Last writer wins, since the parser reaches a
    /// declaration before any subsequent body redeclaration can occur in
    /// well-formed VHDL.
    /// </summary>
    public void Register(FunctionDefinition def)
    {
        if (string.IsNullOrEmpty(def.Name)) return;
        _byName[def.Name] = def;
    }

    /// <summary>Look up a previously-registered signature, or null.</summary>
    public FunctionDefinition? Get(string name)
        => _byName.TryGetValue(name, out var def) ? def : null;

    /// <summary>True iff <paramref name="name"/> is a known function or procedure.</summary>
    public bool IsKnown(string name) => _byName.ContainsKey(name);

    /// <summary>All registered signatures, in insertion order is not guaranteed.</summary>
    public IEnumerable<FunctionDefinition> All => _byName.Values;

    /// <summary>
    /// Drops every entry whose <see cref="FunctionDefinition.PackageName"/>
    /// is null or empty (i.e. user-declared). IEEE-seeded entries (which
    /// carry their package name) survive — those are process-scoped, not
    /// per-fixture. Called between fixtures by the test runner.
    /// </summary>
    public void Clear()
    {
        var keep = _byName
            .Where(kv => !string.IsNullOrEmpty(kv.Value.PackageName))
            .ToArray();
        _byName.Clear();
        foreach (var kv in keep) _byName[kv.Key] = kv.Value;
    }

    /// <summary>
    /// Drops every entry, including IEEE seeds. Used when the host is
    /// resetting the loader (typically tests).
    /// </summary>
    public void ClearAll() => _byName.Clear();
}
