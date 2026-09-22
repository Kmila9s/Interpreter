namespace Interpreter.Interfaces;

/// <summary>
/// Defines the contract for control structures that determine execution behavior.
/// </summary>
public interface IControl
{
    /// <summary>
    /// Analyzes and prepares the behavior of the control structure from a given set of code lines.
    /// </summary>
    /// <param name="Behaviour">An array of strings representing the lines of code that define the behavior.</param>
    void SyntetizeBehaviour(string[] Behaviour);

    /// <summary>
    /// Analyzes and prepares the behavior of the control structure, typically using internally available state.
    /// </summary>
    void SyntetizeBehaviour();
}