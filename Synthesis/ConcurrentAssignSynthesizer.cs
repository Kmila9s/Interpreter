using Interpreter.Models;
using Interpreter.Services;
using Parser.Models;
using static Parser.Models.Constants;

namespace Interpreter.Synthesis;

/// <summary>
/// Folds an <see cref="Operation"/>'s postfix <c>Items</c> array into a
/// tree of <see cref="Cell"/>s. Operates on the same data the
/// <see cref="DeltaCycleEngine"/> consumes at runtime — there's no second
/// expression parser, just a stack walk that mirrors the runtime
/// evaluator but emits nets and cells instead of values.
/// </summary>
internal static class ConcurrentAssignSynthesizer
{
    /// <summary>
    /// Synthesise a single concurrent assignment. The output net is the
    /// LHS; the input nets are whatever real signals/ports the expression
    /// references. Returns the output net so callers (e.g. the conditional
    /// synthesizer) can chain results.
    /// </summary>
    public static Net Synthesize(Operation op, SynthesisContext ctx)
    {
        var resultNet = TargetNet(op.Target, op.Result, ctx);
        SynthesizeInto(op, resultNet, ctx);
        return resultNet;
    }

    /// <summary>
    /// Walks the postfix items and routes the final stack value into the
    /// caller-provided output net via a BUF (or directly when the final
    /// item is itself a single signal). Used by the conditional and
    /// selected synthesizers, which want to drive a pre-existing net
    /// (e.g. a MUX input) rather than create one for the LHS.
    /// </summary>
    public static void SynthesizeInto(Operation op, Net outputNet, SynthesisContext ctx)
    {
        var stack = new Stack<Net>();
        // Tag every emitted cell with the original VHDL line so the
        // Naive view can group cells back to their source assignment
        // and stamp the line text alongside the cluster. Truncated on
        // the renderer side so long expressions don't break layout.
        string? sourceLine = string.IsNullOrEmpty(op.RawData) ? null : op.RawData;

        foreach (var item in op.Items)
        {
            switch (item)
            {
                case Variable v:
                    stack.Push(NetForVariable(v, ctx));
                    break;
                case DeferredAttribute da:
                    // Tier 4.3 pass 6 (2026-07-28): postfix walker previously
                    // dropped DeferredAttribute on the floor, same failure
                    // mode as DeferredIndex before pass 5 — stack underflow
                    // on downstream operators for idioms like
                    // `clk'event and clk='1'`. 'event lowers to a real
                    // EDGE_DETECT cell (mirror of IEEEPrimitives.RisingEdgePrimitive
                    // so the rendered schematic is meaningful); the other
                    // attributes ('last_value, 'stable, 'active) emit a
                    // labeled tap-net so the walker stays balanced without
                    // pretending to render real hardware for constructs the
                    // synth path doesn't yet model.
                    if (da.AttributeName == "event")
                    {
                        var clkNet = NetForVariable(da.Signal, ctx);
                        var edgeOut = ctx.NewInternal(width: 1, sourceLine: sourceLine);
                        ctx.AddCell(
                            kind:    CellKind.EDGE_DETECT,
                            inputs:  new[] { new Pin("CLK", clkNet.Id) },
                            outputs: new[] { new Pin("Y",   edgeOut.Id) },
                            sourceLine: sourceLine,
                            attrs:   new Dictionary<string, string> { ["Edge"] = "any" });
                        stack.Push(edgeOut);
                    }
                    else if (da.AttributeName == "last_value")
                    {
                        // Pass 12 (2026-07-29): promote from labeled tap-net
                        // to a real LAST_VALUE cell. Renderer draws a
                        // labeled rectangle so the student sees the
                        // attribute node as its own primitive instead of
                        // a bare named wire.
                        var srcNet = NetForVariable(da.Signal, ctx);
                        int lvWidth = da.ResultType == DATATYPES.STD_LOGIC_VECTOR
                            ? Math.Max(1, da.Signal.Size)
                            : 1;
                        var lvOut = ctx.NewInternal(width: lvWidth, sourceLine: sourceLine);
                        ctx.AddCell(
                            kind:    CellKind.LAST_VALUE,
                            inputs:  new[] { new Pin("Src", srcNet.Id) },
                            outputs: new[] { new Pin("Y",   lvOut.Id) },
                            sourceLine: sourceLine);
                        stack.Push(lvOut);
                    }
                    else if (da.AttributeName == "stable")
                    {
                        // Pass 12 (2026-07-29): STABLE cell (1-bit output).
                        // TimeNs (nullable) rides on Attrs so the renderer
                        // can label the cell `stable(T ns)` when present.
                        var srcNet = NetForVariable(da.Signal, ctx);
                        var stOut = ctx.NewInternal(width: 1, sourceLine: sourceLine);
                        var attrs = da.TimeArgNs is long ns
                            ? new Dictionary<string, string> { ["TimeNs"] = ns.ToString() }
                            : null;
                        ctx.AddCell(
                            kind:    CellKind.STABLE,
                            inputs:  new[] { new Pin("Src", srcNet.Id) },
                            outputs: new[] { new Pin("Y",   stOut.Id) },
                            sourceLine: sourceLine,
                            attrs:   attrs);
                        stack.Push(stOut);
                    }
                    else
                    {
                        // Remaining attribute kinds ('active, etc.) keep
                        // the labeled tap-net fallback — no bench coverage
                        // yet, no need to commit a cell shape.
                        int attrWidth = da.ResultType == DATATYPES.STD_LOGIC_VECTOR
                            ? Math.Max(1, da.Signal.Size)
                            : 1;
                        string attrName = $"attr_{da.Signal.Name}__{da.AttributeName}";
                        var attrNet = ctx.GetOrCreateNet(attrName,
                            attrWidth > 1 ? NetKind.Bus : NetKind.Wire,
                            attrWidth, NetOrigin.Internal, sourceLine);
                        stack.Push(attrNet);
                    }
                    break;
                case DynamicIndex dyni:
                    // Tier 4.3 pass 7 (2026-07-28): dynamic-index reads
                    // previously fell through silently. Emit a labeled
                    // tap-net whose width matches the source's element
                    // size or scalar bit width.
                    // Pass 10 (2026-07-29): if IndexSource has a parseable
                    // integer Value (typical during ForLoop unroll where
                    // the walker mutates LoopVar.Value per iteration),
                    // emit a static-index tap keyed by {source}_idx_{i} —
                    // same convention as DeferredIndex — so per-iteration
                    // reads over the same array collapse to distinct nets
                    // and the schematic shows a proper N-cell chain
                    // instead of a single reused tap.
                    int dynElemWidth = dyni.Source.ElementSize > 0
                        ? dyni.Source.ElementSize
                        : Math.Max(1, dyni.Source.Size);

                    // Pass 10 static-index tap: if IndexSource has a
                    // parseable integer Value (ForLoop unroll case), emit
                    // a static-index tap {source}_idx_{k} so per-iteration
                    // reads collapse to distinct nets.
                    if (dyni.Index.Source?.Value is object idxRaw
                        && int.TryParse(idxRaw.ToString(), out int idxK))
                    {
                        string idxTapName = VectorIndexing.PartName(dyni.Source.Name, idxK);
                        var idxTapNet = ctx.GetOrCreateNet(idxTapName,
                            dynElemWidth > 1 ? NetKind.Bus : NetKind.Wire,
                            dynElemWidth, NetOrigin.Internal, sourceLine);
                        stack.Push(idxTapNet);
                        break;
                    }

                    // Pass 14 (2026-07-29) + pass 15 IsConstant gate: emit
                    // a ROM_LOOKUP cell when the source is a CONSTANT
                    // array-of-vectors (Value non-empty, ElementSize > 0,
                    // IsConstant true) and the index is genuinely dynamic
                    // (didn't statically resolve above). Signal-typed
                    // arrays (writable RAMs) can carry an initializer in
                    // Value; without the IsConstant gate, pass 14 would
                    // freeze that initializer as ROM data pins and
                    // misrepresent the RAM. Runtime is unaffected either
                    // way (DynamicIndex.Evaluate reads live Source.Value),
                    // but the schematic must not lie about read-only vs.
                    // writable memory. Signal-typed arrays fall to the
                    // tap-net fallback below (proper RAM_READ cell is a
                    // pass-16+ item).
                    string? sourceValue = dyni.Source.Value?.ToString();
                    int elemSize = dyni.Source.ElementSize;
                    if (elemSize > 0 && !string.IsNullOrEmpty(sourceValue)
                        && sourceValue.Length >= elemSize
                        && sourceValue.Length % elemSize == 0
                        && dyni.Source.IsConstant)
                    {
                        int n = sourceValue.Length / elemSize;

                        // The selector net; a placeholder constant keeps the
                        // cell wired up in the degenerate case.
                        Net selNet = SelectorNet(dyni.Index, ctx, sourceLine) ?? ctx.Constant("@rom_sel", 1);

                        // Emit N constant data pins. MSB-first storage
                        // matches DynamicIndex.Evaluate at line 74:
                        // `vec.Substring(start=High*elemSize, elemSize)`.
                        var romInputs = new List<Pin>(n + 1) { new Pin("Sel", selNet.Id) };
                        for (int k = 0; k < n; k++)
                        {
                            int start = k * elemSize;
                            string slot = sourceValue.Substring(start, elemSize);
                            var constNet = ctx.Constant(slot, elemSize);
                            romInputs.Add(new Pin($"D{k}", constNet.Id));
                        }

                        var romOut = ctx.NewInternal(elemSize, sourceLine);
                        ctx.AddCell(
                            kind:    CellKind.ROM_LOOKUP,
                            inputs:  romInputs,
                            outputs: new[] { new Pin("Y", romOut.Id) },
                            sourceLine: sourceLine,
                            attrs:   new Dictionary<string, string>
                            {
                                ["N"]       = n.ToString(),
                                ["Element"] = elemSize.ToString(),
                            });
                        stack.Push(romOut);
                        break;
                    }

                    // Pass 16 (2026-07-29): signal-typed array of vectors —
                    // the RAM counterpart of ROM_LOOKUP. No frozen data
                    // pins (the memory's contents change at runtime); the
                    // source signal net feeds a `Src` pin and the cell
                    // semantically slices at `Sel * Element`. Runtime is
                    // unaffected — DynamicIndex.Evaluate handles the real
                    // slice; the cell exists purely so the schematic
                    // shows a `ram(N)` box instead of a nameless wire.
                    if (elemSize > 0 && !dyni.Source.IsConstant)
                    {
                        // Selector (same synthesise path as ROM_LOOKUP).
                        Net ramSelNet = SelectorNet(dyni.Index, ctx, sourceLine) ?? ctx.Constant("@ram_sel", 1);

                        // Source signal net. For a signal-typed array,
                        // NetForVariable creates a bus net of Source.Size
                        // bits when the array is declared with explicit
                        // range, or falls to width=1 otherwise. Either way
                        // the RAM_READ cell just needs SOMETHING to wire
                        // into `Src`.
                        var srcNet = NetForVariable(dyni.Source, ctx);
                        int knownN = !string.IsNullOrEmpty(sourceValue)
                                     && sourceValue.Length % elemSize == 0
                            ? sourceValue.Length / elemSize
                            : 0;

                        var ramOut = ctx.NewInternal(elemSize, sourceLine);
                        var ramAttrs = new Dictionary<string, string>
                        {
                            ["Element"] = elemSize.ToString(),
                            ["Source"]  = dyni.Source.Name,
                        };
                        if (knownN > 0) ramAttrs["N"] = knownN.ToString();
                        ctx.AddCell(
                            kind:    CellKind.RAM_READ,
                            inputs:  new[] {
                                new Pin("Src", srcNet.Id),
                                new Pin("Sel", ramSelNet.Id),
                            },
                            outputs: new[] { new Pin("Y", ramOut.Id) },
                            sourceLine: sourceLine,
                            attrs:   ramAttrs);
                        stack.Push(ramOut);
                        break;
                    }

                    // Fallback (unusual shape — no ElementSize, or scalar
                    // bit-vector indexed dynamically): labeled tap-net
                    // (pass 7 behavior).
                    string dynDisambig = dyni.Index.Source?.Name ?? "expr";
                    string dynTapName = $"{dyni.Source.Name}_dyn_{dynDisambig}";
                    var dynTapNet = ctx.GetOrCreateNet(dynTapName,
                        dynElemWidth > 1 ? NetKind.Bus : NetKind.Wire,
                        dynElemWidth, NetOrigin.Internal, sourceLine);
                    stack.Push(dynTapNet);
                    break;
                case DeferredIndex di:
                    // Tier 4.3 pass 5 (2026-07-28): postfix walker previously
                    // dropped DeferredIndex on the floor, leaving the stack
                    // short so subsequent operators reported Underflow (or
                    // whole-expression drops surfaced as EmptyExpression on
                    // per-arm assigns like `y <= x(0)` inside a case). Emit a
                    // stable taps-net keyed by (source, high[, low]) matching
                    // the `_idx_` naming convention that NetForVariable
                    // already recognises for name-based bit taps — the
                    // renderer draws the tap on its own without an explicit
                    // BUF cell. Width follows IsSlice.
                    string tapName = VectorIndexing.PartName(di.Source.Name, di.High, di.IsSlice ? di.Low : null);
                    int tapWidth = di.IsSlice
                        ? Math.Max(1, di.High - di.Low + 1)
                        : 1;
                    var tapNet = ctx.GetOrCreateNet(tapName,
                        tapWidth > 1 ? NetKind.Bus : NetKind.Wire,
                        tapWidth, NetOrigin.Internal, sourceLine);
                    stack.Push(tapNet);
                    break;
                case DeferredUserFunction duf:
                    // Tier 4.3 B1 (2026-07-28): attempt to inline the function
                    // body — walk its ReturnStatement, bind formals→caller nets
                    // via scope alias, emit cells + intermediate nets. Falls
                    // back to the opaque-constant + UserFunctionBlackBox
                    // diagnostic shape when the body is outside the MVP scope
                    // (multiple returns, locals, branches, self-recursion).
                    var inlined = TryInlineUserFunction(duf, ctx, sourceLine);
                    if (inlined != null)
                    {
                        stack.Push(inlined);
                    }
                    else
                    {
                        int dufWidth = duf.ReturnType == DATATYPES.STD_LOGIC_VECTOR
                            ? Math.Max(1, duf.ReturnTypeSize)
                            : 1;
                        stack.Push(ctx.Constant($"UserFunction-{duf.FunctionName}", dufWidth));
                        ctx.AddDiagnostic(
                            "UserFunctionBlackBox",
                            $"User-declared function '{duf.FunctionName}' is called but its body " +
                            "was not decomposed into cells — schematic shows an opaque constant " +
                            "net. Runtime execution is correct (via DeferredUserFunction); " +
                            "schematic-side body inlining fell back for this callsite " +
                            "(non-single-return body, self-recursion, or arg-resolve failure).",
                            sourceLine,
                            DiagnosticSeverity.Info);
                    }
                    break;
                case DeferredFunction df:
                    // Try the IEEE primitive table first — for known names
                    // (rising_edge, falling_edge, …) this emits a real
                    // EDGE_DETECT (or other primitive) cell instead of a
                    // placeholder constant, so the netlist reflects the
                    // actual hardware shape.
                    var ieeeArgs = ResolveDeferredArgs(df, ctx);
                    var lowered  = IEEEPrimitives.TryLower(df.FunctionName,
                        new IEEEPrimitiveArgs(ieeeArgs, ctx, sourceLine));
                    if (lowered is { } r)
                    {
                        stack.Push(r.OutputNet);
                    }
                    else
                    {
                        // Unknown function — emit a black-box constant net
                        // so the rest of the expression can still wire up,
                        // and surface a diagnostic.
                        stack.Push(ctx.Constant($"@{df.FunctionName}", 1));
                        ctx.AddDiagnostic(
                            "UnknownIEEEFunction",
                            $"No IEEE primitive registered for '{df.FunctionName}' — " +
                            "emitted a constant placeholder. The simulator's runtime " +
                            "evaluator handles the call but the netlist won't reflect " +
                            "the real hardware shape.",
                            sourceLine);
                    }
                    break;
                case Operator opr:
                    HandleOperator(opr, stack, ctx, sourceLine);
                    break;
            }
        }

        if (stack.Count == 0)
        {
            // Empty expression — leave the output net dangling rather than
            // throwing. Diagnostic surfaces the issue to the user instead.
            ctx.AddDiagnostic("EmptyExpression",
                $"Assignment to '{outputNet.Name}' produced no expression tree.", sourceLine);
            return;
        }

        var top = stack.Pop();
        if (top.Id == outputNet.Id)
            return; // already wired (e.g. y <= y)

        // Drive the output net with a BUF cell so the schematic shows an
        // explicit wire rather than implicitly aliasing two named nets.
        ctx.AddCell(
            kind: CellKind.BUF,
            inputs: new[] { new Pin("A", top.Id) },
            outputs: new[] { new Pin("Y", outputNet.Id) },
            sourceLine: sourceLine);
    }

