using Interpreter.Models;
using Interpreter.Repositories;
using Interpreter.Services;
using Parser.Models;

namespace Interpreter.Synthesis;

/// <summary>
/// Lowers a VHDL <see cref="Process"/> into a netlist. The entry
/// <see cref="Synthesize"/> walks the process body and produces, per
/// driven signal, a "value net" — the gated combinational expression
/// that should drive the signal. Clocked processes wrap each value
/// net in a DFF; pure combinational processes drive the named
/// signal directly via a BUF.
/// </summary>
/// <remarks>
/// The walker handles three task kinds in process bodies:
/// <list type="bullet">
///   <item><see cref="Operation"/> — a plain <c>x &lt;= y</c> assignment.</item>
///   <item><see cref="IfStructure"/> — <c>if/elsif/else</c> branches lower
///         to a chain of MUX cells, one per branch, with the else (or
///         "previous value" feedback) terminating the chain.</item>
///   <item><see cref="CaseStructure"/> — each <c>when</c> clause becomes a
///         disjunction of EQ comparators against the selector, then
///         feeds a MUX in the same chain pattern as if/elsif.</item>
/// </list>
/// Anything we can't decompose (loops, nested processes, statements
/// we don't recognise) sends the whole process to the
/// <see cref="CellKind.BLACKBOX_PROCESS"/> fallback so the diagram
/// stays honest about what wasn't synthesised.
/// </remarks>
internal static class ProcessSynthesizer
{
    public static void Synthesize(Process process, HashSet<Variable> scope, SynthesisContext ctx)
    {
        var (clockSig, clockedHigh, innerTasks) = DetectClock(process);
        // Use pattern-matching null checks throughout. Variable defines
        // overloaded `==` / `!=` operators that compare by Name and
        // mis-handle two-sided-null comparisons (returns true for
        // `someVar != null` when `someVar` is itself null), so any
        // `Variable != null` check has to be `is not null`.
        bool isClocked = clockSig is not null;
        var bodyTasks = isClocked ? innerTasks : process.Tasks;
        if (bodyTasks is null)
        {
            EmitBlackbox(process, ctx);
            return;
        }

        // Bail to blackbox if any task in the body is a kind we don't
        // walk yet. Done up front so partial synthesis can't leave
        // the diagram in a half-decomposed state.
        if (!CanWalk(bodyTasks))
        {
            EmitBlackbox(process, ctx);
            return;
        }

        // Seed the running map with the named output nets for every
        // signal the body assigns. For clocked, this acts as the Q
        // feedback when a branch doesn't drive the signal (the "hold"
        // semantics). For combinational, it's the placeholder before
        // the first assignment lands.
        var running = new Dictionary<string, Net>(StringComparer.OrdinalIgnoreCase);
        var allLhs = CollectAssignedLhs(bodyTasks);
        foreach (var lhsName in allLhs)
        {
            var lhsVar = scope.FirstOrDefault(v =>
                v is not null && v.Name is not null
                && v.Name.Equals(lhsName, StringComparison.OrdinalIgnoreCase));
            if (lhsVar is null) continue;
            running[lhsName] = NetForLhs(lhsVar, ctx);
        }

        string sensList = process.SensitiveList != null
            ? string.Join(",", process.SensitiveList
                .Where(v => v != null && !string.IsNullOrEmpty(v.Name))
                .Select(v => v.Name))
            : "";
        string sourceLineKey = isClocked
            ? $"process: {(clockedHigh ? "rising_edge" : "falling_edge")}({clockSig!.Name})"
            : $"process({sensList})";

        WalkTasks(bodyTasks, scope, ctx, running, sourceLineKey);

        // Drive each output. For clocked signals: D <- running, Q is
        // the named net; the DFF closes the feedback loop the walker
        // expects when "hold previous value" semantics fire. For
        // combinational: the final running net is the value, BUF'd
        // into the named net so the wire shows in the diagram.
        Net? clkNet = clockSig is not null
            ? ctx.GetOrCreateNet(clockSig.Name, NetKind.Wire, 1,
                clockSig.IsPort ? NetOrigin.PortIn : NetOrigin.Signal)
            : null;

        foreach (var (lhsName, valueNet) in running)
        {
            var lhsVar = scope.FirstOrDefault(v => v.Name.Equals(lhsName, StringComparison.OrdinalIgnoreCase));
            if (lhsVar == null) continue;
            var qNet = NetForLhs(lhsVar, ctx);

            // No-op assignment? Walker may have left running[lhs] ==
            // qNet (e.g. a process that "drives" a signal but every
            // path falls through to previous value). Skip to avoid
            // self-driving cells.
            if (ReferenceEquals(valueNet, qNet)) continue;

            if (isClocked)
            {
                ctx.AddCell(
                    kind: CellKind.DFF,
                    inputs: new[] { new Pin("D", valueNet.Id), new Pin("CLK", clkNet!.Id) },
                    outputs: new[] { new Pin("Q", qNet.Id) },
                    sourceLine: sourceLineKey,
                    attrs: new Dictionary<string, string> { ["Edge"] = clockedHigh ? "rising" : "falling" });
            }
            else
            {
                ctx.AddCell(
                    kind: CellKind.BUF,
                    inputs: new[] { new Pin("A", valueNet.Id) },
                    outputs: new[] { new Pin("Y", qNet.Id) },
                    sourceLine: sourceLineKey);
            }
        }
    }

