namespace Interpreter.Services;

/// <summary>
/// The analysis order of a project's sources (G22): the files a caller names
/// keep their order (as on a GHDL command line), and every other file follows
/// sorted by its path relative to the project root (ordinal, <c>/</c>
/// separators) — never the file system's enumeration order, so the
/// "last analysed architecture" is the same on every machine.
/// </summary>
public static class SourceOrder
{
    /// <summary>
    /// Orders <paramref name="files"/>: those listed in <paramref name="preferred"/>
    /// (matched by relative path or file name) first, in that order; the rest by
    /// relative path.
    /// </summary>
    public static List<string> Sort(string root, IEnumerable<string> files, IReadOnlyList<string>? preferred = null)
    {
        var all = files.Distinct().ToList();
        var ordered = new List<string>(all.Count);
        if (preferred != null)
        {
            foreach (var name in preferred)
            {
                string wanted = name.Replace('\\', '/');
                var hit = all.FirstOrDefault(f => !ordered.Contains(f) && Relative(root, f) == wanted)
                       ?? all.FirstOrDefault(f => !ordered.Contains(f) && Path.GetFileName(f) == Path.GetFileName(name));
                if (hit != null) ordered.Add(hit);
            }
        }
        ordered.AddRange(all.Except(ordered).OrderBy(f => Relative(root, f), StringComparer.Ordinal));
        return ordered;
    }

    private static string Relative(string root, string file) =>
        Path.GetRelativePath(root, file).Replace('\\', '/');
}
