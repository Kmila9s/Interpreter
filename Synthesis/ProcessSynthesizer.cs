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

        // Pass 19 (2026-07-30): merge process-local variable declarations
        // into the scope handed to the walker. Without this, a shape like
        // `variable mem : mem_t; mem(i) := din;` couldn't lower to
        // RAM_WRITE because TryHandleDynamicIndexWrite's base lookup
        // searched only architecture-level signals. The merged scope is
        // used everywhere downstream (walker + end-of-Synthesize closure).
        // Local vars are also tracked separately so we can classify their
        // Q nets at closure time.
        var localVars = process.Variables ?? new HashSet<Variable>();
        HashSet<Variable> mergedScope = scope;
        if (localVars.Count > 0)
        {
            mergedScope = new HashSet<Variable>(scope);
            foreach (var lv in localVars) mergedScope.Add(lv);
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
            var lhsVar = mergedScope.FirstOrDefault(v =>
                v is not null && v.Name is not null
                && v.Name.Equals(lhsName, StringComparison.OrdinalIgnoreCase));
            if (lhsVar is null) continue;
            running[lhsName] = NetForLhs(lhsVar, ctx);
        }

        string sensList = process.SensitiveList != null
            ? string.Join(",", process.SensitiveList
                .Where(v => v is not null && !string.IsNullOrEmpty(v.Name))
                .Select(v => v.Name))
            : "";
        string sourceLineKey = isClocked
            ? $"process: {(clockedHigh ? "rising_edge" : "falling_edge")}({clockSig!.Name})"
            : $"process({sensList})";

        WalkTasks(bodyTasks, mergedScope, ctx, running, sourceLineKey);

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
            var lhsVar = mergedScope.FirstOrDefault(v => v.Name.Equals(lhsName, StringComparison.OrdinalIgnoreCase));
            if (lhsVar is null) continue;
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
    internal static void WalkTasks(IList<object> tasks, HashSet<Variable> scope,
        SynthesisContext ctx, Dictionary<string, Net> running, string sourceLine)
    {
        foreach (var task in tasks)
        {
            switch (task)
            {
                case Operation op:
                    if (!TryHandleDynamicIndexWrite(op, scope, ctx, running, sourceLine))
                    {
                        HandleAssignment(op, scope, ctx, running, sourceLine);
                    }
                    break;
                case IfStructure ifs:
                    HandleIf(ifs, scope, ctx, running, sourceLine);
                    break;
                case CaseStructure cs:
                    HandleCase(cs, scope, ctx, running, sourceLine);
                    break;
                case ProcedureCallStatement pcall:
                    // Tier 4.3 B2 (2026-07-28): resolve the procedure's bound
                    // body (formals textually substituted with actuals via
                    // ProcedureCallStatement.ResolveBoundBody — same cache the
                    // runtime uses) and walk it inline. Bound-body assignments
                    // to signal actuals thread directly into the caller's
                    // `running` map because the formal→actual rename already
                    // resolved to caller-scope Variable names. Runs against
                    // <paramref name="scope"/> (caller scope + locals cloned
                    // by ResolveBoundBody) so any callee-local names materialise
                    // as their own nets. Recursion / procedure-in-procedure
                    // works transparently: each nested call resolves + walks
                    // through this same branch.
                    try
                    {
                        var bound = pcall.ResolveBoundBody(null, out _, out var boundScope);
                        if (bound != null && bound.Count > 0)
                        {
                            WalkTasks(bound, boundScope, ctx, running, sourceLine);
                        }
                    }
                    catch { /* CanWalk already vetted; a resolve throw is
                             * treated as a no-op so partial synth doesn't
                             * abort the whole process. */ }
                    break;
                case ForLoopStructure fls:
                    // Tier 4.3 pass 10 (2026-07-29): synth-time unroll for
                    // bounded for-loops. On each iteration mutate the
                    // loop variable's Value to the current index so any
                    // DynamicIndex references inside the body pick up the
                    // per-iteration integer (see ConcurrentAssignSynthesizer's
                    // DynamicIndex case for the static-tap resolution).
                    // Writes to `running` accumulate across iterations —
                    // loop-carried dependencies are modelled correctly
                    // because subsequent iterations reference the previous
                    // iteration's driver net through the same map.
                    if (fls.Tasks != null)
                    {
                        var loopVar = scope.FirstOrDefault(v =>
                            v is not null && v.Name.Equals(fls.LoopVariableName, StringComparison.OrdinalIgnoreCase));
                        Variable? iterVar = loopVar;
                        HashSet<Variable> iterScope = scope;
                        // `is null` is required — Variable overloads ==/!= on Name
                        // which compares strings, not reference identity, so
                        // `iterVar == null` gives the wrong answer for ref-null
                        // (see project_constant_array_fix memory).
                        if (iterVar is null)
                        {
                            iterVar = new Variable(fls.LoopVariableName, fls.RangeStart.ToString(),
                                Constants.DATATYPES.INTEGER);
                            iterScope = new HashSet<Variable>(scope) { iterVar };
                        }

                        if (fls.IsAscending)
                        {
                            for (int i = fls.RangeStart; i <= fls.RangeEnd; i++)
                            {
                                iterVar.Value = i.ToString();
                                WalkTasks(fls.Tasks, iterScope, ctx, running, sourceLine);
                            }
                        }
                        else
                        {
                            for (int i = fls.RangeStart; i >= fls.RangeEnd; i--)
                            {
                                iterVar.Value = i.ToString();
                                WalkTasks(fls.Tasks, iterScope, ctx, running, sourceLine);
                            }
                        }
                    }
                    break;
                // Anything else slips silently — CanWalk already
                // gated entry, so reaching this branch means a kind
                // we accept but don't drive.
            }
        }
    }

    /// <summary>
    /// Tier 4.3 pass 17 (2026-07-29): detect a dynamic-index signal write
    /// like <c>mem(to_integer(unsigned(addr))) &lt;= din</c> before it
    /// falls into the regular <see cref="HandleAssignment"/> path. The
    /// Operation constructor collapses this shape's LHS to a
    /// <c>ConditionResult</c> placeholder (its regex only matches static
    /// integer indices), which the assignment walker then silently drops.
    /// Instead, emit a <see cref="CellKind.RAM_WRITE"/> cell so the RAM's
    /// write side is visible in the schematic and update the running dict
    /// to point at the post-write memory net.
    /// </summary>
    /// <returns>True if this Operation was a dynamic-index write and was
    /// handled here; false to fall through to the default assignment
    /// path.</returns>
    /// <summary>
    /// Extracts the base signal name from a dynamic-index write's raw
    /// text (e.g. "mem ( to_integer(unsigned(addr)) ) &lt;= din" →
    /// "mem"). Returns null when the shape doesn't match.
    /// </summary>
    private static string? TryExtractDynamicIndexBase(string? rawData)
    {
        if (string.IsNullOrEmpty(rawData)) return null;
        int arrow = FindAssignmentArrow(rawData, out _);
        if (arrow <= 0) return null;
        string lhs = rawData.Substring(0, arrow).Trim();
        int paren = lhs.IndexOf('(');
        if (paren <= 0 || lhs[^1] != ')') return null;
        string baseName = lhs.Substring(0, paren).Trim();
        string indexExpr = lhs.Substring(paren + 1, lhs.Length - paren - 2).Trim();
        if (string.IsNullOrEmpty(baseName) || string.IsNullOrEmpty(indexExpr)) return null;
        if (int.TryParse(indexExpr, out _)) return null; // static: legacy path
        return baseName;
    }

    /// <summary>
    /// Pass 19 (2026-07-30) — finds the assignment arrow (<c>&lt;=</c>
    /// for signals, <c>:=</c> for variables) in a raw Operation line
    /// and reports which one matched. Returns the byte offset of the
    /// arrow (0 or negative if none found) and sets <paramref name="isVariable"/>.
    /// Chooses the leftmost arrow so a RHS with `foo &lt;= bar` embedded
    /// in a variable assign like `mem(i) := (a &lt;= b)` still splits at
    /// the `:=` first.
    /// </summary>
    private static int FindAssignmentArrow(string rawData, out bool isVariable)
    {
        int sigArrow = rawData.IndexOf("<=", StringComparison.Ordinal);
        int varArrow = rawData.IndexOf(":=", StringComparison.Ordinal);
        if (varArrow >= 0 && (sigArrow < 0 || varArrow < sigArrow))
        {
            isVariable = true;
            return varArrow;
        }
        isVariable = false;
        return sigArrow;
    }

    private static bool TryHandleDynamicIndexWrite(Operation op, HashSet<Variable> scope,
        SynthesisContext ctx, Dictionary<string, Net> running, string sourceLine)
    {
        if (string.IsNullOrEmpty(op.RawData)) return false;
        int arrowIdx = FindAssignmentArrow(op.RawData, out bool isVariable);
        if (arrowIdx <= 0) return false;

        string lhsRaw = op.RawData.Substring(0, arrowIdx).Trim();
        string rhsRaw = op.RawData.Substring(arrowIdx + 2).Trim();
        if (string.IsNullOrEmpty(lhsRaw) || string.IsNullOrEmpty(rhsRaw)) return false;

        // LHS must be `<baseName> ( <indexExpr> )`. Static integer indices
        // (`mem(3) <= …`) fall through — the Operation constructor already
        // handles them via ResultBitIndex and HandleAssignment writes them
        // straight to the named net.
        int firstParen = lhsRaw.IndexOf('(');
        if (firstParen <= 0) return false;
        int lastParen = lhsRaw.LastIndexOf(')');
        if (lastParen != lhsRaw.Length - 1) return false;
        string baseName = lhsRaw.Substring(0, firstParen).Trim();
        string indexExpr = lhsRaw.Substring(firstParen + 1, lastParen - firstParen - 1).Trim();
        if (string.IsNullOrEmpty(baseName) || string.IsNullOrEmpty(indexExpr)) return false;
        if (int.TryParse(indexExpr, out _)) return false; // static index: legacy path handles

        var baseVar = scope.FirstOrDefault(v =>
            v is not null && v.Name is not null
            && v.Name.Equals(baseName, StringComparison.OrdinalIgnoreCase));
        if (baseVar is null) return false;
        int elemSize = baseVar.ElementSize;
        if (elemSize <= 0) return false; // not an array-of-vectors

        Net dataNet;
        try
        {
            var dataOp = new Operation($"{baseName} <= {rhsRaw}", scope);
            var dataOut = ctx.NewInternal(elemSize, sourceLine);
            ConcurrentAssignSynthesizer.SynthesizeInto(dataOp, dataOut, ctx);
            dataNet = dataOut;
        }
        catch { return false; }

        Net selNet;
        try
        {
            var selOp = new Operation($"{baseName}_idx_sel <= {indexExpr}", scope);
            var selOut = ctx.NewInternal(1, sourceLine);
            ConcurrentAssignSynthesizer.SynthesizeInto(selOp, selOut, ctx);
            selNet = selOut;
        }
        catch { return false; }

        var memInNet = running.TryGetValue(baseName, out var prev)
            ? prev
            : NetForLhs(baseVar, ctx);
        var memOutNet = ctx.NewInternal(Math.Max(1, baseVar.Size), sourceLine);
        int knownN = baseVar.Size > 0 && elemSize > 0 && baseVar.Size % elemSize == 0
            ? baseVar.Size / elemSize
            : 0;
        var attrs = new Dictionary<string, string>
        {
            ["Element"] = elemSize.ToString(),
            ["Source"]  = baseName,
        };
        if (knownN > 0) attrs["N"] = knownN.ToString();
        // Pass 19 (2026-07-30): mark writes originating from `:=` (process-
        // local variable assign) so the schematic renderer / audit can
        // distinguish them from signal `<=` writes. Semantically both
        // still route through a DFF at end-of-Synthesize for state
        // retention across process activations, but reads within the
        // same process pass see the immediate MemOut for variables.
        if (isVariable) attrs["IsVariable"] = "true";
        ctx.AddCell(
            kind:    CellKind.RAM_WRITE,
            inputs:  new[] {
                new Pin("MemIn", memInNet.Id),
                new Pin("Sel",   selNet.Id),
                new Pin("Din",   dataNet.Id),
            },
            outputs: new[] { new Pin("MemOut", memOutNet.Id) },
            sourceLine: sourceLine,
            attrs:   attrs);

        running[baseName] = memOutNet;
        return true;
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

            // Build OR-of-EQ for the choices. For VHDL-2008 `case?` (matching-
            // case, cs.IsMatching), any choice literal containing `-` acts
            // as a don't-care per-bit: we split the raw literal into (value,
            // mask) and build `EQ(selector AND mask, value)` instead of a
            // plain `EQ`. Choices with no `-` fall through to the plain path
            // — same wire budget as pre-2026-07-28.
            Net? matchNet = null;
            foreach (var raw in br.Choices)
            {
                Net eqOut = BuildChoiceCompare(cs.IsMatching, raw, selNet, selWidth, ctx, sourceLine);

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
    /// Build the per-choice comparator net for a case-arm. When
    /// `isMatching` is true (VHDL-2008 `case?`) and the choice literal
    /// contains `-` don't-care bits, we generate a mask+EQ pair; otherwise
    /// we fall through to a plain EQ against the raw literal — same shape
    /// as pre-Tier-4.2.
    /// </summary>
    internal static Net BuildChoiceCompare(bool isMatching, string raw, Net selNet,
        int selWidth, SynthesisContext ctx, string sourceLine)
    {
        if (isMatching)
        {
            string clean = raw.Replace("\"", "").Replace("'", "").Trim();
            if (clean.Contains('-'))
            {
                var value = new System.Text.StringBuilder(clean.Length);
                var mask  = new System.Text.StringBuilder(clean.Length);
                foreach (char c in clean)
                {
                    if (c == '-') { value.Append('0'); mask.Append('0'); }
                    else          { value.Append(c);   mask.Append('1'); }
                }
                // Requote so Constant()'s cleaner strips them the same way as raw.
                var valueNet = ctx.Constant("\"" + value + "\"", selWidth, sourceLine);
                var maskNet  = ctx.Constant("\"" + mask  + "\"", selWidth, sourceLine);
                var andOut = ctx.NewInternal(width: selWidth, sourceLine: sourceLine);
                ctx.AddCell(
                    kind: CellKind.AND,
                    inputs: new[] { new Pin("A", selNet.Id), new Pin("B", maskNet.Id) },
                    outputs: new[] { new Pin("Y", andOut.Id) },
                    sourceLine: sourceLine);
                var eqOut = ctx.NewInternal(width: 1, sourceLine: sourceLine);
                ctx.AddCell(
                    kind: CellKind.EQ,
                    inputs: new[] { new Pin("A", andOut.Id), new Pin("B", valueNet.Id) },
                    outputs: new[] { new Pin("Y", eqOut.Id) },
                    sourceLine: sourceLine,
                    attrs: new Dictionary<string, string> { ["Choice"] = raw, ["Mask"] = mask.ToString() });
                return eqOut;
            }
        }
        // Standard exact-match path.
        var literalNet = ctx.Constant(raw, selWidth, sourceLine);
        var plainEq = ctx.NewInternal(width: 1, sourceLine: sourceLine);
        ctx.AddCell(
            kind: CellKind.EQ,
            inputs: new[] { new Pin("A", selNet.Id), new Pin("B", literalNet.Id) },
            outputs: new[] { new Pin("Y", plainEq.Id) },
            sourceLine: sourceLine,
            attrs: new Dictionary<string, string> { ["Choice"] = raw });
        return plainEq;
    }

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
                case ProcedureCallStatement pcall:
                    // Tier 4.3 B2 (2026-07-28): a process containing a
                    // procedure call is walkable iff the call resolves to a
                    // registered body and that body itself walks. Runs the
                    // shared body-parser (cached on FunctionDefinition
                    // BoundBodyCache) — same output the runtime consumes.
                    try
                    {
                        var bound = pcall.ResolveBoundBody(null, out _, out _);
                        if (bound == null || bound.Count == 0) return false;
                        if (!CanWalk(bound)) return false;
                    }
                    catch { return false; }
                    break;
                case NullStatement:
                    // Tier 4.3 pass 9 (2026-07-29): `null;` is a no-op —
                    // walkable, doesn't drive any nets. Closes 5
                    // BlackboxProcess fallbacks whose case-arms use
                    // `when X => null;` (encoder-16to4, flipflop-jk,
                    // priority, hamming, ser-fsm).
                    break;
                case ForLoopStructure fls:
                    // Tier 4.3 pass 10 (2026-07-29): bounded for-loops
                    // walk via synth-time unroll — the range must be a
                    // pair of statically-known integer bounds (already
                    // enforced by parse-time int properties on the AST)
                    // and the body must itself be walkable. Iteration is
                    // handled in WalkTasks by mutating LoopVar.Value and
                    // re-walking Tasks per index; the DynamicIndex walker
                    // case picks up the current integer value and emits
                    // per-iteration static-index taps so the schematic
                    // shows a real cell chain instead of a black box.
                    if (fls.Tasks == null) return false;
                    if (!CanWalk(fls.Tasks)) return false;
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
                    case Operation dynWrite when TryExtractDynamicIndexBase(dynWrite.RawData) is string dynBase:
                        // Pass 17 (2026-07-29): dynamic-index writes land
                        // with Op.Result.Name == "ConditionResult" because
                        // the Operation constructor's LHS regex only
                        // handles static integer indices. The base signal
                        // still needs to appear in `running` so
                        // TryHandleDynamicIndexWrite's MemOut threads
                        // through the process-level MUX/DFF closure.
                        set.Add(dynBase);
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
            // Sync-reset shape: outer `if rising_edge(clk) then …`.
            var br = ifs.Branches[0];
            var (sig, rising) = DetectClockEdge(br.Condition);
            // Pattern-matching null check — see Synthesize() comment.
            // The Variable type's overloaded operators don't behave
            // correctly for two-sided null and would falsely report a
            // null sig as "non-null" here.
            if (sig is not null) return (sig, rising, br.Tasks);

            // Async-reset shape: `if reset = '1' then … elsif rising_edge(clk) then …`.
            // Recognise the reset-then-elsif-rising_edge pair, collapse the
            // rising_edge elsif to a plain `else`, and hand the synthesised
            // IfStructure to WalkTasks so the mux cascade + DFF wrap fire
            // as if the process had been written with sync semantics.
            // Simulation still runs through DeltaCycleEngine (behavioural),
            // so this rewrite only affects the schematic view — no
            // regression risk to SimulatorBench's async-reset cases.
            if (ifs.Branches.Count >= 2)
            {
                var (sigElsif, risingElsif) = DetectClockEdge(ifs.Branches[1].Condition);
                if (sigElsif is not null)
                {
                    var rewritten = new IfStructure(new HashSet<Variable>(), default!);
                    // Keep every branch before the clocked one verbatim (typically
                    // the async-reset branch, sometimes preceded by an async-set).
                    for (int i = 0; i < 1; i++)
                    {
                        rewritten.Branches.Add(ifs.Branches[i]);
                    }
                    // Elsif rising_edge(clk) → collapse to a plain else so the
                    // walker doesn't try to gate on the edge signal.
                    var clockedElse = new IfBranch { IsElse = true, Tasks = ifs.Branches[1].Tasks };
                    rewritten.Branches.Add(clockedElse);
                    return (sigElsif, risingElsif, new List<object> { rewritten });
                }
            }
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
                    if (found is not null)
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

        // Phase 4 (2026-07-22): include the type of the first unwalkable
        // task so the diagnostic explains what triggered the fallback
        // (procedure call, wait, for-loop, procedure-call, etc.) instead
        // of the generic "unsupported constructs" message.
        var firstUnwalkable = FirstUnwalkableTaskName(process.Tasks);
        var reason = firstUnwalkable != null
            ? $"Process body kept as opaque cell — first unwalkable task is {firstUnwalkable}."
            : "Process body kept as opaque cell — unsupported constructs encountered.";
        ctx.AddDiagnostic("BlackboxProcess", reason);
    }

    /// <summary>
    /// Phase 4 (2026-07-22): recursively find the first task shape that
    /// <see cref="CanWalk"/> would reject, and return its type name for
    /// use in the diagnostic message. Returns <c>null</c> if the tree is
    /// entirely walkable (shouldn't happen in the fallback path).
    /// </summary>
    private static string? FirstUnwalkableTaskName(IList<object>? tasks)
    {
        if (tasks == null) return "(null task list)";
        foreach (var t in tasks)
        {
            if (t is null) return "(null task)";
            if (t is Operation) continue;
            if (t is IfStructure ifs)
            {
                if (ifs.Branches == null) return "IfStructure (no branches)";
                foreach (var br in ifs.Branches)
                {
                    var sub = FirstUnwalkableTaskName(br?.Tasks);
                    if (sub != null) return $"if-branch → {sub}";
                }
                continue;
            }
            if (t is CaseStructure cs)
            {
                if (cs.Branches == null) return "CaseStructure (no branches)";
                foreach (var br in cs.Branches)
                {
                    var sub = FirstUnwalkableTaskName(br?.Tasks);
                    if (sub != null) return $"case-arm → {sub}";
                }
                continue;
            }
            return t.GetType().Name;
        }
        return null;
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
