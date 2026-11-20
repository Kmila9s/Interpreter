namespace Interpreter.Models;

/// <summary>
/// IEEE 1164 nine-valued logic (<c>U X 0 1 Z W L H -</c>): the single source of
/// the truth tables, the strength-stripping conversions and the metavalue
/// predicates. Values are the upper-case characters of the enumeration; every
/// entry point normalises case first, so a stray lower-case <c>u</c>/<c>x</c>/<c>z</c>
/// from older code paths reads as its upper-case value.
/// </summary>
public static class StdLogic
{
    /// <summary>The nine values in declaration order (<c>std_ulogic'left</c> = <c>'U'</c>).</summary>
    public const string Values = "UX01ZWLH-";

    /// <summary>Upper-case form of <paramref name="c"/> (<c>u</c> → <c>U</c>).</summary>
    public static char Norm(char c) => c is >= 'a' and <= 'z' ? (char)(c - 32) : c;

    /// <summary>True when <paramref name="c"/> is one of the nine values (any case).</summary>
    public static bool IsValue(char c) => Values.IndexOf(Norm(c)) >= 0;

    /// <summary>True when every character of <paramref name="s"/> is a std_logic value.</summary>
    public static bool IsVector(string s)
    {
        foreach (char c in s) if (!IsValue(c)) return false;
        return s.Length > 0;
    }

    /// <summary><c>To_X01</c>: <c>0/L</c> → <c>0</c>, <c>1/H</c> → <c>1</c>, anything else → <c>X</c>.</summary>
    public static char ToX01(char c) => Norm(c) switch
    {
        '0' or 'L' => '0',
        '1' or 'H' => '1',
        _ => 'X',
    };

    /// <summary>True for <c>0 1 L H</c> (a logic level, possibly weak).</summary>
    public static bool IsLevel(char c) => ToX01(c) != 'X';

    /// <summary><c>is_x</c> on one value: <c>U X Z W -</c>.</summary>
    public static bool IsX(char c) => !IsLevel(c);

    /// <summary><c>is_x</c> on a vector: any element is <c>U X Z W -</c>.</summary>
    public static bool IsX(string bits)
    {
        foreach (char c in bits) if (IsX(c)) return true;
        return false;
    }

    /// <summary>VHDL-2008 <c>??</c> (the condition operator): <c>To_X01(c) = '1'</c>.</summary>
    public static bool Condition(char c) => ToX01(c) == '1';

    /// <summary>
    /// The IEEE 1164 resolution table (<c>resolved</c>), rows and columns in
    /// <see cref="Values"/> order: <c>U</c> dominates, then <c>X</c>/<c>-</c>, a
    /// forcing <c>0</c>/<c>1</c> beats the weak values, <c>Z</c> yields to anything.
    /// </summary>
    private static readonly string[] ResolutionRows =
    [
        "UUUUUUUUU", // U
        "UXXXXXXXX", // X
        "UX0X0000X", // 0
        "UXX11111X", // 1
        "UX01ZWLHX", // Z
        "UX01WWWWX", // W
        "UX01LWLWX", // L
        "UX01HWWHX", // H
        "UXXXXXXXX", // -
    ];

    /// <summary>
    /// The value of a resolved signal driven by <paramref name="a"/> and
    /// <paramref name="b"/> (<c>0</c>+<c>1</c> → <c>X</c>, <c>Z</c>+<c>1</c> → <c>1</c>,
    /// <c>L</c>+<c>H</c> → <c>W</c>); folding it over every driver gives the
    /// resolution of N drivers (the table is commutative and associative).
    /// A character outside the nine values resolves to itself when both agree, else <c>X</c>.
    /// </summary>
    public static char Resolve(char a, char b)
    {
        int i = Values.IndexOf(Norm(a)), j = Values.IndexOf(Norm(b));
        if (i < 0 || j < 0) return Norm(a) == Norm(b) ? a : 'X';
        return ResolutionRows[i][j];
    }

    /// <summary><c>not</c>.</summary>
    public static char Not(char a) => Norm(a) == 'U' ? 'U' : ToX01(a) switch { '0' => '1', '1' => '0', _ => 'X' };

    /// <summary><c>and</c>: a <c>0</c> wins, then <c>U</c>, then <c>1</c> only if both are 1.</summary>
    public static char And(char a, char b)
    {
        char x = ToX01(a), y = ToX01(b);
        if (x == '0' || y == '0') return '0';
        if (Norm(a) == 'U' || Norm(b) == 'U') return 'U';
        return x == '1' && y == '1' ? '1' : 'X';
    }