    /// <summary>
    /// Resolves a <see cref="DeferredFunction"/>'s arguments to existing
    /// nets in the synthesis context. Each argument may be a Variable, a
    /// Net id, or a string name; whichever shape it has, we look up the
    /// matching net (creating a constant if none exists yet) so the IEEE
    /// primitive emitter has well-typed inputs to wire to.
    /// </summary>
    private static IReadOnlyList<Net> ResolveDeferredArgs(DeferredFunction df, SynthesisContext ctx)
    {
        var nets = new List<Net>(df.Arguments.Length);
        foreach (var arg in df.Arguments)
        {
            switch (arg)
            {
                case Variable v:
                    nets.Add(NetForVariable(v, ctx));
                    break;
                case Net n:
                    nets.Add(n);
                    break;
                case DeferredFunction nested:
                    // Nested IEEE call (e.g. the `unsigned(addr)` inside
                    // `to_integer(unsigned(addr))`). Recursively resolve
                    // its own args, lower it through the same primitive
                    // table, and use the resulting output net as the
                    // parent's input pin. When the inner name has no
                    // primitive (StandardFunctions.IsStandardFunction
                    // covers some legacy aliases that the table doesn't
                    // mirror), fall back to a placeholder constant so
                    // the parent at least wires up.
                    var nestedArgs = ResolveDeferredArgs(nested, ctx);
                    var nestedR    = IEEEPrimitives.TryLower(
                        nested.FunctionName,
                        new IEEEPrimitiveArgs(nestedArgs, ctx, SourceLine: null));
                    if (nestedR is { } nr) nets.Add(nr.OutputNet);
                    else nets.Add(ctx.Constant($"@nested_{nested.FunctionName}", 1));
                    break;
                case DeferredExpression dexpr:
                    // Sub-expression passed as a function argument, e.g. the
                    // `resize(unsigned(a), 5) + resize(unsigned(b), 5)` inside
                    // `std_logic_vector(...)`. The parser wraps such argument
                    // expressions in DeferredExpression rather than folding
                    // them into the outer postfix stream, so the synthesizer
                    // must walk the buffered items and emit an equivalent
                    // cell chain here. Left-associative fold mirrors
                    // DeferredExpression.Evaluate() at runtime.
                    nets.Add(ResolveDeferredExpression(dexpr, ctx));
                    break;
                case string s when df.Variables.FirstOrDefault(
                    x => x.Name.Equals(s, StringComparison.OrdinalIgnoreCase)) is { } found:
                    nets.Add(NetForVariable(found, ctx));
                    break;
                case int i:
                    // Width literal — encode as a single-bit constant whose
                    // Width property carries the integer payload via the
                    // emitter's interpretation of the net's Width field.
                    nets.Add(ctx.Constant(i.ToString(), Math.Max(1, i)));
                    break;
                default:
                    nets.Add(ctx.Constant($"@arg_{arg?.GetType().Name ?? "null"}", 1));
                    break;
            }
        }
        return nets;
    }

