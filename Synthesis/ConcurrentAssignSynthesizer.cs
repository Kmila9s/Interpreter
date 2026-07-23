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
        var resultNet = NetForVariable(op.Result, ctx);
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
    /// Resolves a parsed <see cref="Variable"/> to its corresponding net.
    /// Literals get a CONST net (with the literal value embedded in the
    /// name so the renderer can stamp it on the wire). Indexed accesses
    /// like <c>a(3)</c> get their own net so the schematic shows the
    /// slice as a distinct wire — Phase 5's bus-explode toggle reads
    /// the BusBit attribute to decide whether to pinch the bus open.
    /// </summary>
    private static Net NetForVariable(Variable v, SynthesisContext ctx)
    {
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
}