    /// <summary><c>or</c>: a <c>1</c> wins, then <c>U</c>, then <c>0</c> only if both are 0.</summary>
    public static char Or(char a, char b)
    {
        char x = ToX01(a), y = ToX01(b);
        if (x == '1' || y == '1') return '1';
        if (Norm(a) == 'U' || Norm(b) == 'U') return 'U';
        return x == '0' && y == '0' ? '0' : 'X';
    }

    /// <summary><c>xor</c>: <c>U</c> wins, then <c>X</c> unless both are levels.</summary>
    public static char Xor(char a, char b)
    {
        if (Norm(a) == 'U' || Norm(b) == 'U') return 'U';
        char x = ToX01(a), y = ToX01(b);
        return x == 'X' || y == 'X' ? 'X' : x == y ? '0' : '1';
    }

    /// <summary>VHDL-2008 matching equality <c>?=</c>: <c>-</c> matches anything.</summary>
    public static char MatchEq(char a, char b)
    {
        if (Norm(a) == '-' || Norm(b) == '-') return '1';
        if (Norm(a) == 'U' || Norm(b) == 'U') return 'U';
        char x = ToX01(a), y = ToX01(b);
        return x == 'X' || y == 'X' ? 'X' : x == y ? '1' : '0';
    }

    /// <summary>Vector <c>?=</c>: the AND of the element matches.</summary>
    public static char MatchEq(string a, string b)
    {
        char acc = '1';
        for (int i = 0; i < a.Length; i++) acc = And(acc, MatchEq(a[i], b[i]));
        return acc;
    }

    /// <summary><c>std_match</c> on one value pair: <c>-</c> or the same level.</summary>
    public static bool Match(char a, char b) =>
        Norm(a) == '-' || Norm(b) == '-' || (IsLevel(a) && ToX01(a) == ToX01(b));

    /// <summary>Binary logical operator by name (<c>and or xor nand nor xnor</c>); null when not one.</summary>
    public static Func<char, char, char>? Binary(string op) => op.ToLowerInvariant() switch
    {
        "and" => And,
        "or" => Or,
        "xor" => Xor,
        "nand" => (a, b) => Not(And(a, b)),
        "nor" => (a, b) => Not(Or(a, b)),
        "xnor" => (a, b) => Not(Xor(a, b)),
        "?=" => MatchEq,
        "?/=" => (a, b) => Not(MatchEq(a, b)),
        _ => null,
    };

    /// <summary>Element-wise <paramref name="op"/> over two equal-width vectors.</summary>
    public static string Vector(Func<char, char, char> op, string a, string b)
    {
        var r = new char[a.Length];
        for (int i = 0; i < r.Length; i++) r[i] = op(a[i], b[i]);
        return new string(r);
    }

    /// <summary>Element-wise <c>not</c>.</summary>
    public static string Not(string a) => string.Create(a.Length, a, static (r, src) =>
    {
        for (int i = 0; i < r.Length; i++) r[i] = Not(src[i]);
    });

    /// <summary>VHDL-2008 unary reduction (<c>and v</c>): left fold of <paramref name="op"/>.</summary>
    public static char Reduce(Func<char, char, char> op, string bits, char identity)
    {
        char acc = identity;
        foreach (char c in bits) acc = op(acc, c);
        return acc;
    }

    /// <summary>
    /// Unsigned value of a bit string whose elements are all levels (<c>0 1 L H</c>, weak levels
    /// read as 0/1); false when an element is a metavalue or the string is empty or wider than 63 bits.
    /// </summary>
    public static bool TryToUnsigned(string bits, out long value)
    {
        value = 0;
        if (bits.Length is 0 or > 63) return false;
        foreach (char c in bits)
        {
            char l = ToX01(c);
            if (l == 'X') return false;
            value = (value << 1) | (uint)(l - '0');
        }
        return true;
    }

    /// <summary>numeric_std <c>To_01</c>: levels to 0/1; any metavalue makes the whole result 0.</summary>
    public static string To01(string bits) =>
        IsX(bits) ? new string('0', bits.Length) : string.Create(bits.Length, bits, static (r, src) =>
        {
            for (int i = 0; i < r.Length; i++) r[i] = ToX01(src[i]);
        });
}
