using Parser.Models;
using static Parser.Models.Constants;

namespace Interpreter.Services;

/// <summary>
/// Handles VHDL signal attributes like 'event, 'last_value, 'stable, 'active, etc.
/// Signal attributes are used primarily in process sensitivity and edge detection.
/// </summary>
public class SignalAttributeHandler
{
    /// <summary>
    /// Stores the previous values of signals for 'event and 'last_value detection.
    /// </summary>
    private readonly Dictionary<string, object> _previousValues = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Stores the active flag for signals (whether they had an event this delta cycle).
    /// </summary>
    private readonly Dictionary<string, bool> _activeFlags = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Stores the stable flag for signals (whether they have been stable).
    /// </summary>
    private readonly Dictionary<string, bool> _stableFlags = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Singleton instance for global signal tracking.
    /// </summary>
    private static SignalAttributeHandler? _instance;

    /// <summary>
    /// Gets the singleton instance.
    /// </summary>
    public static SignalAttributeHandler Instance => _instance ??= new SignalAttributeHandler();

    /// <summary>
    /// Registers a signal for attribute tracking.
    /// </summary>
    public void RegisterSignal(Variable signal)
    {
        // Always (re)seed the per-signal state. The handler is a singleton,
        // so without this a follow-up simulation in the same process would
        // inherit stale state from the prior run (eg. _previousValues["clk"]
        // = "1") and the next stimulus event would be silently swallowed.
        // Bug #18 fix.
        string key = signal.Name;
        _previousValues[key] = signal.Value;
        _activeFlags[key] = false;
        _stableFlags[key] = true;

        // Subscribe to value changes
        signal.OnValueChange += OnSignalValueChange;
    }

    /// <summary>
    /// Handles signal value change events.
    /// </summary>
    private void OnSignalValueChange(Variable signal)
    {
        string key = signal.Name;
        if (_previousValues.TryGetValue(key, out var prevValue))
        {
            // Signal had an event if value changed
            _activeFlags[key] = !Equals(prevValue, signal.Value);
            _stableFlags[key] = Equals(prevValue, signal.Value);
        }
    }

    /// <summary>
    /// Updates the previous value of a signal (call at end of delta cycle).
    /// </summary>
    public void UpdatePreviousValue(Variable signal)
    {
        _previousValues[signal.Name] = signal.Value;
        _activeFlags[signal.Name] = false;
    }

    /// <summary>
    /// Updates all tracked signals (call at end of simulation step).
    /// </summary>
    public void UpdateAllPreviousValues(HashSet<Variable> signals)
    {
        foreach (var signal in signals)
        {
            UpdatePreviousValue(signal);
        }
    }

    /// <summary>
    /// Gets whether a signal had an event (value change) during current delta cycle.
    /// Implements: signal'event
    /// </summary>
    public bool GetEvent(Variable signal)
    {
        return _activeFlags.TryGetValue(signal.Name, out var active) && active;
    }

    /// <summary>
    /// Gets whether a signal had an event (value change) during current delta cycle.
    /// </summary>
    public bool GetEvent(string signalName)
    {
        return _activeFlags.TryGetValue(signalName, out var active) && active;
    }

    /// <summary>
    /// Gets the previous value of a signal.
    /// Implements: signal'last_value
    /// </summary>
    public object GetLastValue(Variable signal)
    {
        return _previousValues.TryGetValue(signal.Name, out var value) ? value : signal.Value;
    }

    /// <summary>
    /// Gets the previous value of a signal.
    /// </summary>
    public object GetLastValue(string signalName)
    {
        return _previousValues.TryGetValue(signalName, out var value) ? value : "U";
    }

    /// <summary>
    /// Gets whether a signal has been stable (no change).
    /// Implements: signal'stable
    /// </summary>
    public bool GetStable(Variable signal)
    {
        return _stableFlags.TryGetValue(signal.Name, out var stable) && stable;
    }

    /// <summary>
    /// Gets whether a signal is active (had a transaction).
    /// Implements: signal'active
    /// </summary>
    public bool GetActive(Variable signal)
    {
        return _activeFlags.TryGetValue(signal.Name, out var active) && active;
    }

    /// <summary>
    /// Checks for rising edge: signal'event and signal = '1'
    /// </summary>
    public bool IsRisingEdge(Variable signal)
    {
        if (!GetEvent(signal)) return false;
        string currentValue = signal.Value?.ToString()?.Replace("'", "").Replace("\"", "") ?? "";
        return currentValue == "1";
    }

