using System;
using System.Collections.Generic;
using System.Linq;

namespace Interpreter.Services;

/// <summary>
/// A per-architecture lookup of user-defined ENUMERATION types → their declared
/// literals, used by the type-precise cross-enum case check (precise-C10).
/// </summary>
/// <remarks>
/// <para>
/// A singleton (mirroring <see cref="PackageRegistry"/>'s access pattern) because
/// <c>CaseStructure</c> has no path to the architecture's <c>CustomTypes</c>
/// registry. It is <see cref="LoadFrom"/>-reset at the start of each architecture's
/// behaviour synthesis, so it only ever holds the enum types of the architecture
/// currently being synthesised — this avoids cross-architecture leakage when two
/// architectures declare the same enum type name (e.g. 34_TrafficLight).
/// </para>
/// <para>
/// It intentionally reads the declared literal LIST of each enum TYPE (not the
/// enum-literal <c>Variable</c>s), so a case-insensitive collision between a literal
/// and a same-named signal/port never hides a literal — the collision that defeated
/// the earlier literal-tagging attempt.
/// </para>
/// </remarks>
public sealed class EnumTypeRegistry
{
    private static readonly EnumTypeRegistry _instance = new();
    public static EnumTypeRegistry Instance => _instance;

    // type name → its literals. Both type names and literals compare case-insensitively.
    private readonly Dictionary<string, string[]> _types = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Removes all registered enum types.</summary>
    public void Clear() => _types.Clear();

    /// <summary>Registers (or replaces) an enum type and its literals.</summary>
    public void RegisterEnumType(string typeName, IReadOnlyList<string> literals)
    {
        if (string.IsNullOrEmpty(typeName) || literals is null) return;
        _types[typeName] = literals.Where(l => !string.IsNullOrEmpty(l)).ToArray();
    }

    /// <summary>
    /// Clears and repopulates from an architecture's enumeration types. Called once
    /// per architecture, before its behaviour (and thus its cases) is synthesised.
    /// </summary>
    public void LoadFrom(TypeRegistry? customTypes)
    {
        Clear();
        if (customTypes is null) return;
        foreach (var t in customTypes.GetEnumerationTypes())
            if (!string.IsNullOrEmpty(t.Name))
                RegisterEnumType(t.Name, t.EnumValues);
    }

    /// <summary>The literals of <paramref name="typeName"/>, or null if unknown.</summary>
    public IReadOnlyList<string>? Literals(string? typeName) =>
        typeName is not null && _types.TryGetValue(typeName, out var lits) ? lits : null;

    /// <summary>True if <paramref name="name"/> is a literal of ANY registered enum type.</summary>
    public bool IsAnyEnumLiteral(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        foreach (var lits in _types.Values)
            foreach (var l in lits)
                if (l.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
