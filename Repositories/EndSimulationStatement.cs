using Interpreter.Interfaces;
using Interpreter.Models;

namespace Interpreter.Repositories;

/// <summary>
/// <c>std.env.stop</c> / <c>std.env.finish</c> (VHDL-2008 §16.5): ends the simulation
/// successfully at the current time.
/// </summary>
/// <param name="procedure"><c>stop</c> or <c>finish</c> (for the end message).</param>
public sealed class EndSimulationStatement(string procedure) : ISControl
{
    /// <summary>The called procedure (<c>stop</c> or <c>finish</c>).</summary>
    public string Procedure { get; } = procedure;

    /// <summary>Nothing to read: the call was parsed by <see cref="SimpleStatement"/>.</summary>
    public void Syntetize() { }

    /// <inheritdoc/>
    public Task ExecuteAsync() =>
        throw new SimulationEndException(false, Procedure == "stop" ? "simulation stopped" : "simulation finished");
}
