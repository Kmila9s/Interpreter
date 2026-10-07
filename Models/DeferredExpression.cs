using Parser.Models;
using static Parser.Models.Constants;

namespace Interpreter.Models;

/// <summary>
/// Represents a sub-expression that appears inside a function-call
/// argument list and must be re-evaluated live on every tick.
/// </summary>
/// <remarks>
/// <para>
/// Motivating case (bench bug found 2026-07-05 via DIP-adder Visual
/// sample): the RHS <c>sum &lt;= std_logic_vector(unsigned(a) +
/// unsigned(b))</c>. Historically <see cref="Services.Operation.ParseFunctionCall"/>
/// treated each argument as a single resolved value — a sub-expression
/// like <c>unsigned(a) + unsigned(b)</c> either got flattened into three
/// pseudo-args <c>[a-val, "+", b-val]</c> (which the outer primitive
/// misinterpreted) or, once folded at parse time, produced a frozen
/// constant that never updated when <c>a</c>/<c>b</c> changed.
/// </para>
/// <para>
/// A <c>DeferredExpression</c> wraps the buffered item list — a mix of
/// <see cref="Variable"/> (live-referencing source signals),
/// <see cref="DeferredFunction"/> (nested live-evaluated IEEE calls),
/// <see cref="DeferredIndex"/> / <see cref="DynamicIndex"/> (slice /
/// runtime-indexed reads), <c>int</c> literals, and
/// <see cref="Services.Operator"/> nodes for the binary/unary combinators
/// between them. On every <see cref="Evaluate"/> call the operands are
/// re-resolved (so the current tick's values propagate) and the operator
/// chain is folded left-to-right.
/// </para>
/// <para>
/// Left-to-right associativity is intentional: it correctly handles all
/// single-operator sub-expressions (the common <c>f(x + y)</c> shape) and
/// left-associative chains (<c>f(a + b + c)</c>). Mixed-precedence
/// sub-expressions (<c>a + b * c</c>) still need the intermediate-signal
/// escape hatch — matching the way SimulatorBench 021-adder is written.
/// </para>
/// </remarks>
public class DeferredExpression
{
    private readonly List<object> _items;

    /// <summary>Creates a deferred expression buffering the given RPN items.</summary>
    /// <param name="items">The buffered expression items to defer.</param>
    public DeferredExpression(List<object> items)
    {
        _items = items;
    }

    /// <summary>
    /// Read-only view of the buffered items. Exposed so the synthesizer
    /// can walk the sub-expression at build time and emit cells for each
    /// operator, mirroring what <see cref="Evaluate"/> does at runtime.
    /// The layout is infix: <c>[operand, op, operand, op, operand, …]</c>.
    /// </summary>
    public IReadOnlyList<object> Items => _items;

    /// <summary>
    /// Re-fold the buffered items using the current live values of any
    /// Variables / DeferredFunctions they reference. Returns a fresh
    /// Variable representing the sub-expression's current value.
    /// </summary>
    public Variable Evaluate()
    {
        if (_items.Count == 0) return new Variable("__DExprEmpty", "", DATATYPES.STD_LOGIC_VECTOR);
        if (_items.Count == 1) return Services.Operation.ToArgVariablePublic(_items[0]);

        // Sanity-check shape: [operand, op, operand, op, operand, …]. If
        // it doesn't alternate, fall back to items[0] resolved live —
        // mirrors the fallback in Operation.FoldArgExpression so we never
        // crash on malformed input.
        for (int i = 0; i < _items.Count; i++)
        {
            bool shouldBeOp = (i % 2) == 1;
            bool isOp = _items[i] is Services.Operator;
            if (shouldBeOp != isOp) return Services.Operation.ToArgVariablePublic(_items[0]);
        }

        Variable acc = Services.Operation.ToArgVariablePublic(_items[0]);
        for (int i = 1; i + 1 < _items.Count; i += 2)
        {
            var op = (Services.Operator)_items[i];
            var right = Services.Operation.ToArgVariablePublic(_items[i + 1]);
            object result = op.Execute(acc, right);
            var vType = acc.Type;
            if (string.Equals(op.OperatorName, "&", StringComparison.Ordinal))
                vType = DATATYPES.STD_LOGIC_VECTOR;
            var next = new Variable("__DExprFold", result, vType)
            {
                PortType = PortType.OUT,
            };
            if (vType == DATATYPES.STD_LOGIC_VECTOR && result is string s)
                next.Size = s.Length;
            acc = next;
        }
        return acc;
    }
}
