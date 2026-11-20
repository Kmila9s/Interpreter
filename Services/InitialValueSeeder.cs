using Interpreter.Models;
using Parser.Models;
using static Parser.Models.Constants;

namespace Interpreter.Services;

/// <summary>
/// Applies an <see cref="InitialValuePolicy"/> before a run: the single place that decides how
/// objects declared without <c>:=</c> start. <see cref="InitialValuePolicy.Lrm"/> leaves every
/// object at the value its declaration gave it (the explicit one or <c>T'left</c>);
/// <see cref="InitialValuePolicy.Didactic"/> seeds them, and every undriven port, at 0.
/// </summary>
public static class InitialValueSeeder
{
    /// <summary>
    /// Seeds <paramref name="objects"/> under <paramref name="policy"/>; objects in
    /// <paramref name="driven"/> (clocks, stimulated ports) are left to their driver.
    /// </summary>
    public static void Apply(InitialValuePolicy policy, IEnumerable<Variable> objects, IReadOnlySet<Variable> driven)
    {
        if (policy == InitialValuePolicy.Lrm) return;
        foreach (var v in objects)
        {
            if (driven.Contains(v) || v.IsConstant || (v.HasInitializer && !v.IsPort)) continue;
            if (DidacticValue(v) is { } value) v.Value = value;
        }
    }

    /// <summary>
    /// Before a run: applies <paramref name="config"/>'s policy to its entity (clocks and stimulated
    /// ports are left to their driver), then sets the port initial values.
    /// </summary>
    public static void Prepare(SimulationConfig config)
    {
        var driven = new HashSet<Variable>(config.Clocks.Select(c => c.Signal).Concat(config.Stimuli.Select(s => s.Signal)));
        Apply(config.InitialValues, ObjectsOf(config.Entity), driven);
        foreach (var initial in config.PortInitials) initial.Apply();
    }

    /// <summary>The objects of <paramref name="entity"/>: ports, architecture signals and process variables.</summary>
    public static IEnumerable<Variable> ObjectsOf(Entity entity) =>
        entity.Ports.Concat(entity.Architecture?.Signals ?? [])
            .Concat(entity.Architecture?.Tasks.OfType<Process>().SelectMany(p => p.Variables) ?? []);

    /// <summary>The didactic start value of <paramref name="v"/>, or null to keep its declared one.</summary>
    private static object? DidacticValue(Variable v)
    {
        if (v.Name.Equals("enable", StringComparison.OrdinalIgnoreCase) || v.Name.Equals("en", StringComparison.OrdinalIgnoreCase))
            return "1";
        if (v.EnumLiterals is { Count: > 0 } || v.Type is DATATYPES.STRING or DATATYPES.CHARACTER)
            return null;
        if (v.Layout is not null || v.ElementShape?.Layout is not null)
            return v.IsPort ? new string('0', v.Size) : null;
        return v.Type switch
        {
            DATATYPES.INTEGER or DATATYPES.NATURAL => v.ScalarRange is { } r ? (int)Math.Clamp(0, r.Low, r.High) : 0,
            DATATYPES.BOOLEAN => false,
            _ => v.Size > 1 ? new string('0', v.Size) : "0",
        };
    }
}