    /// <summary>
    /// Synthesise a <see cref="DeferredExpression"/> — the buffered
    /// sub-expression the parser stashes when an outer function call
    /// receives a mixed operator+operand tree as an argument. Items are
    /// laid out infix: <c>[operand, op, operand, op, operand, …]</c>.
    /// We emit one cell per operator, left-associatively, and return the
    /// net that carries the final result.
    /// </summary>
    private static Net ResolveDeferredExpression(DeferredExpression dexpr, SynthesisContext ctx)
    {
        var items = dexpr.Items;
        if (items.Count == 0) return ctx.Constant("@dexpr_empty", 1);
        if (items.Count == 1) return ResolveDeferredItem(items[0], ctx);

        // Shape must alternate operand-op-operand-op-…; malformed input
        // falls back to the first operand (matches Evaluate's guard).
        for (int i = 0; i < items.Count; i++)
        {
            bool shouldBeOp = (i % 2) == 1;
            bool isOp = items[i] is Operator;
            if (shouldBeOp != isOp) return ResolveDeferredItem(items[0], ctx);
        }

        Net acc = ResolveDeferredItem(items[0], ctx);
        for (int i = 1; i + 1 < items.Count; i += 2)
        {
            var opr = (Operator)items[i];
            var right = ResolveDeferredItem(items[i + 1], ctx);
            var (kind, isUnary) = ClassifyOperator(opr);
            if (isUnary) continue; // shouldn't appear infix — skip defensively

            int outWidth = Math.Max(acc.Width, right.Width);
            if (opr.OperatorType == OperatorType.COMPARATION) outWidth = 1;

            var outNet = ctx.NewInternal(width: outWidth, sourceLine: null);
            ctx.AddCell(
                kind: kind,
                inputs: new[] { new Pin("A", acc.Id), new Pin("B", right.Id) },
                outputs: new[] { new Pin("Y", outNet.Id) },
                sourceLine: null,
                attrs: new Dictionary<string, string> { ["Op"] = opr.OperatorName });
            acc = outNet;
        }
        return acc;
    }

    /// <summary>
    /// Resolve one item inside a <see cref="DeferredExpression"/> to a net.
    /// Mirrors the operand branches of the outer expression walker but
    /// operates on a single-item basis so
    /// <see cref="ResolveDeferredExpression"/> can drive it.
    /// </summary>
    private static Net ResolveDeferredItem(object item, SynthesisContext ctx)
    {
        switch (item)
        {
            case Variable v:
                return NetForVariable(v, ctx);
            case DeferredFunction df:
                var args = ResolveDeferredArgs(df, ctx);
                var r = IEEEPrimitives.TryLower(df.FunctionName,
                    new IEEEPrimitiveArgs(args, ctx, SourceLine: null));
                if (r is { } lower) return lower.OutputNet;
                return ctx.Constant($"@dexpr_{df.FunctionName}", 1);
            case DeferredExpression nested:
                return ResolveDeferredExpression(nested, ctx);
            case int n:
                return ctx.Constant(n.ToString(), Math.Max(1, n));
            case string s:
                return ctx.Constant("Literal-" + s, s.Length > 0 ? s.Length : 1);
            default:
                return ctx.Constant($"@dexpr_item_{item?.GetType().Name ?? "null"}", 1);
        }
    }

