using Interpreter.Interfaces;
using Interpreter.Repositories;
using Parser.Models;

namespace Interpreter.Services;

/// <summary>
/// Predefined procedures of the standard library (<c>std.env</c>, <c>std.textio</c>)
/// and the resolution of a procedure-call statement that no user/package
/// registry knows. Mirrors <see cref="StandardFunctions"/> for procedures.
/// </summary>
public static class StandardProcedures
{
    /// <summary>Library prefixes a predefined procedure may be called through.</summary>
    private static readonly string[] Prefixes = ["std.env.", "std.textio."];

    /// <summary>
    /// Resolves a procedure call that <see cref="ProcedureCallStatement.IsProcedureCall"/>
    /// did not recognise: a predefined procedure becomes its task; any other name is
    /// an error, as in GHDL (<c>no declaration for "foo"</c>).
    /// </summary>
    /// <param name="call">The call statement (already consumed).</param>
    /// <param name="scope">Objects visible at the call.</param>
    /// <exception cref="ParserException">The name is undeclared, not a procedure, or not supported.</exception>
    public static ISControl Resolve(SimpleStatement call, HashSet<Variable> scope)
    {
        string name = Predefined(call.CallName);
        if (name is "stop" or "finish") return new EndSimulationStatement(name);
        if (TextioCall.Handles(name)) return TextioCall.Create(name, call, scope);
        if (TextioCall.IsUnsupported(name))
            throw new ParserException($"'{name}' (std.textio) is not supported yet", call.First);
        if (scope.Any(v => v.Name.Equals(call.CallName, StringComparison.OrdinalIgnoreCase)) || PackageRegistry.IsKnownName(call.CallName))
            throw new ParserException($"\"{call.CallName}\" is not a procedure", call.First);
        throw new ParserException($"no declaration for \"{call.CallName}\"", call.First);
    }

    /// <summary>Lower-cased name without a <c>std.env.</c> / <c>std.textio.</c> prefix.</summary>
    private static string Predefined(string name)
    {
        string low = name.ToLowerInvariant();
        foreach (var prefix in Prefixes)
            if (low.StartsWith(prefix, StringComparison.Ordinal)) return low[prefix.Length..];
        return low;
    }
}