    /// <summary>
    /// Checks for falling edge: signal'event and signal = '0'
    /// </summary>
    public bool IsFallingEdge(Variable signal)
    {
        if (!GetEvent(signal)) return false;
        string currentValue = signal.Value?.ToString()?.Replace("'", "").Replace("\"", "") ?? "";
        return currentValue == "0";
    }

    /// <summary>
    /// Evaluates a signal attribute expression.
    /// </summary>
    /// <param name="signalName">The signal name.</param>
    /// <param name="attributeName">The attribute name (event, last_value, stable, active).</param>
    /// <param name="variables">The variable set to look up the signal.</param>
    /// <returns>The attribute value.</returns>
    public object EvaluateAttribute(string signalName, string attributeName, HashSet<Variable> variables)
    {
        var signal = variables.FirstOrDefault(v => v.Name.Equals(signalName, StringComparison.OrdinalIgnoreCase));
        if (signal == null)
        {
            throw new InvalidOperationException($"Signal '{signalName}' not found for attribute evaluation");
        }

        return attributeName.ToLower() switch
        {
            "event" => GetEvent(signal),
            "last_value" => GetLastValue(signal),
            "stable" => GetStable(signal),
            "active" => GetActive(signal),
            _ => throw new NotSupportedException($"Signal attribute '{attributeName}' is not supported")
        };
    }

    /// <summary>
    /// Parses and evaluates a signal attribute expression like "clk'event".
    /// </summary>
    /// <param name="expression">The expression containing signal attribute.</param>
    /// <param name="variables">The variable set.</param>
    /// <returns>Tuple of (isAttribute, signalName, attributeName, value).</returns>
    public (bool isAttribute, string signalName, string attributeName, object value) ParseAttributeExpression(
        string expression, HashSet<Variable> variables)
    {
        // Check if expression contains attribute syntax
        if (!expression.Contains("'"))
        {
            return (false, "", "", null!);
        }

        // Find the apostrophe position (attribute indicator)
        int apostropheIndex = expression.IndexOf("'");

        // Skip string literals (they start with ')
        if (apostropheIndex == 0)
        {
            return (false, "", "", null!);
        }

        string signalName = expression[..apostropheIndex].Trim();
        string attributeName = expression[(apostropheIndex + 1)..].Trim();

        // Check if this is actually a signal attribute (not a character literal)
        string[] validAttributes = ["event", "last_value", "stable", "active", "left", "right", "high", "low", "length", "range"];

        // Get base attribute name (might have more stuff after it)
        string baseAttribute = attributeName.Split(' ', '(', ')', '=')[0].ToLower();

        if (!validAttributes.Contains(baseAttribute))
        {
            return (false, "", "", null!);
        }

        try
        {
            var value = EvaluateAttribute(signalName, baseAttribute, variables);
            return (true, signalName, baseAttribute, value);
        }
        catch
        {
            return (false, "", "", null!);
        }
    }

    /// <summary>
    /// Resets all tracking data.
    /// </summary>
    public void Reset()
    {
        _previousValues.Clear();
        _activeFlags.Clear();
        _stableFlags.Clear();
    }
}

/// <summary>
/// Extension methods for Variable to easily access signal attributes.
/// </summary>
public static class VariableAttributeExtensions
{
    /// <summary>
    /// Gets whether the signal had an event.
    /// </summary>
    public static bool Event(this Variable signal)
    {
        return SignalAttributeHandler.Instance.GetEvent(signal);
    }

    /// <summary>
    /// Gets the last value of the signal.
    /// </summary>
    public static object LastValue(this Variable signal)
    {
        return SignalAttributeHandler.Instance.GetLastValue(signal);
    }

    /// <summary>
    /// Gets whether the signal is stable.
    /// </summary>
    public static bool Stable(this Variable signal)
    {
        return SignalAttributeHandler.Instance.GetStable(signal);
    }

    /// <summary>
    /// Checks for rising edge on the signal.
    /// </summary>
    public static bool RisingEdge(this Variable signal)
    {
        return SignalAttributeHandler.Instance.IsRisingEdge(signal);
    }

    /// <summary>
    /// Checks for falling edge on the signal.
    /// </summary>
    public static bool FallingEdge(this Variable signal)
    {
        return SignalAttributeHandler.Instance.IsFallingEdge(signal);
    }
}
