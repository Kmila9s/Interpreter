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
    /// Parses a bit string (all <c>0</c>/<c>1</c>, read as binary), a decimal
    /// integer or a hex literal (<c>0x…</c>, <c>x…</c>); false when the text is
    /// not numeric.
    /// </summary>
    public static bool TryParseInt(string? s, out long value)
    {
        value = 0;
        if (string.IsNullOrEmpty(s)) return false;
        if (s.Length <= 63 && IsBitString(s))
        {
            foreach (char c in s) value = (value << 1) | (uint)(c - '0');
            return true;
        }
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
    /// Bits written by a literal: <c>x"0F"</c> → <c>00001111</c>, <c>"0101"</c> →
    /// <c>0101</c>, <c>'1'</c> → <c>1</c>. False for anything else.
    /// </summary>
    public static bool TryBits(string text, out string bits)
    {
        bits = string.Empty;
        if (text.Length >= 3 && text[0] is 'x' or 'X' && text[1] == '"' && text[^1] == '"')
        {
            try { bits = HexBits(text[2..^1]); return true; }
            catch (FormatException) { return false; }
            catch (ArgumentException) { return false; }
        }
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"') { bits = text[1..^1]; return true; }
        if (text.Length == 3 && text[0] == '\'' && text[2] == '\'') { bits = text[1].ToString(); return true; }
        return false;
    }

    /// <summary>Binary text of hex digits (<c>"0F"</c> → <c>"00001111"</c>); underscores are ignored.</summary>
    public static string HexBits(string hexDigits) =>
        string.Concat(hexDigits.Where(c => c != '_')
            .Select(c => Convert.ToString(Convert.ToInt32(c.ToString(), 16), 2).PadLeft(4, '0')));

    private static bool IsBitString(string s)
    {
        foreach (char c in s)
            if (c is not ('0' or '1')) return false;
        return true;
    }
}
