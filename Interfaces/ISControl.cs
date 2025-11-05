namespace Interpreter.Interfaces;

/// <summary>
/// Defines the contract for a sequential control structure that can be executed and synthesized.
/// </summary>
public interface ISControl {
    /// <summary>
    /// Executes the control structure's logic asynchronously.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task ExecuteAsync();

    /// <summary>
    /// Analyzes and prepares the control structure's behavior, typically using internally available state.
    /// </summary>
    void Syntetize();

    /// <summary>
    /// Analyzes and prepares the control structure's behavior from a given set of raw data lines.
    /// </summary>
    /// <param name="RawData">An array of strings representing the lines of code that define the behavior.</param>
    void Syntetize(string[] RawData);
    
}