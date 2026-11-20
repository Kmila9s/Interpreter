namespace Interpreter.Interfaces;

/// <summary>
/// A concurrent statement whose equivalent process is sensitive to the signals
/// named in its text (simple, conditional and selected signal assignments).
/// </summary>
public interface IConcurrentStatement
{
    /// <summary>
    /// The texts the statement reads: waveform values and delays, conditions,
    /// the selector, the target's index expressions — never the target itself.
    /// Its sensitivity is <see cref="Services.ImplicitSensitivity.SignalsIn"/> of them.
    /// </summary>
    IEnumerable<string> ReadTexts { get; }
}
