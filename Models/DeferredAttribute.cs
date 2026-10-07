using Interpreter.Services;
using Parser.Models;
using static Parser.Models.Constants;

namespace Interpreter.Models;

/// <summary>
/// Tier 4.1 (2026-07-28): Signal-attribute call that defers evaluation until
/// <c>Operation.Execute</c> time, mirroring <see cref="DeferredFunction"/>.
///
/// Before this class existed, <c>Operation.TryParseSignalAttribute</c> read the
/// attribute value ONCE during <c>Syntetize()</c> and baked a plain
/// <c>Variable</c> literal into <c>Items[]</c>. Subsequent Execute() calls
/// reused the frozen value — so raw `clk'event and clk='1'` inside a
/// `wait until` never refreshed, whereas `rising_edge(clk)` worked because
/// it was already wrapped in a <see cref="DeferredFunction"/>. This class
/// closes that gap for the dynamic attributes.
///
/// Scope: `'event`, `'last_value`, `'stable` (both no-arg and `(T)` forms).
/// Static integer-class attributes (`'length`, `'high`, `'low`, `'left`,
/// `'right`) stay inline-evaluated since their value is fixed at declaration.
/// </summary>
public class DeferredAttribute
{
    /// <summary>The signal the attribute is bound to.</summary>
    public Variable Signal { get; }
    /// <summary>Lower-case attribute name — "event" | "last_value" | "stable".</summary>
    public string AttributeName { get; }
    /// <summary>Result data type. BOOLEAN for event/stable; signal.Type for last_value.</summary>
    public DATATYPES ResultType { get; }
    /// <summary>
    /// Time argument for `'stable(T)`, in raw ns. Scaling to the
    /// tick*1000+delta units used by <see cref="SignalHistory"/> and
    /// <see cref="SignalAttributeHandler.OnDeltaCommit"/> happens inside
    /// <see cref="SignalAttributeHandler.GetStable(Variable, long)"/> using
    /// the engine's registered tick resolution. Null for no-arg forms.
    /// </summary>
    public long? TimeArgNs { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="DeferredAttribute"/> class.
    /// </summary>
    /// <param name="signal">The signal the attribute is evaluated against.</param>
    /// <param name="attributeName">The attribute name (e.g. event, last_value, stable).</param>
    /// <param name="resultType">The data type of the attribute result.</param>
    /// <param name="timeArgNs">Optional time argument for 'stable(T), in raw ns; null for no-arg forms.</param>
    public DeferredAttribute(Variable signal, string attributeName, DATATYPES resultType, long? timeArgNs = null)
    {
        Signal = signal;
        AttributeName = attributeName.ToLower();
        ResultType = resultType;
        TimeArgNs = timeArgNs;
    }

    /// <summary>
    /// Evaluates the attribute against the live <see cref="SignalAttributeHandler"/>
    /// state at Execute time.
    /// </summary>
    public Variable Evaluate()
    {
        var h = SignalAttributeHandler.Instance;
        object value = AttributeName switch
        {
            "event"      => h.GetEvent(Signal),
            "last_value" => h.GetLastValue(Signal),
            "stable"     => TimeArgNs is long ns ? h.GetStable(Signal, ns) : h.GetStable(Signal),
            "active"     => h.GetActive(Signal),
            _            => false
        };

        // Mirror DeferredFunction's bench-fix (bug #12): keep bool as bool
        // for BOOLEAN results so downstream comparators / IfStructure's
        // `Result.Value is bool` type check picks it up, but stringify for
        // vector results so Variable.Value's std_logic_vector normalizer
        // gets a familiar shape. Null fallback (unwritten history) uses a
        // width-aware "U" string so downstream std_logic_vector assignments
        // don't receive the "false" literal we used to hand out — that would
        // survive Variable.Value's trim and write garbage into a vector.
        object resultValue = ResultType switch
        {
            DATATYPES.BOOLEAN          => value ?? false,
            DATATYPES.STD_LOGIC_VECTOR => value?.ToString() ?? new string('U', Math.Max(1, Signal.Size)),
            _                          => value?.ToString() ?? "U"
        };

        var resultVar = new Variable(
            $"DeferredAttr-{Signal.Name}'{AttributeName}",
            resultValue,
            ResultType);
        resultVar.PortType = PortType.OUT;

        // For last_value on a vector, propagate the width so downstream ops
        // don't collapse it to a scalar (parallel to the DeferredFunction
        // width fix at Interpreter/Models/DeferredFunction.cs bench bug #12).
        // Use the source signal's declared Size — the raw "U" placeholder
        // is 1 char and would otherwise mis-declare the width to callers.
        if (ResultType == DATATYPES.STD_LOGIC_VECTOR)
            resultVar.Size = Signal.Size;

        return resultVar;
    }
}
