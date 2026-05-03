using Interpreter.Repositories;
using Interpreter.Services;
using Parser.Models;

namespace Interpreter.Synthesis;

/// <summary>
/// Lowers a VHDL <c>when/else</c> conditional assignment into a cascade
/// of MUX cells. The textbook example
/// <code>y &lt;= a when sel = "00" else b when sel = "01" else c;</code>
/// becomes <c>y = MUX(sel="00", a, MUX(sel="01", b, c))</c>, with each
/// condition expression first synthesised into its own gate tree whose
/// output drives the MUX selector.
/// </summary>
internal static class ConditionalAssignSynthesizer
{
    public static void Synthesize(ConditionalAssignment ca, HashSet<Variable> scope, SynthesisContext ctx)
    {
        // `Target` is a Variable; Variable defines `==` overloads that
        // mishandle two-sided null. Use `is null` to bypass the
        // overload. (Without this, `ca.Target == null` returns false
        // even when Target is null, leading to NREs downstream.)
        if (ca.Target is null || ca.Branches.Count == 0) return;

        // Resolve LHS net.
        var targetVar = ca.Target;
        int targetWidth = targetVar.Type == Constants.DATATYPES.STD_LOGIC_VECTOR
            ? Math.Max(1, targetVar.Size) : 1;
        var outNet = ctx.GetOrCreateNet(
            ca.TargetName,
            targetWidth > 1 ? NetKind.Bus : NetKind.Wire,
            targetWidth,
            targetVar.IsPort ? NetOrigin.PortOut : NetOrigin.Signal);
        // Source-line key: a single rebuilt VHDL-like string covering
        // the whole when/else cascade. Used by the Naive view to group
        // every MUX in the cascade visually.
        string sourceLine = $"{ca.TargetName} <= " + string.Join(" else ", ca.Branches.Select(b =>
            b.IsDefault ? b.ValueExpression : $"{b.ValueExpression} when {b.ConditionExpression}"));

        // Walk branches in reverse so the default sits at the bottom of the
        // chain. Each iteration wraps the running net in a new MUX whose
        // 'else' input is the prior net.
        Net current = ctx.Constant("U", targetWidth); // dangling default if no else branch
        for (int i = ca.Branches.Count - 1; i >= 0; i--)
        {
            var branch = ca.Branches[i];
            var valueNet = SynthesizeExpression(branch.ValueExpression, scope, ctx, targetWidth);

            if (branch.IsDefault || string.IsNullOrEmpty(branch.ConditionExpression))
            {
                current = valueNet;
                continue;
            }

            var selNet = SynthesizeExpression(branch.ConditionExpression!, scope, ctx, 1);
            var muxOut = ctx.NewInternal(width: targetWidth, sourceLine: sourceLine);
            ctx.AddCell(
                kind: CellKind.MUX,
                inputs: new[]
                {
                    new Pin("S", selNet.Id),
                    new Pin("D1", valueNet.Id), // when condition true
                    new Pin("D0", current.Id),  // else
                },
                outputs: new[] { new Pin("Y", muxOut.Id) },
                sourceLine: sourceLine,
                attrs: new Dictionary<string, string> { ["Cond"] = branch.ConditionExpression! });
            current = muxOut;
        }

        // Drive the output through a BUF so the named LHS appears as a wire
        // in the diagram even if the cascade collapsed to a single net.
        ctx.AddCell(
            kind: CellKind.BUF,
            inputs: new[] { new Pin("A", current.Id) },
            outputs: new[] { new Pin("Y", outNet.Id) },
            sourceLine: sourceLine);
    }

    /// <summary>
    /// Wraps an arbitrary VHDL expression string in an <see cref="Operation"/>
    /// just long enough to extract its postfix items, then synthesises that
    /// into the net graph. The temporary Operation isn't kept; only its
    /// gate tree leaves a trace.
    /// </summary>
    public static Net SynthesizeExpression(string expr, HashSet<Variable> scope, SynthesisContext ctx, int targetWidth)
    {
        // Wrap as a fake assignment so Operation parses the RHS into Items.
        // The synthetic LHS variable is a stable temp; the resulting net is
        // an internal one so concurrent re-synthesis doesn't collide.
        var temp = new Variable($"_expr_{Guid.NewGuid():N}", "0",
            targetWidth > 1 ? Constants.DATATYPES.STD_LOGIC_VECTOR : Constants.DATATYPES.STD_LOGIC,
            targetWidth);
        temp.PortType = Constants.PortType.OUT;
        var localScope = new HashSet<Variable>(scope) { temp };
        var op = new Operation($"{temp.Name} <= {expr}", localScope);

        var outNet = ctx.NewInternal(width: targetWidth);
        ConcurrentAssignSynthesizer.SynthesizeInto(op, outNet, ctx);
        return outNet;
    }
}
