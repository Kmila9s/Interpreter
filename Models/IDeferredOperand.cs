using Parser.Models;

namespace Interpreter.Models;

/// <summary>
/// An expression operand evaluated when the expression runs (not when it is
/// parsed). The evaluator, the shunting-yard and the delta engine treat every
/// implementation the same way, so a new operand kind is a new class, not a
/// new branch at each of those sites.
/// </summary>
public interface IDeferredOperand
{
    /// <summary>The operand's value now.</summary>
    Variable Evaluate();

    /// <summary>Signals the value depends on, or null when it may read any signal in scope (re-run every delta).</summary>
    IEnumerable<Variable>? Inputs { get; }

    /// <summary>Width in bits of the value (synthesis nets).</summary>
    int Width { get; }
}
