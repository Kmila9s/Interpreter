using System.Runtime.CompilerServices;
using Parser.Models;

namespace Interpreter.Models;

/// <summary>
/// The files of <c>std.textio</c> a design opens for reading
/// (<c>file f : text open read_mode is "data.txt";</c>): each file object's
/// lines, read when first used, relative to <see cref="BaseDirectory"/> (the
/// project folder, where GHDL's working directory would be).
/// </summary>
public static class TextioFiles
{
    private sealed class OpenFile
    {
        public required string Path;
        public string[]? Lines;
        public int Next;
    }

    private static readonly ConditionalWeakTable<Variable, OpenFile> Files = new();

    /// <summary>The folder relative file names are opened from (the project; the current directory when unset).</summary>
    public static string? BaseDirectory { get; set; }

    /// <summary>Declares <paramref name="file"/> opened for reading on <paramref name="path"/>.</summary>
    public static void OpenRead(Variable file, string path) =>
        Files.AddOrUpdate(file, new OpenFile { Path = path });

    private static OpenFile Opened(Variable file, int line)
    {
        if (!Files.TryGetValue(file, out var f))
            throw new SimulationRuntimeException($"file \"{file.Name}\" is not open", line);
        if (f.Lines is null)
        {
            string full = System.IO.Path.IsPathRooted(f.Path) ? f.Path
                : System.IO.Path.Combine(BaseDirectory ?? Directory.GetCurrentDirectory(), f.Path);
            if (!File.Exists(full)) throw new SimulationRuntimeException($"cannot open file \"{f.Path}\"", line);
            f.Lines = File.ReadAllLines(full);
        }
        return f;
    }

    /// <summary><c>endfile(f)</c>.</summary>
    public static bool EndOfFile(Variable file) => Opened(file, 0) is var f && f.Next >= f.Lines!.Length;

    /// <summary><c>readline(f, l)</c>: the next line's text.</summary>
    public static string ReadLine(Variable file, int line)
    {
        var f = Opened(file, line);
        if (f.Next >= f.Lines!.Length) throw new SimulationRuntimeException("file \"" + file.Name + "\": end of file reached", line);
        return f.Lines[f.Next++];
    }

    /// <summary>
    /// <c>read(l, v)</c>: removes the next value for <paramref name="target"/>'s type
    /// from <paramref name="text"/> (leading blanks skipped) and returns it — an
    /// integer, a vector of the target's length, one std_logic/bit/character, a boolean.
    /// </summary>
    public static object Read(ref string text, Variable target, int line)
    {
        string rest = text.TrimStart();
        object value;
        int used;
        switch (target.Type)
        {
            case Constants.DATATYPES.STD_LOGIC_VECTOR:
                used = Math.Max(1, target.Size);
                if (rest.Length < used || !StdLogic.IsVector(rest[..used])) throw ReadError(target, line);
                value = rest[..used].ToUpperInvariant();
                break;
            case Constants.DATATYPES.STD_LOGIC or Constants.DATATYPES.BIT or Constants.DATATYPES.CHARACTER:
                if (rest.Length == 0) throw ReadError(target, line);
                used = 1;
                value = rest[..1];
                break;
            case Constants.DATATYPES.BOOLEAN:
                used = rest.TakeWhile(char.IsLetter).Count();
                if (!bool.TryParse(rest[..used], out bool b)) throw ReadError(target, line);
                value = b;
                break;
            default:
                used = rest.Length > 0 && rest[0] is '-' or '+' ? 1 : 0;
                used += rest.Skip(used).TakeWhile(char.IsDigit).Count();
                if (!int.TryParse(rest[..used], out int n)) throw ReadError(target, line);
                value = n;
                break;
        }
        text = rest[used..];
        return value;
    }

    private static SimulationRuntimeException ReadError(Variable target, int line) =>
        new($"read: no value of the type of \"{target.Name}\" in the line", line);
}
