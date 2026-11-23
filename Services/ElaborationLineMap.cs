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

    /// <summary>The marker that makes every following line line <paramref name="line"/> of <paramref name="file"/> (generated lines of one statement).</summary>
    public static string Pinned(string file, int line) => $"{Prefix}={line} \"{file}\"";

    /// <summary>The marker after which lines have no known origin (text that is no longer the file's, e.g. an unrolled generate).</summary>
    public static string Unknown => $"{Prefix}?";

    /// <summary>The map of <paramref name="text"/>; null when it has no markers (not an elaborated text).</summary>
    public static ElaborationLineMap? Parse(string? text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains(Prefix, StringComparison.Ordinal)) return null;
        var lines = text.Split('\n');
        var origins = new (string File, int Line)?[lines.Length];
        string? file = null;
        int next = 0;
        bool pinned = false;
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].AsSpan().Trim();
            if (line.StartsWith(Prefix, StringComparison.Ordinal))
            {
                file = TryReadMarker(line[Prefix.Length..], out var markerFile, out next, out pinned) ? markerFile : null;
                continue;
            }
            if (file is not null) origins[i] = (file, pinned ? next : next++);
        }
        return new ElaborationLineMap(origins);
    }

    /// <summary>Reads <c>N "file"</c> or <c>=N "file"</c> (pinned); false for <c>?</c> (no origin) or anything else.</summary>
    private static bool TryReadMarker(ReadOnlySpan<char> marker, out string file, out int number, out bool pinned)
    {
        file = string.Empty;
        number = 0;
        pinned = marker.StartsWith("=");
        if (pinned) marker = marker[1..];
        int space = marker.IndexOf(' ');
        if (space < 0 || !int.TryParse(marker[..space], out number)) return false;
        file = marker[(space + 1)..].Trim().Trim('"').ToString();
        return file.Length > 0;
    }

    /// <summary>The source file and line of 1-based line <paramref name="flatLine"/>; null when unknown.</summary>
    public (string File, int Line)? Origin(int flatLine) =>
        flatLine >= 1 && flatLine <= _origins.Length ? _origins[flatLine - 1] : null;

    /// <summary><paramref name="error"/> moved to its source position (itself when the line is unknown).</summary>
    public T Relocate<T>(T error) where T : IRelocatable<T> =>
        error.File is null && Origin(error.Line) is var (file, line) ? error.Relocated(file, line) : error;

    /// <summary>Any failure moved to its source position when it carries one (parse and run-time errors); others unchanged.</summary>
    public Exception Locate(Exception error) => error switch
    {
        SimulationRuntimeException runtime => Relocate(runtime),
        ParserException parse => Relocate(parse),
        _ => error,
    };

    /// <summary><paramref name="diagnostic"/> moved to its source position (itself when the line is unknown).</summary>
    public Diagnostic Relocate(Diagnostic diagnostic) =>
        diagnostic.File is null && Origin(diagnostic.Line) is var (file, line) ? diagnostic with { Line = line, File = file } : diagnostic;
}