    // ─── Body walker ─────────────────────────────────────────────────

    /// <summary>
    /// Recurses over the process body, mutating <paramref name="running"/>
    /// in place. After the call, <c>running[lhs]</c> is the net
    /// representing the value the LHS should be driven with given the
    /// control flow seen so far.
    /// </summary>
    private static void WalkTasks(IList<object> tasks, HashSet<Variable> scope,
        SynthesisContext ctx, Dictionary<string, Net> running, string sourceLine)
    {
        foreach (var task in tasks)
        {
            switch (task)
            {
                case Operation op:
                    HandleAssignment(op, scope, ctx, running, sourceLine);
                    break;
                case IfStructure ifs:
                    HandleIf(ifs, scope, ctx, running, sourceLine);
                    break;
                case CaseStructure cs:
                    HandleCase(cs, scope, ctx, running, sourceLine);
                    break;
                // Anything else slips silently — CanWalk already
                // gated entry, so reaching this branch means a kind
                // we accept but don't drive.
            }
        }
    }

    private static void HandleAssignment(Operation op, HashSet<Variable> scope,
        SynthesisContext ctx, Dictionary<string, Net> running, string sourceLine)
    {
        if (op.Result is null || string.IsNullOrEmpty(op.Result.Name)) return;
        // The synthesizer creates fake "ConditionResult" Operations for
        // bare conditions; ignore those — they're never real
        // assignments to a named signal.
        if (op.Result.Name.StartsWith("Condition", StringComparison.OrdinalIgnoreCase)) return;

        int width = op.Result.Type == Constants.DATATYPES.STD_LOGIC_VECTOR
            ? Math.Max(1, op.Result.Size) : 1;
        var valNet = ctx.NewInternal(width: width, sourceLine: sourceLine);
        ConcurrentAssignSynthesizer.SynthesizeInto(op, valNet, ctx);
        running[op.Result.Name] = valNet;
    }

