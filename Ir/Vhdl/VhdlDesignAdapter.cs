using Interpreter.Interfaces;
using Interpreter.Repositories;
using Interpreter.Services;
using Parser.Models;
using static Parser.Models.Constants;

namespace Interpreter.Ir.Vhdl;

/// <summary>
/// Lowers a parsed VHDL design unit (<see cref="Entity"/> with its architecture) to the
/// language-neutral <see cref="IrDesign"/>: one <see cref="IrSignal"/> per port, generic,
/// body object and process variable, and one <see cref="IrProcess"/> per concurrent unit
/// (generates unrolled), with its sensitivity. The parsed objects stay the run's value cells,
/// reachable through <see cref="IrDesign.Binding"/> (until v3.2.3).
/// </summary>
public static class VhdlDesignAdapter
{
    /// <summary>The IR of <paramref name="entity"/>, whose elaborated lines come from <paramref name="lineMap"/>.</summary>
    /// <exception cref="InvalidOperationException">The entity has no architecture.</exception>
    public static IrDesign From(Entity entity, ElaborationLineMap? lineMap = null)
    {
        var arch = entity.Architecture
            ?? throw new InvalidOperationException($"Entity '{entity.Name}' has no architecture.");
        var cells = new Dictionary<IrSignal, Variable>();
        IrSignal Lower(Variable v, IrObjectKind kind)
        {
            var signal = SignalOf(v, kind);
            cells[signal] = v;
            return signal;
        }

        var ports = entity.Ports.Where(p => p is not null).Select(p => Lower(p, IrObjectKind.Port)).ToList();
        var generics = entity.Generics.Where(g => g is not null).Select(g => Lower(g, IrObjectKind.Generic)).ToList();
        var signals = arch.Signals.Select(s => Lower(s, s.IsConstant ? IrObjectKind.Constant : IrObjectKind.Signal)).ToList();

        var binding = new IrBinding(cells, ports, ports.Concat(signals));
        // Sensitivity lists name objects of the architecture scope (compared by name, as VHDL does).
        var byName = new Dictionary<Variable, IrSignal>();
        foreach (var signal in ports.Concat(signals)) byName.TryAdd(cells[signal], signal);
        List<IrSignal> Named(IEnumerable<Variable> read) =>
            [.. read.Select(v => byName.GetValueOrDefault(v)).OfType<IrSignal>().Distinct()];

        var processes = GenerateBlock.Flatten(arch.Tasks)
            .Select(task => ProcessOf(task, binding.Objects, Named, Lower))
            .ToList();

        return new IrDesign
        {
            Name = entity.Name,
            Ports = ports,
            Generics = generics,
            Signals = signals,
            Processes = processes,
            LineMap = lineMap,
            Binding = binding,
        };
    }

    /// <summary>The concurrent unit <paramref name="task"/> (a process or a concurrent statement).</summary>
    private static IrProcess ProcessOf(object task, HashSet<Variable> objects,
        Func<IEnumerable<Variable>, List<IrSignal>> named, Func<Variable, IrObjectKind, IrSignal> lower)
    {
        switch (task)
        {
            case Process p:
                return new IrProcess
                {
                    Label = p.Label,
                    SourceLine = p.SourceLine,
                    Origin = p.IsEquivalent ? IrProcessOrigin.Equivalent : IrProcessOrigin.Written,
                    SensitivityKind = p.HasWait ? IrSensitivityKind.Wait
                        : p.SensitiveToAll ? IrSensitivityKind.All
                        : p.IsEquivalent ? IrSensitivityKind.Implicit
                        : IrSensitivityKind.Explicit,
                    Sensitivity = p.HasWait ? [] : named(p.SensitiveList),
                    Variables = [.. p.Variables.Select(v => lower(v, v.IsConstant ? IrObjectKind.Constant : IrObjectKind.Variable))],
                    Body = p,
                };
            case IConcurrentStatement statement:
                var reads = ImplicitSensitivity.OfStatement(statement, objects);
                return new IrProcess
                {
                    SourceLine = (statement as Operation)?.SourceLine ?? 0,
                    Origin = IrProcessOrigin.Equivalent,
                    SensitivityKind = reads is null ? IrSensitivityKind.EveryDelta : IrSensitivityKind.Implicit,
                    Sensitivity = reads is null ? [] : named(reads),
                    Body = task,
                };
            default:
                // A concurrent control structure that is not a statement with a read set runs every delta.
                return new IrProcess { Origin = IrProcessOrigin.Equivalent, SensitivityKind = IrSensitivityKind.EveryDelta, Body = task };
        }
    }

    /// <summary>The IR description of <paramref name="v"/> (its shape, never its value).</summary>
    private static IrSignal SignalOf(Variable v, IrObjectKind kind) => new()
    {
        Name = v.Name,
        Kind = kind,
        PortMode = kind == IrObjectKind.Port ? ModeOf(v.PortType) : null,
        Type = TypeOf(v),
        Width = Math.Max(1, v.Size),
        Range = v.HasDeclaredRange ? new IrRange(v.LeftBound!.Value, v.RightBound!.Value, v.IsDownto) : null,
        Signed = TypeMarks.SignednessOf(v.TypeMark) == Signedness.Signed,
        Layout = v.Layout,
        EnumLiterals = v.EnumLiterals,
    };

    private static IrPortMode ModeOf(PortType mode) => mode switch
    {
        PortType.OUT => IrPortMode.Out,
        PortType.INOUT => IrPortMode.InOut,
        PortType.BUFFER => IrPortMode.Buffer,
        _ => IrPortMode.In,
    };

    private static IrTypeKind TypeOf(Variable v)
    {
        if (v.EnumLiterals is not null || v.EnumTypeName is not null) return IrTypeKind.Enum;
        if (v.Layout is not null) return IrTypeKind.Record;
        if (v.ElementSize > 0 || v.ElementShape is not null) return IrTypeKind.Array;   // array of vectors/records (memories)
        return v.Type switch
        {
            DATATYPES.STD_LOGIC => IrTypeKind.Logic,
            DATATYPES.STD_LOGIC_VECTOR => IrTypeKind.LogicVector,
            DATATYPES.BIT => IrTypeKind.Bit,
            DATATYPES.BOOLEAN => IrTypeKind.Boolean,
            DATATYPES.INTEGER or DATATYPES.NATURAL or DATATYPES.RANGE => IrTypeKind.Integer,
            DATATYPES.CHARACTER => IrTypeKind.Character,
            DATATYPES.STRING => IrTypeKind.String,
            _ => IrTypeKind.Unknown,
        };
    }
}
