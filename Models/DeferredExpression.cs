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
    private readonly List<object> _postfix;

    /// <summary>Creates a deferred expression over the buffered infix items of a function argument.</summary>
    /// <param name="items">Operands (variables, deferred reads/calls, literals) and operators, in infix order.</param>
    public DeferredExpression(List<object> items)
    {
        _items = items;
        _postfix = new Repositories.ShuntingYard(Services.Operation.WithUnarySigns(
            [.. items.Select(i => i is Services.Operator ? i : Operand(i))])).Result;
    }

    /// <summary>
    /// Read-only view of the buffered infix items (<c>[operand, op, operand, …]</c>), walked by
    /// the synthesizer to emit a cell per operator.
    /// </summary>
    public IReadOnlyList<object> Items => _items;

    /// <summary>
    /// The argument's current value, evaluated with the operators' precedence by the
    /// same evaluator as any expression (<see cref="Services.Operation.EvaluateRpn"/>).
    /// </summary>
    public Variable Evaluate() =>
        _items.Count == 0 ? new Variable("__DExprEmpty", "", DATATYPES.STD_LOGIC_VECTOR)
        : _items.Count == 1 ? Services.Operation.ToArgVariablePublic(_items[0])
        : Services.Operation.EvaluateRpn(_postfix, string.Join(" ", _items));

    /// <summary>An operand the evaluator understands: literals and bare tokens become constants.</summary>
    private static object Operand(object item) => item switch
    {
        int or string => Services.Operation.ToArgVariablePublic(item),
        _ => item,
    };
}