    private static void HandleIf(IfStructure ifs, HashSet<Variable> scope,
        SynthesisContext ctx, Dictionary<string, Net> running, string sourceLine)
    {
        var branchResults = new List<(Net Cond, Dictionary<string, Net> Outputs)>();
        Dictionary<string, Net>? elseOutputs = null;

        // Walk each branch in an isolated clone so writes don't bleed
        // between sibling branches. Conditional branches contribute a
        // condition net; the else branch (if any) sets the chain's
        // terminal value.
        foreach (var br in ifs.Branches)
        {
            var clone = new Dictionary<string, Net>(running, StringComparer.OrdinalIgnoreCase);
            WalkTasks(br.Tasks, scope, ctx, clone, sourceLine);
            if (br.IsElse)
            {
                elseOutputs = clone;
            }
            else
            {
                var condNet = SynthesizeCondition(br.Condition!, scope, ctx, sourceLine);
                branchResults.Add((condNet, clone));
            }
        }

        // Touched LHSs = anything modified in any branch.
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, outputs) in branchResults)
            foreach (var (k, v) in outputs)
                if (!running.TryGetValue(k, out var prev) || !ReferenceEquals(prev, v))
                    touched.Add(k);
        if (elseOutputs != null)
            foreach (var (k, v) in elseOutputs)
                if (!running.TryGetValue(k, out var prev) || !ReferenceEquals(prev, v))
                    touched.Add(k);

        // For each touched LHS, build a MUX chain. First branch (top
        // priority in VHDL) ends up at the outermost MUX; the else
        // (or fall-through) sits at the bottom.
        foreach (var lhs in touched)
        {
            Net defaultNet;
            if (elseOutputs != null && elseOutputs.TryGetValue(lhs, out var elseVal))
                defaultNet = elseVal;
            else if (running.TryGetValue(lhs, out var prev))
                defaultNet = prev;
            else
                continue; // can't resolve a default — skip this LHS

            var width = defaultNet.Width;
            var chain = defaultNet;
            for (int i = branchResults.Count - 1; i >= 0; i--)
            {
                var (cond, outputs) = branchResults[i];
                var brValue = outputs.TryGetValue(lhs, out var v)
                    ? v
                    : (running.TryGetValue(lhs, out var p) ? p : defaultNet);
                if (ReferenceEquals(brValue, chain)) continue; // branch didn't change LHS

                var muxOut = ctx.NewInternal(width: width, sourceLine: sourceLine);
                ctx.AddCell(
                    kind: CellKind.MUX,
                    inputs: new[]
                    {
                        new Pin("S", cond.Id),
                        new Pin("D1", brValue.Id),
                        new Pin("D0", chain.Id),
                    },
                    outputs: new[] { new Pin("Y", muxOut.Id) },
                    sourceLine: sourceLine);
                chain = muxOut;
            }
            running[lhs] = chain;
        }
    }

    private static void HandleCase(CaseStructure cs, HashSet<Variable> scope,
        SynthesisContext ctx, Dictionary<string, Net> running, string sourceLine)
    {
        if (cs.Selector == null) return;

        int selWidth = cs.Selector.Type == Constants.DATATYPES.STD_LOGIC_VECTOR
            ? Math.Max(1, cs.Selector.Size) : 1;
        var selNet = ctx.GetOrCreateNet(cs.Selector.Name,
            selWidth > 1 ? NetKind.Bus : NetKind.Wire, selWidth,
            cs.Selector.IsPort ? NetOrigin.PortIn : NetOrigin.Signal);

        // Same shape as HandleIf, but each branch's "condition" is an
        // EQ comparator (or chain of EQ + OR for multi-choice
        // branches).
        var branchResults = new List<(Net Cond, Dictionary<string, Net> Outputs)>();
        Dictionary<string, Net>? elseOutputs = null;

        foreach (var br in cs.Branches)
        {
            var clone = new Dictionary<string, Net>(running, StringComparer.OrdinalIgnoreCase);
            WalkTasks(br.Tasks, scope, ctx, clone, sourceLine);

            if (br.IsOthers)
            {
                elseOutputs = clone;
                continue;
            }

            // Build OR-of-EQ for the choices.
            Net? matchNet = null;
            foreach (var raw in br.Choices)
            {
                var literalNet = ctx.Constant(raw, selWidth, sourceLine);
                var eqOut = ctx.NewInternal(width: 1, sourceLine: sourceLine);
                ctx.AddCell(
                    kind: CellKind.EQ,
                    inputs: new[] { new Pin("A", selNet.Id), new Pin("B", literalNet.Id) },
                    outputs: new[] { new Pin("Y", eqOut.Id) },
                    sourceLine: sourceLine,
                    attrs: new Dictionary<string, string> { ["Choice"] = raw });

                if (matchNet == null)
                {
                    matchNet = eqOut;
                }
                else
                {
                    var orOut = ctx.NewInternal(width: 1, sourceLine: sourceLine);
                    ctx.AddCell(
                        kind: CellKind.OR,
                        inputs: new[] { new Pin("A", matchNet.Id), new Pin("B", eqOut.Id) },
                        outputs: new[] { new Pin("Y", orOut.Id) },
                        sourceLine: sourceLine);
                    matchNet = orOut;
                }
            }
            if (matchNet == null) continue;
            branchResults.Add((matchNet, clone));
        }

        // Reuse the if-style merger: build per-LHS MUX chain with the
        // others-branch (or running) as the terminal value.
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, outputs) in branchResults)
            foreach (var (k, v) in outputs)
                if (!running.TryGetValue(k, out var prev) || !ReferenceEquals(prev, v))
                    touched.Add(k);
        if (elseOutputs != null)
            foreach (var (k, v) in elseOutputs)
                if (!running.TryGetValue(k, out var prev) || !ReferenceEquals(prev, v))
                    touched.Add(k);

        foreach (var lhs in touched)
        {
            Net defaultNet;
            if (elseOutputs != null && elseOutputs.TryGetValue(lhs, out var elseVal))
                defaultNet = elseVal;
            else if (running.TryGetValue(lhs, out var prev))
                defaultNet = prev;
            else
                continue;

            var width = defaultNet.Width;
            var chain = defaultNet;
            for (int i = branchResults.Count - 1; i >= 0; i--)
            {
                var (cond, outputs) = branchResults[i];
                var brValue = outputs.TryGetValue(lhs, out var v)
                    ? v
                    : (running.TryGetValue(lhs, out var p) ? p : defaultNet);
                if (ReferenceEquals(brValue, chain)) continue;

                var muxOut = ctx.NewInternal(width: width, sourceLine: sourceLine);
                ctx.AddCell(
                    kind: CellKind.MUX,
                    inputs: new[]
                    {
                        new Pin("S", cond.Id),
                        new Pin("D1", brValue.Id),
                        new Pin("D0", chain.Id),
                    },
                    outputs: new[] { new Pin("Y", muxOut.Id) },
                    sourceLine: sourceLine);
                chain = muxOut;
            }
            running[lhs] = chain;
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Synthesise an Operation that represents a boolean condition
    /// into a single 1-bit net. The Operation already carries the
    /// postfix item list — we just wire it into a fresh net via the
    /// existing concurrent-assign synthesiser.
    /// </summary>
    private static Net SynthesizeCondition(Operation cond, HashSet<Variable> scope,
        SynthesisContext ctx, string sourceLine)
    {
        var outNet = ctx.NewInternal(width: 1, sourceLine: sourceLine);
        ConcurrentAssignSynthesizer.SynthesizeInto(cond, outNet, ctx);
        return outNet;
    }

    /// <summary>
    /// Returns true when every task in the body (recursively) is one
    /// of the kinds the walker handles. Anything else triggers the
    /// blackbox fallback for the whole process — partial synthesis
    /// would leave the diagram dishonest.
    /// </summary>
    private static bool CanWalk(IList<object>? tasks)
    {
        if (tasks == null) return false;
        foreach (var t in tasks)
        {
            switch (t)
            {
                case null:
                    return false;
                case Operation:
                    break;
                case IfStructure ifs:
                    if (ifs.Branches == null) return false;
                    foreach (var br in ifs.Branches)
                        if (br == null || !CanWalk(br.Tasks)) return false;
                    break;
                case CaseStructure cs:
                    if (cs.Branches == null) return false;
                    foreach (var br in cs.Branches)
                        if (br == null || !CanWalk(br.Tasks)) return false;
                    break;
                default:
                    return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Collects every signal name assigned anywhere in the body —
    /// used to seed the running map with the named output nets so
    /// the MUX-chain merger has a sensible "previous value" to
    /// fall through to when a branch doesn't drive the LHS.
    /// </summary>
    private static HashSet<string> CollectAssignedLhs(IList<object> tasks)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Visit(tasks);
        return set;

        void Visit(IList<object> ts)
        {
            foreach (var t in ts)
            {
                switch (t)
                {
                    case Operation op when op.Result?.Name is { Length: > 0 } name
                                         && !name.StartsWith("Condition", StringComparison.OrdinalIgnoreCase):
                        set.Add(name);
                        break;
                    case IfStructure ifs:
                        foreach (var br in ifs.Branches) Visit(br.Tasks);
                        break;
                    case CaseStructure cs:
                        foreach (var br in cs.Branches) Visit(br.Tasks);
                        break;
                }
            }
        }
    }

    /// <summary>
    /// Detects the canonical clocked-process shape: the body is a
    /// single IfStructure whose first branch's condition is
    /// <c>rising_edge(clk)</c> / <c>falling_edge(clk)</c> or the
    /// older <c>clk'event and clk = '1'</c> idiom. Returns the
    /// inner branch tasks so the body walker doesn't re-traverse
    /// the rising_edge wrapper.
    /// </summary>
    private static (Variable? clockSig, bool rising, IList<object>? innerTasks) DetectClock(Process process)
    {
        if (process.Tasks.Count == 1 && process.Tasks[0] is IfStructure ifs && ifs.Branches.Count >= 1)
        {
            var br = ifs.Branches[0];
            var (sig, rising) = DetectClockEdge(br.Condition);
            // Pattern-matching null check — see Synthesize() comment.
            // The Variable type's overloaded operators don't behave
            // correctly for two-sided null and would falsely report a
            // null sig as "non-null" here.
            if (sig is not null) return (sig, rising, br.Tasks);
        }
        Variable? nullSig = null;
        IList<object>? nullTasks = null;
        return (nullSig, false, nullTasks);
    }

    private static (Variable? signal, bool rising) DetectClockEdge(Operation? cond)
    {
        if (cond is null) return (null, false);
        foreach (var item in cond.Items)
        {
            if (item is DeferredFunction df &&
                (df.FunctionName.Equals("rising_edge", StringComparison.OrdinalIgnoreCase)
                 || df.FunctionName.Equals("falling_edge", StringComparison.OrdinalIgnoreCase)))
            {
                if (df.Arguments.Length > 0 && df.Arguments[0] is Variable v)
                    return (v, df.FunctionName.Equals("rising_edge", StringComparison.OrdinalIgnoreCase));
                if (df.Arguments.Length > 0 && df.Arguments[0] is string sname)
                {
                    var found = df.Variables.FirstOrDefault(x => x.Name.Equals(sname, StringComparison.OrdinalIgnoreCase));
                    if (found != null)
                        return (found, df.FunctionName.Equals("rising_edge", StringComparison.OrdinalIgnoreCase));
                }
            }
            if (item is Variable attr && attr.Name.StartsWith("Attribute-", StringComparison.Ordinal)
                && attr.Name.Contains("'event", StringComparison.OrdinalIgnoreCase))
            {
                int dash = attr.Name.IndexOf('-');
                int apo = attr.Name.IndexOf('\'');
                if (dash > 0 && apo > dash)
                {
                    string sig = attr.Name[(dash + 1)..apo];
                    var found = cond.Variables.FirstOrDefault(v => v.Name.Equals(sig, StringComparison.OrdinalIgnoreCase));
                    if (found is not null) return (found, true);
                }
            }
        }
        return (null, false);
    }

    private static Net NetForLhs(Variable v, SynthesisContext ctx)
    {
        int w = v.Type == Constants.DATATYPES.STD_LOGIC_VECTOR ? Math.Max(1, v.Size) : 1;
        var origin = v.IsPort
            ? v.PortType switch
            {
                Constants.PortType.IN => NetOrigin.PortIn,
                Constants.PortType.OUT => NetOrigin.PortOut,
                Constants.PortType.INOUT => NetOrigin.PortInOut,
                _ => NetOrigin.PortIn,
            }
            : NetOrigin.Signal;
        return ctx.GetOrCreateNet(v.Name, w > 1 ? NetKind.Bus : NetKind.Wire, w, origin);
    }

    /// <summary>
    /// Emits a single black-box cell representing the entire process
    /// when the body uses constructs the walker can't decompose
    /// (loops, function calls, etc.). Approximates inputs from the
    /// sensitivity list and outputs from any LHS the walker found.
    /// </summary>
    private static void EmitBlackbox(Process process, SynthesisContext ctx)
    {
        var inputs = new List<Pin>();
        var outputs = new List<Pin>();
        var seenIn = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenOut = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var s in process.SensitiveList)
        {
            int w = s.Type == Constants.DATATYPES.STD_LOGIC_VECTOR ? Math.Max(1, s.Size) : 1;
            var net = ctx.GetOrCreateNet(s.Name, w > 1 ? NetKind.Bus : NetKind.Wire, w,
                s.IsPort ? NetOrigin.PortIn : NetOrigin.Signal);
            if (seenIn.Add(s.Name))
                inputs.Add(new Pin(s.Name, net.Id));
        }
        CollectDrivenSignals(process.Tasks, ctx, outputs, seenOut);

        ctx.AddCell(
            kind: CellKind.BLACKBOX_PROCESS,
            inputs: inputs,
            outputs: outputs,
            attrs: new Dictionary<string, string>
            {
                ["Reason"] = "Process body uses constructs not yet decomposed (loops / function calls / etc.).",
            });

        ctx.AddDiagnostic("BlackboxProcess",
            "Process body kept as opaque cell — unsupported constructs encountered.");
    }

    private static void CollectDrivenSignals(IEnumerable<object> tasks, SynthesisContext ctx,
        List<Pin> outputs, HashSet<string> seen)
    {
        foreach (var t in tasks)
        {
            switch (t)
            {
                case Operation op when op.Result?.Name is { Length: > 0 } name
                                     && !name.StartsWith("Condition", StringComparison.Ordinal):
                    if (seen.Add(name))
                    {
                        int w = op.Result.Type == Constants.DATATYPES.STD_LOGIC_VECTOR
                            ? Math.Max(1, op.Result.Size) : 1;
                        var net = ctx.GetOrCreateNet(name, w > 1 ? NetKind.Bus : NetKind.Wire, w,
                            op.Result.IsPort ? NetOrigin.PortOut : NetOrigin.Signal);
                        outputs.Add(new Pin(name, net.Id));
                    }
                    break;
                case IfStructure ifs:
                    foreach (var br in ifs.Branches)
                        CollectDrivenSignals(br.Tasks, ctx, outputs, seen);
                    break;
                case CaseStructure cs:
                    foreach (var br in cs.Branches)
                        CollectDrivenSignals(br.Tasks, ctx, outputs, seen);
                    break;
            }
        }
    }
}
