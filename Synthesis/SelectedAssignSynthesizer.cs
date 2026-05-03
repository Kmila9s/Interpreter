using Interpreter.Repositories;
using Parser.Models;

namespace Interpreter.Synthesis;

/// <summary>
/// Lowers a VHDL <c>with selector select</c> assignment into a decoder +
/// MUX-cascade. Each choice produces an EQ comparator (selector against
/// the literal choice value); the comparator outputs feed a chain of
/// MUX selectors so the first match wins. <c>others</c> drops in as the
/// fall-through input.
/// </summary>
internal static class SelectedAssignSynthesizer
{
    public static void Synthesize(SelectedAssignment sa, HashSet<Variable> scope, SynthesisContext ctx)
    {
        // See ConditionalAssignSynthesizer — Variable's `==` overloads
        // need to be sidestepped via pattern matching.
        if (sa.Target is null || sa.Choices.Count == 0) return;

        var target = sa.Target;
        int width = target.Type == Constants.DATATYPES.STD_LOGIC_VECTOR
            ? Math.Max(1, target.Size) : 1;
        var outNet = ctx.GetOrCreateNet(
            sa.TargetName,
            width > 1 ? NetKind.Bus : NetKind.Wire,
            width,
            target.IsPort ? NetOrigin.PortOut : NetOrigin.Signal);
        // Source-line key for Naive view: the full with/select line.
        string sourceLine = $"with {sa.SelectorExpression} select " + sa.TargetName + " <= "
            + string.Join(", ", sa.Choices.Select(c => c.IsOthers
                ? $"{c.ValueExpression} when others"
                : $"{c.ValueExpression} when {string.Join("|", c.Choices)}"));

        // Selector net synthesised as a sub-expression.
        var selNet = ConditionalAssignSynthesizer.SynthesizeExpression(
            sa.SelectorExpression, scope, ctx, targetWidth: 0);

        // Build chain in reverse so 'others' sits at the bottom.
        Net current = ctx.Constant("U", width);
        for (int i = sa.Choices.Count - 1; i >= 0; i--)
        {
            var ch = sa.Choices[i];
            var valueNet = ConditionalAssignSynthesizer.SynthesizeExpression(
                ch.ValueExpression, scope, ctx, width);

            if (ch.IsOthers)
            {
                current = valueNet;
                continue;
            }

            // Multi-choice rows like `when "00" | "01"` OR the comparators
            // together so a single MUX gates the same value for any match.
            Net? matchNet = null;
            foreach (var raw in ch.Choices)
            {
                var literalNet = ctx.Constant(raw, selNet.Width);
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

            var muxOut = ctx.NewInternal(width: width, sourceLine: sourceLine);
            ctx.AddCell(
                kind: CellKind.MUX,
                inputs: new[]
                {
                    new Pin("S", matchNet.Id),
                    new Pin("D1", valueNet.Id),
                    new Pin("D0", current.Id),
                },
                outputs: new[] { new Pin("Y", muxOut.Id) },
                sourceLine: sourceLine);
            current = muxOut;
        }

        ctx.AddCell(
            kind: CellKind.BUF,
            inputs: new[] { new Pin("A", current.Id) },
            outputs: new[] { new Pin("Y", outNet.Id) },
            sourceLine: sourceLine);
    }
}
