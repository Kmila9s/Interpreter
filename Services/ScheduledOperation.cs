using Parser.Models;
using static Parser.Models.Constants;

namespace Interpreter.Services;

/// <summary>
/// Wraps an Operation to use signal scheduling instead of immediate assignment.
/// This enables proper VHDL delta cycle semantics where signal assignments are scheduled.
/// </summary>
public class ScheduledOperation
{
    private readonly Operation _operation;
    private readonly SignalScheduler _scheduler;

    /// <summary>
    /// Gets the underlying operation.
    /// </summary>
    public Operation UnderlyingOperation => _operation;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScheduledOperation"/> class.
    /// </summary>
    /// <param name="operation">The operation to wrap.</param>
    /// <param name="scheduler">The signal scheduler to use for updates.</param>
    public ScheduledOperation(Operation operation, SignalScheduler scheduler)
    {
        _operation = operation;
        _scheduler = scheduler;
    }

    /// <summary>
    /// Executes the operation but schedules the result instead of assigning immediately.
    /// This implements proper VHDL delta cycle semantics.
    /// </summary>
    public void Execute()
    {
        // Check if the result target is an input port (can't assign to inputs)
        // v0.17.0 Fix: Only check PortType for actual ports, not internal signals
        // Internal signals have IsPort=false but PortType defaults to IN
        if (_operation.Result.IsPort && _operation.Result.PortType == PortType.IN)
        {
            throw new InvalidOperationException($"Input ports are not eligible for assignments: {_operation.Result.Name}");
        }

        // Evaluate the expression using the operation's items
        Stack<Variable> variables = new();

        foreach (var item in _operation.Items)
        {
            if (item is Variable newVar)
            {
                variables.Push(newVar);
            }
            // v0.17.0: Handle deferred function evaluation at execution time
            else if (item is Models.DeferredFunction deferredFunc)
            {
                var result = deferredFunc.Evaluate();
                variables.Push(result);
            }
            // Bench bug #5 sub-cause D: re-read indexed vector bits at
            // expression-evaluation time so `signal(N)` reflects the live
            // signal value, not the value at parse time.
            else if (item is Models.DeferredIndex deferredIdx)
            {
                variables.Push(deferredIdx.Evaluate());
            }
            else if (item is Models.DynamicIndex dynIdx)
            {
                variables.Push(dynIdx.Evaluate());
            }
            else if (item is Operator op)
            {
                // Only treat as unary NOT if it's actually a logic operation with NOT precedence
                bool isUnaryNot = op.OperatorType == OperatorType.LOGIC_OPERATION &&
                                  op.Precedence is Models.PrecedencesLogicOperators &&
                                  (Models.PrecedencesLogicOperators)op.Precedence == Models.PrecedencesLogicOperators.NOT;

                if (isUnaryNot)
                {
                    var value = variables.Pop();
                    variables.Push(new("Temp", op.Execute(value), value.Type));
                }
                else
                {
                    var right = variables.Pop();
                    var left = variables.Pop();
                    Variable vari = new("Temp", op.Execute(left, right), left.Type);
                    if (op.OperatorType == OperatorType.COMPARATION)
                    {
                        vari.IsPort = false;
                        vari.Type = DATATYPES.BOOLEAN;
                        vari.Value = bool.Parse((string)vari.Value);
                    }
                    else if (string.Equals(op.OperatorName, "&", StringComparison.Ordinal))
                    {
                        // Mirror Operation.Execute: `&` returns a bit string
                        // regardless of operand types — retag the wrapper.
                        vari.Type = DATATYPES.STD_LOGIC_VECTOR;
                        if (vari.Value is string concat) vari.Size = concat.Length;
                    }
                    variables.Push(vari);
                }
            }
        }

        if (variables.Count != 1)
        {
            throw new InvalidOperationException($"Malformed expression: stack not reduced to a single result");
        }

        // Get the computed value
        object computedValue = variables.Pop().Value;

        // Indexed-target assignment (`output(0) <= …`): route through the
        // scheduler's per-bit channel so 8 sibling per-bit writes merge
        // instead of clobbering each other on apply. Bench bug #5 fix.
        if (_operation.ResultBitIndex is int bitIdx)
        {
            string newBit = (computedValue?.ToString() ?? "0").TrimStart('\'').TrimEnd('\'').Trim();
            char bitChar = newBit.Length > 0 ? newBit[^1] : '0';
            _scheduler.ScheduleBitWrite(_operation.Result, bitIdx, bitChar, _operation.SourceLine);
            return;
        }

        // `after N ns` inertial delay: route through the future-tick queue
        // so the assignment lands at `currentTick + ceil(delay/tickResNs)`
        // instead of the current delta. Bench bug #8 fix.
        if (_operation.AfterDelayNs > 0)
        {
            _scheduler.ScheduleAfterNs(_operation.Result, computedValue, _operation.AfterDelayNs, _operation.SourceLine);
            return;
        }

        // VHDL variable assignment (`:=`) — apply immediately so the next
        // statement (especially the next iteration of an enclosing
        // for-loop) reads the updated value. Scheduling these would leave
        // `count := count + 1` reading stale `count` forever.
        // Bug #14 follow-up.
        if (_operation.IsVariableAssignment)
        {
            _operation.Result.Value = computedValue;
            return;
        }

        // Schedule the update instead of applying immediately. The source
        // line propagates so the delta-cycle engine can tag the change with
        // the exact statement that produced it — Phase B+ Deep AST plumbing.
        _scheduler.Schedule(_operation.Result, computedValue, _operation.SourceLine);
    }

    /// <summary>
    /// Creates a ScheduledOperation from an Operation and SignalScheduler.
    /// </summary>
    /// <param name="operation">The operation to wrap.</param>
    /// <param name="scheduler">The signal scheduler.</param>
    /// <returns>A new ScheduledOperation instance.</returns>
    public static ScheduledOperation Create(Operation operation, SignalScheduler scheduler)
    {
        return new ScheduledOperation(operation, scheduler);
    }
}