    private static void HandleOperator(Operator opr, Stack<Net> stack, SynthesisContext ctx, string? sourceLine)
    {
        var (kind, isUnary) = ClassifyOperator(opr);

        // NOT is unary; everything else is binary. The runtime evaluator
        // pops one operand for unary, two for binary — we mirror that.
        if (isUnary)
        {
            if (stack.Count == 0)
            {
                ctx.AddDiagnostic("Underflow", $"Unary operator '{opr.OperatorName}' had no operand.", sourceLine);
                return;
            }
            var a = stack.Pop();
            var outNet = ctx.NewInternal(width: a.Width, sourceLine: sourceLine);
            ctx.AddCell(kind, new[] { new Pin("A", a.Id) }, new[] { new Pin("Y", outNet.Id) }, sourceLine,
                attrs: new Dictionary<string, string> { ["Op"] = opr.OperatorName });
            stack.Push(outNet);
            return;
        }

        if (stack.Count < 2)
        {
            ctx.AddDiagnostic("Underflow", $"Binary operator '{opr.OperatorName}' missing operands.", sourceLine);
            return;
        }
        var right = stack.Pop();
        var left = stack.Pop();
        int outWidth = Math.Max(left.Width, right.Width);

        // Comparators always produce a 1-bit result regardless of input width.
        if (opr.OperatorType == OperatorType.COMPARATION)
            outWidth = 1;

        var output = ctx.NewInternal(width: outWidth, sourceLine: sourceLine);
        ctx.AddCell(
            kind: kind,
            inputs: new[] { new Pin("A", left.Id), new Pin("B", right.Id) },
            outputs: new[] { new Pin("Y", output.Id) },
            sourceLine: sourceLine,
            attrs: new Dictionary<string, string> { ["Op"] = opr.OperatorName });
        stack.Push(output);
    }

    /// <summary>
    /// Maps a runtime <see cref="Operator"/> to a synthesis-time
    /// <see cref="CellKind"/>. The classification mirrors the precedence
    /// table in <see cref="Operator.DefinePrecedence(string)"/> but at
    /// the cell-kind level rather than the precedence level.
    /// </summary>
    private static (CellKind kind, bool isUnary) ClassifyOperator(Operator opr)
    {
        string name = opr.OperatorName.ToLowerInvariant();
        return name switch
        {
            "and" => (CellKind.AND, false),
            "nand" => (CellKind.NAND, false),
            "or" => (CellKind.OR, false),
            "nor" => (CellKind.NOR, false),
            "xor" => (CellKind.XOR, false),
            "xnor" => (CellKind.XNOR, false),
            "not" => (CellKind.NOT, true),

            "+" => (CellKind.ADD, false),
            "-" => (CellKind.SUB, false),
            "*" => (CellKind.MUL, false),
            "/" => (CellKind.DIV, false),
            "**" => (CellKind.POW, false),
            "mod" => (CellKind.MOD, false),

            "sll" or "sla" => (CellKind.SHL, false),
            "srl" or "sra" => (CellKind.SHR, false),
            "rol" => (CellKind.ROL, false),
            "ror" => (CellKind.ROR, false),

            "=" => (CellKind.EQ, false),
            "!=" or "/=" => (CellKind.NEQ, false),
            "<" => (CellKind.LT, false),
            "<=" => (CellKind.LTE, false),
            ">" => (CellKind.GT, false),
            ">=" => (CellKind.GTE, false),

            // Anything we don't recognise becomes a BUF so the diagram
            // stays connected — better than a dangling pin.
            _ => (CellKind.BUF, false),
        };
    }

    /// <summary>
    /// The net carrying a run-time index: the index variable's own net, or
    /// the index expression synthesised into a new net; null when it cannot
    /// be built. Shared by ROM/RAM reads and RAM writes.
    /// </summary>
    internal static Net? SelectorNet(IndexValue index, SynthesisContext ctx, string? sourceLine)
    {
        if (index.Source is not null) return NetForVariable(index.Source, ctx);
        if (string.IsNullOrEmpty(index.Expression) || index.Scope is null) return null;
        try
        {
            var selOut = ctx.NewInternal(1, sourceLine);
            SynthesizeInto(new Operation(index.Expression, index.Scope), selOut, ctx);
            return selOut;
        }
        catch { return null; }
    }

    /// <summary>The net an assignment to <paramref name="target"/> drives: its bit-select net for a literal part, else the whole <paramref name="whole"/>.</summary>
    internal static Net TargetNet(AssignmentTarget? target, Variable whole, SynthesisContext ctx) =>
        PartTargetNet(target, ctx) ?? NetForVariable(whole, ctx);

    /// <summary>
    /// The bit-select net a part target with literal indices (<c>c(4)</c>,
    /// <c>v(3 downto 2)</c>) of an internal signal drives; null for a whole
    /// target, a run-time index or a port (ports stay whole, like component
    /// actuals in <c>ComponentSynthesizer.ResolveActualNet</c>).
    /// </summary>
    internal static Net? PartTargetNet(AssignmentTarget? target, SynthesisContext ctx)
    {
        if (target is not { IsPartial: true, StaticIndices: { } ix } || target.Base.IsPort) return null;
        int high = Math.Max(ix.Left, ix.Right), low = Math.Min(ix.Left, ix.Right);
        int width = VectorIndexing.RangeWidth(target.Base, ix.Left, ix.Right);
        return ctx.GetOrCreateNet(
            VectorIndexing.PartName(target.Base.Name, high, target.Right is null ? null : low),
            width > 1 ? NetKind.Bus : NetKind.Wire, width, NetOrigin.Signal);
    }

    /// <summary>
    /// Resolves a parsed <see cref="Variable"/> to its corresponding net.
    /// Literals get a CONST net (with the literal value embedded in the
    /// name so the renderer can stamp it on the wire). Indexed accesses
    /// like <c>a(3)</c> get their own net so the schematic shows the
    /// slice as a distinct wire — Phase 5's bus-explode toggle reads
    /// the BusBit attribute to decide whether to pinch the bus open.
    /// </summary>
    private static Net NetForVariable(Variable v, SynthesisContext ctx)
    {
        // Tier 4.3 B3 (2026-07-28): if a body walker has pushed a scope
        // frame, the callee-local name (formal parameter or synth-materialised
        // local) shadows any architecture-scope net that happens to share the
        // identifier. Checked before literals/aggregates so a formal named
        // "Literal-x" (hypothetical) still binds correctly.
        if (ctx.TryResolveAlias(v.Name, out var aliased))
            return aliased!;

        if (v.Name.StartsWith("Literal-", StringComparison.Ordinal))
        {
            string lit = v.Value?.ToString() ?? "0";
            int width = v.Type == DATATYPES.STD_LOGIC_VECTOR ? Math.Max(1, lit.Replace("\"", "").Length) : 1;
            return ctx.Constant(lit, width);
        }
        if (v.Name.StartsWith("Aggregate_others_", StringComparison.Ordinal))
        {
            return ctx.Constant(v.Value?.ToString() ?? "0", v.Size);
        }
        if (v.Name.StartsWith("Function-", StringComparison.Ordinal) ||
            v.Name.StartsWith("UserFunction-", StringComparison.Ordinal) ||
            v.Name.StartsWith("Attribute-", StringComparison.Ordinal) ||
            v.Name.StartsWith("DeferredFunction-", StringComparison.Ordinal))
        {
            // Side-effect-free functions and signal attributes resolved at
            // parse time. Treat as an opaque constant input — gives the
            // user a labelled stub on the schematic without pretending we
            // synthesised the function body.
            //
            // Phase 4 (2026-07-22): surface a "UserFunctionBlackBox" diagnostic
            // for user-declared function calls so the schematic viewer's
            // diagnostics pane makes the degradation visible. Previously the
            // stub-constant emit was silent — users saw a labelled net with
            // no explanation of why the function's body wasn't decomposed
            // into cells. IEEE primitives that miss the TryLower table are
            // already surfaced via "UnknownIEEEFunction" (line ~72).
            if (v.Name.StartsWith("UserFunction-", StringComparison.Ordinal))
            {
                var fnName = v.Name.Substring("UserFunction-".Length);
                ctx.AddDiagnostic(
                    "UserFunctionBlackBox",
                    $"User-declared function '{fnName}' is called but its body was " +
                    "not decomposed into cells — schematic shows an opaque constant " +
                    "net. Runtime execution is correct (via DeferredUserFunction); " +
                    "schematic-side inlining is Phase 6+ work.",
                    sourceLine: null,
                    severity: DiagnosticSeverity.Info);
            }
            return ctx.Constant(v.Name, v.Size > 0 ? v.Size : 1);
        }
        if (v.Name.Contains("_idx_", StringComparison.Ordinal))
        {
            // a_idx_3 → tap from a's bus. Recreate the parent name so the
            // net resolves to the same Net as a top-level reference.
            int idx = v.Name.IndexOf("_idx_", StringComparison.Ordinal);
            string parent = v.Name[..idx];
            // Use a stable taps net name keyed on parent + bit so multiple
            // references to a(3) collapse to a single wire.
            return ctx.GetOrCreateNet(v.Name,
                NetKind.Wire, 1, NetOrigin.Internal);
        }

        int w = v.Type == DATATYPES.STD_LOGIC_VECTOR ? Math.Max(1, v.Size) : 1;
        var origin = v.IsPort
            ? v.PortType switch
            {
                PortType.IN => NetOrigin.PortIn,
                PortType.OUT => NetOrigin.PortOut,
                PortType.INOUT => NetOrigin.PortInOut,
                _ => NetOrigin.PortIn,
            }
            : NetOrigin.Signal;
        return ctx.GetOrCreateNet(v.Name, w > 1 ? NetKind.Bus : NetKind.Wire, w, origin);
    }

