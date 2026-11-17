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

    private static bool IsBitString(string s)
    {
        foreach (char c in s)
            if (c is not ('0' or '1')) return false;
        return true;
    }
}
