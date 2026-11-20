using Parser.Models;

namespace Interpreter.Models;

/// <summary>
/// The single reading of a condition result (<c>if</c>, <c>elsif</c>, <c>while</c>,
/// <c>wait until</c>, <c>exit/next … when</c>, <c>assert</c>, <c>when … else</c>).
/// A boolean is itself; a std_ulogic value goes through the VHDL-2008 condition
/// operator <c>??</c> (<c>'1'</c>/<c>'H'</c> are true); anything else is false.
/// </summary>
public static class Truth
{
    /// <summary>True when <paramref name="result"/> (a bool, a char, a string or a <see cref="Variable"/> wrapping one) holds.</summary>
    public static bool Of(object? result) => result switch
    {
        bool b => b,
        Variable v => Of(v.Value),
        char c => StdLogic.Condition(c),
        string s => OfText(s),
        _ => false,
    };

    private static bool OfText(string s)
    {
        if (bool.TryParse(s, out bool b)) return b;
        string t = s.Length == 3 && s[0] == '\'' && s[2] == '\'' ? s[1..2] : s;
        return t.Length == 1 && StdLogic.Condition(t[0]);
    }
}
