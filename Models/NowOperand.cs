using Parser.Models;

namespace Interpreter.Models;

/// <summary>
/// VHDL's predefined function <c>now</c>: the current simulation time, read
/// when the expression is evaluated (never folded at analysis).
/// </summary>
public sealed class NowOperand : IDeferredOperand
{
    /// <inheritdoc/>
    public Variable Evaluate() => PhysicalTime.Value(Services.SignalAttributeHandler.Instance.NowFs, "now");

    /// <inheritdoc/>
    public IEnumerable<Variable>? Inputs => null;

    /// <inheritdoc/>
    public int Width => 64;
}
