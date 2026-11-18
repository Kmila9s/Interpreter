using Interpreter.Models;
using Interpreter.Services;

namespace Interpreter.Repositories;

/// <summary>One pass over a loop body on the plain (engine-less) execution path.</summary>
internal static class LoopPass
{
    /// <summary>
    /// Runs the body once. Returns false when an <c>exit</c> for this loop ended it;
    /// a <c>next</c> for this loop ends the pass early; exit/next for an outer loop propagate.
    /// </summary>
    public static async Task<bool> RunAsync(List<object> tasks, string? label)
    {
        try { await new Executer(tasks).ExecuteAsync(); }
        catch (ExitException ex) when (ex.Targets(label)) { return false; }
        catch (NextException ex) when (ex.Targets(label)) { }
        return true;
    }
}
