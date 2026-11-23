using Parser.Models;

namespace Interpreter.Models;

/// <summary>
/// A run-time error of the simulated design (division by zero, an index outside
/// the declared range, …): the simulation stops with an error, as in GHDL. The
/// evaluator throws it without a position; <see cref="Interpreter.Services.Operation"/>
/// re-throws it with the source line of the statement being executed.
/// </summary>
public sealed class SimulationRuntimeException : Exception, ISourcePosition
{
    /// <inheritdoc/>
    public int Line { get; }

    /// <inheritdoc/>
    public int Column => 0;

    /// <summary>Creates the error with its GHDL-style message and, when known, its 1-based line (and file, G82).</summary>
    public SimulationRuntimeException(string message, int line = 0, string? file = null)
        : base(line <= 0 ? message : file is null ? $"{message} at Line {line}" : $"{message} at Line {line} in {Path.GetFileName(file)}")
    {
        Line = line;
        Reason = message;
        File = file;
    }

    /// <summary>The source file of <see cref="Line"/> when it is not the parsed text itself (an elaborated design's child, G82).</summary>
    public string? File { get; }

    /// <summary>This error located at <paramref name="line"/> of <paramref name="file"/>.</summary>
    public SimulationRuntimeException Relocated(string file, int line) => new(Reason, line, file);

    /// <summary>The message without the position.</summary>
    public string Reason { get; }

    /// <summary>This error located at <paramref name="line"/> (itself when it already has a line or the line is unknown).</summary>
    public SimulationRuntimeException At(int line) => Line > 0 || line <= 0 ? this : new(Reason, line);
}