    /// <summary>
    /// Tier 4.3 B1 (2026-07-28): attempt to inline a user function body at synth
    /// time. MVP scope covers single-<c>return E;</c> bodies with no local
    /// variable writes and no self-recursion. Returns the return-expression's
    /// output net on success; null on any fall-back path (unregistered function,
    /// arg-count mismatch, non-single-return body, self-recursion, parse or
    /// synth error). Callers must handle null by emitting the opaque-constant
    /// fallback.
    /// </summary>
    private static Net? TryInlineUserFunction(DeferredUserFunction duf, SynthesisContext ctx, string? sourceLine)
    {
        FunctionDefinition? fdef;
        try { fdef = PackageRegistry.Instance.GetFunction(duf.FunctionName); }
        catch { return null; }

        if (fdef == null || string.IsNullOrEmpty(fdef.BodyRaw)) return null;
        if (duf.ArgExpressions.Count != fdef.Parameters.Count) return null;

        // Self-recursion / mutual re-entry guard.
        if (!ctx.BeginInline(duf.FunctionName)) return null;
        try
        {
            // Ensure the body is parsed. ParseFunctionBodyStatic is pure — safe
            // to call at synth time without a live simulation. Cache the parse
            // on the FunctionDefinition so the runtime path can reuse it.
            var body = fdef.SynthesizedBody;
            if (body == null)
            {
                try
                {
                    var parseScope = BuildInliningScope(fdef, duf.Variables);
                    var tokenizer = new Parser.Repositories.Tokenizer();
                    var sourceLines = fdef.BodyLines != null && fdef.BodyLines.Length > 0
                        ? fdef.BodyLines
                        : fdef.BodyRaw.Split('\n');
                    foreach (var line in sourceLines)
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                            tokenizer.TokenizeLine(line);
                    }
                    tokenizer.Evaluate();
                    var tokenDict = tokenizer.GetDictionaryType();
                    if (tokenDict == null || tokenDict.Count == 0) return null;
                    var streamer = new Parser.Repositories.TokenStream(tokenDict);
                    body = Interpreter.Services.FunctionExecutor.ParseFunctionBodyStatic(streamer, parseScope);
                    // Do NOT cache back onto fdef.SynthesizedBody — the runtime
                    // path binds it against its own scope; sharing that pointer
                    // would leak synth-time Variables into runtime evaluation.
                }
                catch { return null; }
            }

            // MVP scope covers three body shapes:
            //   (1) single ReturnStatement,
            //   (2) single IfStructure whose leaves are Returns (N-arm mux),
            //   (3) prefix of local-variable AssignmentStatements (represented
            //       by Operation instances whose Result names a fdef local)
            //       followed by (1) or (2) as the terminal task.
            // Pass 20 (2026-07-30): additionally, an "early-return" shape
            // where guard-return ifs (no else) precede a terminal return
            // is canonicalised into shape (2) by TryCanonicalizeMultiReturn
            // before the terminal-shape gate. Anything else — signal
            // writes, loops, non-return terminals, writes to non-local
            // names — still falls back.
            if (body == null || body.Count < 1) return null;
            var canonical = TryCanonicalizeMultiReturn(body);
            if (canonical != null) body = canonical.ToList();

            // Split body into an assignment prefix + a terminal statement.
            int terminalIndex = body.Count - 1;
            object terminal = body[terminalIndex];
            bool isReturnTerminal = terminal is Interpreter.Repositories.ReturnStatement;
            bool isIfTerminal     = terminal is Interpreter.Repositories.IfStructure;
            bool isCaseTerminal   = terminal is Interpreter.Repositories.CaseStructure;
            if (!isReturnTerminal && !isIfTerminal && !isCaseTerminal) return null;

            // Every prefix task must either be
            //   - an Operation writing to a Variable declared as a local in
            //     this function definition, OR
            //   - a ProcedureCallStatement whose bound body walks (pass 5:
            //     reuses ProcessSynthesizer.WalkTasks; writes to caller-scope
            //     locals thread through the scope-alias stack).
            // Anything else — signal writes, loops, unknown-name Operation
            // writes — drops us to the black-box fallback.
            var localsByName = fdef.LocalVariables.ToDictionary(
                v => v.Name, v => v, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < terminalIndex; i++)
            {
                switch (body[i])
                {
                    case Operation prefixOp:
                        if (prefixOp.Result is null || string.IsNullOrEmpty(prefixOp.Result.Name)) return null;
                        if (!localsByName.ContainsKey(prefixOp.Result.Name)) return null;
                        break;
                    case Interpreter.Repositories.ProcedureCallStatement _:
                        // Vet at execution time (ResolveBoundBody may throw).
                        break;
                    case Interpreter.Repositories.ForLoopStructure _:
                        // Pass 24 (2026-07-30): bounded for-loops in fn body
                        // prefix — unrolled at synth time via
                        // ProcessSynthesizer.WalkTasks (pass-10 machinery).
                        // Each iteration writes to fn-locals through the
                        // scope-alias stack; only local writes are recognised
                        // (signal writes inside the loop would still bail via
                        // the walker's LHS lookup).
                        break;
                    default:
                        return null;
                }
            }

            // Resolve each arg expression to a caller-side net (shared by all
            // body shapes).
            var aliases = new Dictionary<string, Net>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < duf.ArgExpressions.Count; i++)
            {
                var argNet = ResolveArgExpressionToNet(duf.ArgExpressions[i], duf.Variables, ctx, sourceLine);
                if (argNet == null) return null;
                aliases[fdef.Parameters[i].Name] = argNet;
            }

            int retWidth = fdef.ReturnType == DATATYPES.STD_LOGIC_VECTOR
                ? Math.Max(1, fdef.ReturnTypeSize)
                : 1;

