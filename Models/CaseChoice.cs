namespace Interpreter.Models;

/// <summary>
/// The single comparison of a <c>case</c>/<c>case?</c>/<c>with … select</c> choice against the
/// selector value. Quotes are stripped from both sides. Matching case (<c>case?</c>) treats a
/// <c>-</c> in the choice as "any value" and compares the rest element by element; an
/// expression selector (or <c>with … select</c>) compares numerically when both sides are
/// integers (decimal, bit string or hex); otherwise the comparison is textual and
/// case-insensitive (enumeration literals and identifiers).
/// </summary>
public static class CaseChoice
{
    /// <summary>True when <paramref name="choice"/> selects <paramref name="selector"/>.</summary>
    public static bool Matches(string choice, string selector, bool matching = false, bool numeric = false)
    {
        string a = Clean(choice), b = Clean(selector);
        if (matching) return Wildcard(a, b);
        if (numeric && HdlLiteral.TryParseInt(a, out long ai) && HdlLiteral.TryParseInt(b, out long bi))
            return ai == bi;
        return a.Equals(b, StringComparison.OrdinalIgnoreCase);
    }

    private static string Clean(string value) => value.Replace("\"", "").Replace("'", "").Trim();

    private static bool Wildcard(string choice, string selector)
    {
        if (choice.Length != selector.Length) return false;
        for (int i = 0; i < choice.Length; i++)
            if (choice[i] != '-' && char.ToUpperInvariant(choice[i]) != char.ToUpperInvariant(selector[i])) return false;
        return true;
    }
}
