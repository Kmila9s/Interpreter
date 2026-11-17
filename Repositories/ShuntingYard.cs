using Interpreter.Services;
using Parser.Models;

namespace Interpreter.Repositories;

/// <summary>
/// Implements the Shunting-yard algorithm to convert infix notation to postfix notation.
/// </summary>
public class ShuntingYard {
    /// <summary>Stack used to hold operators during the infix-to-postfix conversion.</summary>
    private Stack<Operator> _operators = new();
    /// <summary>The original list of items (operands and operators) in infix notation.</summary>
    private readonly List<object> _items = new();

    /// <summary>
    /// Gets the result of the Shunting-yard algorithm in postfix notation.
    /// </summary>
    public List<object> Result { get; private set; } = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ShuntingYard"/> class.
    /// </summary>
    /// <param name="BaseItems">The list of items in infix notation.</param>
    public ShuntingYard(List<object> BaseItems) { 
        _items = BaseItems; 
        OrderPrecedenceList();
    }

    /// <summary>
    /// Orders the precedence of the operators in the list.
    /// </summary>
    private void OrderPrecedenceList(){
        foreach (var item in _items){
            // Operands (variables, deferred functions, and deferred indexed
            // reads) go directly to output. v0.17.0 added DeferredFunction
            // (for rising_edge / falling_edge); bench Phase F bug #5 sub-cause D
            // added DeferredIndex (for `signal(N)` RHS reads that must be
            // re-evaluated each delta against live signal values).
            // 2026-07-20 added DeferredUserFunction (user-declared VHDL
            // functions whose body is registered — need re-evaluation on
            // every Operation.Execute so calls inside clocked processes
            // don't freeze at first-fire values).
            // Tier 4.1 (2026-07-28): DeferredAttribute for signal'event /
            // 'last_value / 'stable[(T)] — same lifecycle as DeferredFunction:
            // pass through as an operand, re-evaluated at Execute time.
            if(item is Variable || item is Models.DeferredFunction || item is Models.DeferredIndex || item is Models.DynamicIndex || item is Models.DeferredUserFunction || item is Models.DeferredAttribute || item is Models.IDeferredOperand) Result.Add(item);
            else if(item is Operator operation ){
                // Left parenthesis: push onto operator stack
                if(operation.OperatorName == "(")
                    _operators.Push(operation);
                // Right parenthesis: pop operators to output until matching '(' is found
                else if(operation.OperatorName == ")"){
                    while(
                        _operators.Count > 0 &&
                        _operators.Peek().OperatorName != "("
                    )
                        Result.Add(_operators.Pop());
                    if(_operators.Count > 0 && _operators.Peek().OperatorName == "(")
                        _operators.Pop(); // Discard the "("
                // Regular operator: pop higher-or-equal precedence operators to output first
                }else{
                    // v0.13.0 Fix: Use Convert.ToInt32() instead of direct cast for enum precedence
                    // The Precedence property is typed as 'object' and can be either:
                    // - PrecedencesLogicOperators (for AND, OR, NOT, etc.)
                    // - PrecedencesArithOperators (for +, -, *, /, **, mod)
                    // Direct casting (int) fails when the object contains an enum value
                    //
                    // v3.4 Fix: never pop a `(` from the operator stack while
                    // pushing a regular operator. Only the matching `)` should
                    // pop a `(`. Without this guard, any operator whose
                    // precedence value is ≥ PARENTHESIS (4) — `&`, `=`, `<`,
                    // `<=` (signal-assign), etc. — flushed the enclosing `(`
                    // straight to the output, leaving stray `(` Operators in
                    // the postfix that later tried to Pop two operands and
                    // produced the classic "Stack empty" runtime error.
                    while(
                        _operators.Count > 0 &&
                        _operators.Peek().OperatorName != "(" &&
                        Convert.ToInt32(_operators.Peek().Precedence) <= Convert.ToInt32(operation.Precedence)
                    )
                        Result.Add(_operators.Pop());
                    _operators.Push(operation);
                }
            }
        }
        // Flush remaining operators from the stack to the output
        while (_operators.Count > 0)
            Result.Add(_operators.Pop());
    }
}