            ctx.PushScope(aliases);
            try
            {
                var bodyScope = BuildInliningScope(fdef, duf.Variables);

                // Walk local-write prefix. Each Operation write drives a
                // fresh internal net through SynthesizeInto and registers a
                // scope alias so later reads pick up the most-recent driver.
                // Pure SSA rename — subsequent writes overwrite the alias
                // (no phi needed since the prefix has no branches).
                //
                // Pass 5: also handles ProcedureCallStatement — resolve the
                // procedure's bound body (formals textually substituted with
                // actuals by ResolveBoundBody), walk it via ProcessSynthesizer.WalkTasks
                // against a fresh per-call `running` dict, then transfer any
                // writes to caller-side locals into the scope-alias stack so
                // the terminal read paths see the procedure's effect.
                for (int i = 0; i < terminalIndex; i++)
                {
                    switch (body[i])
                    {
                        case Operation prefixOp:
                        {
                            var local    = localsByName[prefixOp.Result.Name];
                            int width    = local.Type == DATATYPES.STD_LOGIC_VECTOR
                                ? Math.Max(1, local.Size)
                                : 1;
                            var writeNet = ctx.NewInternal(width, sourceLine);
                            SynthesizeInto(prefixOp, writeNet, ctx);
                            ctx.SetAlias(local.Name, writeNet);
                            break;
                        }
                        case Interpreter.Repositories.ProcedureCallStatement pcall:
                        {
                            IList<object>? bound;
                            HashSet<Variable>? boundScope;
                            try
                            {
                                bound = pcall.ResolveBoundBody(null, out boundScope);
                            }
                            catch { return null; }
                            if (bound == null || bound.Count == 0 || boundScope == null) continue;

                            var running = new Dictionary<string, Net>(StringComparer.OrdinalIgnoreCase);
                            ProcessSynthesizer.WalkTasks(bound, boundScope, ctx, running, sourceLine ?? string.Empty);

                            // Transfer writes to function locals into the
                            // scope-alias stack so terminal reads pick them up.
                            foreach (var (name, net) in running)
                            {
                                if (localsByName.ContainsKey(name)) ctx.SetAlias(name, net);
                            }
                            break;
                        }
                        case Interpreter.Repositories.ForLoopStructure floop:
                        {
                            // Pass 24 (2026-07-30): unroll a bounded for-loop
                            // via ProcessSynthesizer.WalkTasks. Iteration
                            // writes accumulate into a per-loop `running`
                            // map; at end-of-loop we transfer local-name
                            // writes into the fn's scope-alias stack so the
                            // terminal return picks up the final iteration's
                            // driver net. Loop body may not itself contain a
                            // ReturnStatement — the pass-10 walker has no
                            // case for it; if the body needs a return-in-loop
                            // shape the whole inline still falls to opaque.
                            var running = new Dictionary<string, Net>(StringComparer.OrdinalIgnoreCase);
                            // Seed running with current alias binding for each
                            // local so loop-carried reads pick up prior
                            // writes (rather than the local's initial value).
                            foreach (var (lname, lvar) in localsByName)
                            {
                                if (ctx.TryResolveAlias(lname, out var prevNet) && prevNet != null)
                                    running[lname] = prevNet;
                            }
                            ProcessSynthesizer.WalkTasks(new List<object> { floop },
                                bodyScope, ctx, running, sourceLine ?? string.Empty);
                            foreach (var (name, net) in running)
                            {
                                if (localsByName.ContainsKey(name)) ctx.SetAlias(name, net);
                            }
                            break;
                        }
                    }
                }

                // Terminal shape 1: single-return body.
                if (isReturnTerminal)
                {
                    var ret = (Interpreter.Repositories.ReturnStatement)terminal;
                    if (!ret.HasExpression || string.IsNullOrEmpty(ret.ExpressionString)) return null;
                    var retOp = new Operation(ret.ExpressionString, bodyScope);
                    var outNet = ctx.NewInternal(retWidth, sourceLine);
                    SynthesizeInto(retOp, outNet, ctx);
                    return outNet;
                }

                // Terminal shape 2: if/elsif/else where every leaf is a return.
                if (terminal is Interpreter.Repositories.IfStructure ifs)
                {
                    return TryInlineIfElseAsMux(ifs, bodyScope, retWidth, ctx, sourceLine, localsByName);
                }

                // Terminal shape 3: case-of-returns (Tier 4.3 pass 4 item B).
                if (terminal is Interpreter.Repositories.CaseStructure cs)
                {
                    return TryInlineCaseAsMux(cs, bodyScope, retWidth, ctx, sourceLine, localsByName);
                }

                return null;
            }
            catch { return null; }
            finally { ctx.PopScope(); }
        }
        finally
        {
            ctx.EndInline(duf.FunctionName);
        }
    }

    /// <summary>
    /// Pass 20 (2026-07-30): fold a function body that uses the classic
    /// "early-return" idiom
    /// <code>
    ///   if c1 then return E1; end if;
    ///   if c2 then return E2; end if;
    ///   ...
    ///   return En;
    /// </code>
    /// into the flat if/elsif/else canonical form the existing
    /// <see cref="TryInlineIfElseAsMux"/> already lowers. The rewrite
    /// mutates the first leading <c>IfStructure</c> in place — appending
    /// elsif branches from subsequent guard-return ifs and an <c>else</c>
    /// branch from the terminal return — then returns a single-element
    /// task list <c>[canonicalIf]</c>. Callers should re-check the
    /// terminal shape after canonicalisation.
    /// </summary>
    /// <returns>
    /// The rewritten body, or <c>null</c> if the input didn't match the
    /// early-return shape (in which case the caller falls through to the
    /// existing terminal-shape gates).
    /// </returns>
    /// <remarks>
    /// Non-goals covered by a <c>null</c> return:
    /// <list type="bullet">
    ///   <item>Body already single-terminal (no canonicalisation needed).</item>
    ///   <item>Leading if has an else — different pattern, existing gate handles it.</item>
    ///   <item>Any leading if branch that doesn't end in a <c>ReturnStatement</c>.</item>
    ///   <item>Tail contains anything other than a terminal <c>ReturnStatement</c>
    ///         or a nested guard-return <c>IfStructure</c> with else.</item>
    ///   <item>Return-in-loop — the caller's for/while walker would have
    ///         rejected the loop task already; loops in the body are
    ///         detected by the outer prefix-vetting gate.</item>
    /// </list>
    /// Mutates the first leading if's Branches; safe because
    /// <see cref="TryInlineUserFunction"/> re-parses the body per call
    /// and doesn't cache back onto <c>FunctionDefinition.SynthesizedBody</c>.
    /// </remarks>
    private static IList<object>? TryCanonicalizeMultiReturn(IList<object> body)
    {
        if (body == null || body.Count < 2) return null;

        // Snapshot leading guard-return ifs. A guard-return if is:
        //   - No else branch.
        //   - Every branch's tasks END in a ReturnStatement.
        var leadingIfs = new List<Interpreter.Repositories.IfStructure>();
        int i = 0;
        while (i < body.Count && body[i] is Interpreter.Repositories.IfStructure ifNode
               && IsGuardReturnIf(ifNode))
        {
            leadingIfs.Add(ifNode);
            i++;
        }
        if (leadingIfs.Count == 0) return null;

        // Tail accepts three shapes:
        //   (a) a terminal ReturnStatement (becomes else with one Return task),
        //   (b) an IfStructure that has an else branch AND every branch ends
        //       in a return (integrate branches directly — its else stays else,
        //       its non-else become elsif).
        //   (c) Pass 22 (2026-07-30): `[Operation*, ReturnStatement]` — the
        //       Ops become the head of the else branch's tasks (pre-pass-20
        //       this would have been rejected). Combined with pass-20's
        //       branch-shape relax (`[Op*, Return]`), this lets shapes like
        //       `if X then return A end if; y := f(); return y+B;` lower.
        if (i >= body.Count) return null;

        var canonicalIf = leadingIfs[0];

        // Fold leadingIfs[1..] as elsif branches.
        for (int k = 1; k < leadingIfs.Count; k++)
        {
            foreach (var br in leadingIfs[k].Branches)
            {
                canonicalIf.Branches.Add(br);
            }
        }

        int remaining = body.Count - i;
        if (remaining == 1 && body[i] is Interpreter.Repositories.IfStructure finalIf)
        {
            if (!HasElseBranch(finalIf) || !AllBranchesEndInReturn(finalIf))
                return null;
            foreach (var br in finalIf.Branches)
            {
                canonicalIf.Branches.Add(br);
            }
        }
        else if (body[body.Count - 1] is Interpreter.Repositories.ReturnStatement finalRet
                 && finalRet.HasExpression && !string.IsNullOrEmpty(finalRet.ExpressionString))
        {
            // Vet all trailing prefix tasks are Operations. Actual local-
            // scope check happens in TryInlineIfElseAsMux's branch shape
            // gate (needs localsByName). Here we just guarantee the shape
            // fits `[Op*, Return]` so the caller's terminal-shape gate
            // sees a lowerable if.
            for (int k = i; k < body.Count - 1; k++)
            {
                if (body[k] is not Operation) return null;
            }
            var elseTasks = new List<object>();
            for (int k = i; k < body.Count; k++) elseTasks.Add(body[k]);
            canonicalIf.Branches.Add(new Interpreter.Repositories.IfBranch
            {
                IsElse = true,
                Tasks  = elseTasks,
            });
        }
        else
        {
            return null;
        }

        return new List<object> { canonicalIf };

        static bool IsGuardReturnIf(Interpreter.Repositories.IfStructure ifs)
            => !HasElseBranch(ifs) && AllBranchesEndInReturn(ifs);

        static bool HasElseBranch(Interpreter.Repositories.IfStructure ifs)
        {
            foreach (var br in ifs.Branches)
                if (br.IsElse) return true;
            return false;
        }

        static bool AllBranchesEndInReturn(Interpreter.Repositories.IfStructure ifs)
        {
            if (ifs.Branches.Count == 0) return false;
            foreach (var br in ifs.Branches)
            {
                if (br.Tasks.Count == 0) return false;
                if (br.Tasks[^1] is not Interpreter.Repositories.ReturnStatement r) return false;
                if (!r.HasExpression || string.IsNullOrEmpty(r.ExpressionString)) return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Tier 4.3 B1 expansion (2026-07-28): fold a function body of shape
    /// <code>
    ///   if c1 then return E1; elsif c2 then return E2; ... else return En; end if;
    /// </code>
    /// into a MUX cascade — one <see cref="CellKind.MUX"/> per non-else branch,
    /// wrapping the running "otherwise" net (mirrors the shape emitted by
    /// <see cref="ConditionalAssignSynthesizer"/>). Requires:
    /// <list type="bullet">
    /// <item>Last branch is <c>else</c>, all preceding branches have non-null Condition.</item>
    /// <item>Every branch holds exactly one <see cref="Interpreter.Repositories.ReturnStatement"/> with a non-empty expression.</item>
    /// </list>
    /// Anything else (multi-task branches, missing else, non-return tasks) falls
    /// back — the caller then emits the opaque-constant + diagnostic.
    /// </summary>
    private static Net? TryInlineIfElseAsMux(
        Interpreter.Repositories.IfStructure ifs,
        HashSet<Variable> bodyScope,
        int retWidth,
        SynthesisContext ctx,
        string? sourceLine,
        IReadOnlyDictionary<string, Variable>? localsByName = null)
    {
        if (ifs.Branches.Count < 2) return null;
        // Last branch must be `else`; every other branch must be conditional.
        // Pass 20 (2026-07-30): branch tasks are now
        // `[Operation*, ReturnStatement]` — the terminal task must be a
        // return; any prefix tasks must be Operation writes to a fn-local
        // variable (same rule as the outer prefix vetting). Locals map
        // may be null when a caller doesn't have one (case: process-side
        // callers); in that case any prefix is rejected.
        for (int i = 0; i < ifs.Branches.Count; i++)
        {
            var br = ifs.Branches[i];
            bool isLast = i == ifs.Branches.Count - 1;
            if (isLast && !br.IsElse) return null;
            if (!isLast && (br.IsElse || br.Condition == null)) return null;
            if (br.Tasks.Count == 0) return null;
            if (br.Tasks[^1] is not Interpreter.Repositories.ReturnStatement r) return null;
            if (!r.HasExpression || string.IsNullOrEmpty(r.ExpressionString)) return null;
            for (int k = 0; k < br.Tasks.Count - 1; k++)
            {
                if (br.Tasks[k] is not Operation prefixOp) return null;
                if (prefixOp.Result is null || string.IsNullOrEmpty(prefixOp.Result.Name)) return null;
                if (localsByName == null || !localsByName.ContainsKey(prefixOp.Result.Name)) return null;
            }
        }

        // Walk in reverse so the else-branch value seeds the running net; each
        // conditional branch wraps in a MUX whose D0 is the running net and D1
        // is that branch's return value.
        Net? current = null;
        for (int i = ifs.Branches.Count - 1; i >= 0; i--)
        {
            var br = ifs.Branches[i];
            var ret = (Interpreter.Repositories.ReturnStatement)br.Tasks[^1];

            // Pass 20 (2026-07-30): per-branch scope frame so local writes
            // stay branch-local. `bodyScope` is the fn-body variable set;
            // `SetAlias` on the pushed frame is picked up when the return
            // expression synthesises. Pop after each branch so sibling
            // branches don't inherit the writes.
            Net valNet;
            bool pushed = br.Tasks.Count > 1;
            if (pushed)
            {
                ctx.PushScope(new Dictionary<string, Net>(StringComparer.OrdinalIgnoreCase));
            }
            try
            {
                for (int k = 0; k < br.Tasks.Count - 1; k++)
                {
                    var prefixOp = (Operation)br.Tasks[k];
                    var local    = localsByName![prefixOp.Result!.Name];
                    int width    = local.Type == DATATYPES.STD_LOGIC_VECTOR
                        ? Math.Max(1, local.Size)
                        : 1;
                    var writeNet = ctx.NewInternal(width, sourceLine);
                    SynthesizeInto(prefixOp, writeNet, ctx);
                    ctx.SetAlias(local.Name, writeNet);
                }
                valNet = ConditionalAssignSynthesizer.SynthesizeExpression(
                    ret.ExpressionString, bodyScope, ctx, retWidth);
            }
            finally
            {
                if (pushed) ctx.PopScope();
            }

            if (br.IsElse)
            {
                current = valNet;
                continue;
            }

            // Condition net. Operation.RawData may include a leading `<lhs> <= `
            // shim from the parse — strip it so the emitted select is the pure
            // boolean expression the user wrote.
            string condExpr = br.Condition!.RawData ?? string.Empty;
            if (string.IsNullOrWhiteSpace(condExpr)) return null;
            int leftArrow = condExpr.IndexOf("<=", StringComparison.Ordinal);
            if (leftArrow > 0) condExpr = condExpr[(leftArrow + 2)..].Trim();

            var selNet = ConditionalAssignSynthesizer.SynthesizeExpression(condExpr, bodyScope, ctx, 1);
            var muxOut = ctx.NewInternal(retWidth, sourceLine);
            ctx.AddCell(
                kind: CellKind.MUX,
                inputs: new[]
                {
                    new Pin("S",  selNet.Id),
                    new Pin("D1", valNet.Id),
                    new Pin("D0", current!.Id),
                },
                outputs: new[] { new Pin("Y", muxOut.Id) },
                sourceLine: sourceLine,
                attrs: new Dictionary<string, string> { ["Cond"] = condExpr });
            current = muxOut;
        }
        return current;
    }

    /// <summary>
    /// Tier 4.3 pass 4 item B (2026-07-28): fold a function body of shape
    /// <code>
    ///   case sel is
    ///     when C1     => return E1;
    ///     when C2     => return E2;
    ///     ...
    ///     when others => return En;
    ///   end case;
    /// </code>
    /// into a MUX cascade — one <see cref="CellKind.MUX"/> per non-others arm,
    /// wrapping a running "otherwise" net (mirrors the shape emitted by
    /// <see cref="ProcessSynthesizer.HandleCase"/> for a single virtual LHS).
    /// Requires an explicit <c>when others</c> arm at the end, and each arm's
    /// tasks to be exactly one <see cref="Interpreter.Repositories.ReturnStatement"/>
    /// with a non-empty expression. Multi-choice arms OR-reduce their match
    /// nets via <see cref="ProcessSynthesizer.BuildChoiceCompare"/>, so VHDL-2008
    /// <c>case?</c> matching (don't-care <c>-</c> bits) works uniformly with
    /// the process-side path.
    /// </summary>
    private static Net? TryInlineCaseAsMux(
        Interpreter.Repositories.CaseStructure cs,
        HashSet<Variable> bodyScope,
        int retWidth,
        SynthesisContext ctx,
        string? sourceLine,
        IReadOnlyDictionary<string, Variable>? localsByName = null)
    {
        if (cs.Selector is null || cs.Branches.Count < 2) return null;
        var othersBranch = cs.Branches.LastOrDefault(b => b.IsOthers);
        if (othersBranch == null) return null;
        // Pass 21 (2026-07-30): arm tasks are now `[Operation*, ReturnStatement]`
        // — mirror of the pass-20 if/else relaxation. Terminal task must
        // be a return with non-empty expression; any prefix tasks are
        // Operation writes to fn-locals.
        foreach (var br in cs.Branches)
        {
            if (br.Tasks.Count == 0) return null;
            if (br.Tasks[^1] is not Interpreter.Repositories.ReturnStatement r) return null;
            if (!r.HasExpression || string.IsNullOrEmpty(r.ExpressionString)) return null;
            for (int k = 0; k < br.Tasks.Count - 1; k++)
            {
                if (br.Tasks[k] is not Operation prefixOp) return null;
                if (prefixOp.Result is null || string.IsNullOrEmpty(prefixOp.Result.Name)) return null;
                if (localsByName == null || !localsByName.ContainsKey(prefixOp.Result.Name)) return null;
            }
        }

        var selNet = NetForVariable(cs.Selector, ctx);
        int selWidth = selNet.Width;

        // Walk in reverse so the others-branch value seeds the running net; each
        // non-others arm wraps in a MUX whose D0 is the running net and D1 is
        // that arm's return value.
        Net? current = null;
        for (int i = cs.Branches.Count - 1; i >= 0; i--)
        {
            var br = cs.Branches[i];
            var ret = (Interpreter.Repositories.ReturnStatement)br.Tasks[^1];

            // Pass 21 (2026-07-30): per-arm scope frame (same pattern as
            // TryInlineIfElseAsMux pass-20 relaxation) so writes stay
            // arm-local.
            Net valNet;
            bool pushed = br.Tasks.Count > 1;
            if (pushed)
            {
                ctx.PushScope(new Dictionary<string, Net>(StringComparer.OrdinalIgnoreCase));
            }
            try
            {
                for (int k = 0; k < br.Tasks.Count - 1; k++)
                {
                    var prefixOp = (Operation)br.Tasks[k];
                    var local    = localsByName![prefixOp.Result!.Name];
                    int width    = local.Type == DATATYPES.STD_LOGIC_VECTOR
                        ? Math.Max(1, local.Size)
                        : 1;
                    var writeNet = ctx.NewInternal(width, sourceLine);
                    SynthesizeInto(prefixOp, writeNet, ctx);
                    ctx.SetAlias(local.Name, writeNet);
                }
                valNet = ConditionalAssignSynthesizer.SynthesizeExpression(
                    ret.ExpressionString, bodyScope, ctx, retWidth);
            }
            finally
            {
                if (pushed) ctx.PopScope();
            }

            if (br.IsOthers)
            {
                current = valNet;
                continue;
            }
            if (current == null) return null; // others must come last

            // OR-reduce match nets across the arm's choice literals.
            Net? matchNet = null;
            foreach (var raw in br.Choices)
            {
                var eqOut = ProcessSynthesizer.BuildChoiceCompare(
                    cs.IsMatching, raw, selNet, selWidth, ctx, sourceLine ?? string.Empty);
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
            if (matchNet == null) return null;

            var muxOut = ctx.NewInternal(retWidth, sourceLine);
            ctx.AddCell(
                kind: CellKind.MUX,
                inputs: new[]
                {
                    new Pin("S",  matchNet.Id),
                    new Pin("D1", valNet.Id),
                    new Pin("D0", current.Id),
                },
                outputs: new[] { new Pin("Y", muxOut.Id) },
                sourceLine: sourceLine);
            current = muxOut;
        }
        return current;
    }

    /// <summary>
    /// Build a fresh HashSet&lt;Variable&gt; suitable for parsing a function
    /// body at synth time. Contains: (1) fresh Variables for each formal
    /// parameter, (2) cloned local variables, and (3) caller-scope references
    /// (constants, signals visible from the callsite — parallel to the runtime
    /// <c>CreateLocalScope</c> shape). Order matters: formals first so parser
    /// lookups by name shadow caller-scope siblings if any.
    /// </summary>
    private static HashSet<Variable> BuildInliningScope(FunctionDefinition fdef, HashSet<Variable> callerScope)
    {
        var scope = new HashSet<Variable>();
        foreach (var p in fdef.Parameters) scope.Add(p.CreateVariable());
        foreach (var loc in fdef.LocalVariables)
        {
            scope.Add(new Variable(loc.Name, loc.Value?.ToString() ?? "", loc.Type)
            {
                Size = loc.Size,
                CustomTypeName = loc.CustomTypeName
            });
        }
        foreach (var cv in callerScope)
        {
            if (!scope.Any(v => v.Name.Equals(cv.Name, StringComparison.OrdinalIgnoreCase)))
                scope.Add(cv);
        }
        return scope;
    }

    /// <summary>
    /// Resolve a raw argument expression string to a synth net. Fast paths for
    /// lone identifiers and single-quoted / double-quoted literals; falls back
    /// to constructing an <see cref="Operation"/> against the caller scope and
    /// synthesising it into a fresh internal net. Returns null when the
    /// expression can't be parsed or synthesised — the caller then aborts the
    /// inlining attempt.
    /// </summary>
    private static Net? ResolveArgExpressionToNet(string argExpr, HashSet<Variable> callerScope,
                                                   SynthesisContext ctx, string? sourceLine)
    {
        argExpr = argExpr.Trim();
        if (string.IsNullOrEmpty(argExpr)) return null;

        // Lone identifier.
        var maybeVar = callerScope.FirstOrDefault(v =>
            v.Name.Equals(argExpr, StringComparison.OrdinalIgnoreCase));
        if (maybeVar is not null) return NetForVariable(maybeVar, ctx);

        // Literal std_logic / std_logic_vector.
        if ((argExpr.StartsWith('\'') && argExpr.EndsWith('\'') && argExpr.Length >= 3) ||
            (argExpr.StartsWith('"')  && argExpr.EndsWith('"')  && argExpr.Length >= 2))
        {
            string clean = argExpr.Trim('\'', '"');
            int width = Math.Max(1, clean.Length);
            return ctx.Constant(clean, width);
        }

        // General expression.
        try
        {
            var argOp = new Operation(argExpr, callerScope);
            var argOut = ctx.NewInternal(1, sourceLine);
            SynthesizeInto(argOp, argOut, ctx);
            return argOut;
        }
        catch { return null; }
    }
}
