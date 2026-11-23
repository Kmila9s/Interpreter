using Interpreter.Models;
using Parser.Models;

namespace Interpreter.Services;

/// <summary>
/// Where each line of an elaborated (flattened) text comes from (G82). The
/// elaborator writes a marker comment <c>--@@ N "file"</c> (no identifier, so renames never touch it) before every piece
/// it copies from a source (an architecture's declarations or statements, the
/// statements after an inlined instance, the port connections of an instance);
/// the lines after a marker are lines N, N+1, … of that file. Errors located in
/// the flattened text are moved back to the file and line the user wrote.
/// </summary>
public sealed class ElaborationLineMap
{
    private const string Prefix = "--@@ ";

    private readonly (string File, int Line)?[] _origins;

    private ElaborationLineMap((string File, int Line)?[] origins) => _origins = origins;

    /// <summary>The marker that makes the following lines lines <paramref name="line"/>… of <paramref name="file"/>.</summary>
    public static string Marker(string file, int line) => $"{Prefix}{line} \"{file}\"";

    /// <summary>The map of <paramref name="text"/>; null when it has no markers (not an elaborated text).</summary>
    public static ElaborationLineMap? Parse(string? text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains(Prefix, StringComparison.Ordinal)) return null;
        var lines = text.Split('\n');
        var origins = new (string File, int Line)?[lines.Length];
        string? file = null;
        int next = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.StartsWith(Prefix, StringComparison.Ordinal) && TryReadMarker(line, out var markerFile, out int markerLine))
            {
                (file, next) = (markerFile, markerLine);
                continue;
            }
            if (file is not null) origins[i] = (file, next++);
        }
        return new ElaborationLineMap(origins);
    }

    private static bool TryReadMarker(string line, out string file, out int number)
    {
        file = string.Empty;
        number = 0;
        int space = line.IndexOf(' ', Prefix.Length);
        if (space < 0 || !int.TryParse(line.AsSpan(Prefix.Length, space - Prefix.Length), out number)) return false;
        file = line[(space + 1)..].Trim().Trim('"');
        return file.Length > 0;
    }

    /// <summary>The source file and line of 1-based line <paramref name="flatLine"/>; null when unknown.</summary>
    public (string File, int Line)? Origin(int flatLine) =>
        flatLine >= 1 && flatLine <= _origins.Length ? _origins[flatLine - 1] : null;

    /// <summary><paramref name="error"/> moved to its source position (itself when the line is unknown).</summary>
    public ParserException Relocate(ParserException error) =>
        error.File is null && Origin(error.Line) is var (file, line) ? error.Relocated(file, line) : error;

    /// <summary><paramref name="error"/> moved to its source position (itself when the line is unknown).</summary>
    public SimulationRuntimeException Relocate(SimulationRuntimeException error) =>
        error.File is null && Origin(error.Line) is var (file, line) ? error.Relocated(file, line) : error;

    /// <summary><paramref name="diagnostic"/> moved to its source position (itself when the line is unknown).</summary>
    public Diagnostic Relocate(Diagnostic diagnostic) =>
        diagnostic.File is null && Origin(diagnostic.Line) is var (file, line)
            ? new Diagnostic
            {
                Severity = diagnostic.Severity, Line = line, File = file, Code = diagnostic.Code, Message = diagnostic.Message,
                StartColumn = diagnostic.StartColumn, EndColumn = diagnostic.EndColumn,
            }
            : diagnostic;
}
