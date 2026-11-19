using Interpreter.Repositories;
using Interpreter.Services;
using Parser.Models;

namespace Interpreter.Synthesis;

/// <summary>
/// Public entry point: turn a parsed <see cref="Entity"/> into a
/// <see cref="Netlist"/>. Pure function — no simulation state, no
/// mutation. Safe to call after parse, before run, or while a
/// simulation is paused.
/// </summary>
public static class Synthesizer
{
    /// <summary>Diagnostic for an aggregate assignment target <c>(a, b) &lt;= …</c> (simulated, not synthesised yet).</summary>
    internal const string AggregateTargetNote = "Aggregate assignment target (a, b) <= … not yet synthesised.";

    /// <summary>
    /// Synthesise the entity's first architecture into a gate-level
    /// netlist. Each architecture task (concurrent assign, conditional,
    /// selected, process) is delegated to its specialised lowerer; the
    /// shared <see cref="SynthesisContext"/> accumulates nets and cells
    /// in declaration order so the renderer can group cells by source
    /// line in Naive view.
    /// </summary>
    public static Netlist Synthesize(Entity entity)
    {
        if (entity == null) throw new ArgumentNullException(nameof(entity));
        var ctx = new SynthesisContext(entity.Name);

        // Seed the netlist with every port and signal. Doing this up front
        // means the diagram has a complete I/O strip even when the
        // architecture is empty (parse-only view).
        foreach (var p in entity.Ports) ctx.AddPort(p);
        if (entity.Architecture != null)
        {
            foreach (var s in entity.Architecture.Signals) ctx.AddSignal(s);

            // Component instantiations live in EntityRegistry (populated by
            // Architecture.SyntetizeBehaviour), not in Architecture.Tasks.
            // Emit one BLACKBOX_COMPONENT cell per instance so hierarchical
            // designs (e.g. capstone-calc's calc_top) surface their
            // sub-modules in the schematic instead of rendering as a
            // handful of orphan primitives.
            ComponentSynthesizer.SynthesizeAll(entity.Name, ctx);

            HashSet<Variable> scope = entity.Architecture.GetAllSignals();
            foreach (var task in entity.Architecture.Tasks)
            {
                switch (task)
                {
                    case Operation { AggregateTarget: not null }:
                        ctx.AddDiagnostic("Unsupported", AggregateTargetNote);
                        break;
                    case Operation op:
                        ConcurrentAssignSynthesizer.Synthesize(op, ctx);
                        break;
                    case ConditionalAssignment ca:
                        ConditionalAssignSynthesizer.Synthesize(ca, scope, ctx);
                        break;
                    case SelectedAssignment sa:
                        SelectedAssignSynthesizer.Synthesize(sa, scope, ctx);
                        break;
                    case Process pr:
                        ProcessSynthesizer.Synthesize(pr, scope, ctx);
                        break;
                    case IfStructure:
                    case GenerateBlock:
                        // Top-level if/generate at the architecture level
                        // is rare and not yet decomposed. Surface a
                        // diagnostic; the diagram is otherwise still
                        // useful for the parts we did synthesise.
                        ctx.AddDiagnostic("Unsupported",
                            $"Architecture-level {task.GetType().Name} not yet synthesised.");
                        break;
                }
            }
        }

        return ctx.Build();
    }
}
