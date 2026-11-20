using System.Globalization;

namespace Interpreter.Models;

/// <summary>
/// Reads the literal shapes values take at run time: integers written in
/// decimal, as a bit string or in hex, and the <c>(others=&gt;X)</c> aggregate
/// marker the parser emits for <c>(others =&gt; X)</c>. The single reader for
/// index values, <c>case</c>/<c>with … select</c> choices and aggregate fills.
/// </summary>
public static class HdlLiteral
{
    /// <summary>The marker prefix of an <c>(others =&gt; X)</c> aggregate value.</summary>
    public const string OthersMarker = "(others=>";

    /// <summary>
    /// Parses an integer written as text: decimal, or a hex literal (<c>0x…</c>,
    /// <c>x…</c>); false when the text is not numeric. Decimal comes first (G65:
    /// <c>"10"</c> is ten, not the bits <c>1 0</c>); a vector's bits are read by
    /// <see cref="LogicVector.TryToInteger"/>, which knows the value is a vector.
    /// </summary>
    public static bool TryParseInt(string? s, out long value)
    {
        value = 0;
        if (string.IsNullOrEmpty(s)) return false;
        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            return true;
        ReadOnlySpan<char> h = s;
        if (h.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) h = h[2..];
        else if (h.StartsWith("x", StringComparison.OrdinalIgnoreCase)) h = h[1..];
        else return false;
        return long.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// The fill character of an <c>(others=&gt;X)</c> marker (quotes removed;
    /// <c>0</c> when empty); false when <paramref name="text"/> is not one.
    /// </summary>
    public static bool TryOthersFill(string? text, out char fill)
    {
        fill = '0';
        if (text is null || !text.StartsWith(OthersMarker, StringComparison.Ordinal)) return false;
        int start = OthersMarker.Length;
        int close = text.IndexOf(')', start);
        var inner = (close >= 0 ? text.AsSpan(start, close - start) : text.AsSpan(start)).Trim().Trim('\'');
        if (inner.Length > 0) fill = inner[0];
        return true;
    }

    /// <summary>
    /// Bits written by a literal: <c>x"0F"</c> → <c>00001111</c>, <c>o"7"</c> →
    /// <c>111</c>, <c>b"01"</c> / <c>"0101"</c> → the digits, <c>'1'</c> → <c>1</c>.
    /// False for anything else.
    /// </summary>
    public static bool TryBits(string text, out string bits)
    {
        bits = string.Empty;
        if (text.Length >= 3 && text[1] == '"' && text[^1] == '"' && text[0] is 'x' or 'X' or 'o' or 'O' or 'b' or 'B')
        {
            int radix = char.ToLowerInvariant(text[0]) switch { 'x' => 16, 'o' => 8, _ => 2 };
            int width = radix switch { 16 => 4, 8 => 3, _ => 1 };
            var sb = new System.Text.StringBuilder();
            foreach (char c in text[2..^1])
            {
                if (c == '_') continue;
                int d = Uri.IsHexDigit(c) ? Convert.ToInt32(c.ToString(), 16) : -1;
                if (d < 0 || d >= radix) return false;
                sb.Append(Convert.ToString(d, 2).PadLeft(width, '0'));
            }
            bits = sb.ToString();
            return true;
        }
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"') { bits = text[1..^1]; return true; }
        if (text.Length == 3 && text[0] == '\'' && text[2] == '\'') { bits = text[1].ToString(); return true; }
        return false;
    }

    /// <summary>Binary text of hex digits (<c>"0F"</c> → <c>"00001111"</c>); underscores are ignored.</summary>
    public static string HexBits(string hexDigits) =>
        string.Concat(hexDigits.Where(c => c != '_')
            .Select(c => Convert.ToString(Convert.ToInt32(c.ToString(), 16), 2).PadLeft(4, '0')));
}
