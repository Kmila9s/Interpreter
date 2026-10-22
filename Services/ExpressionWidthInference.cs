using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Parser.Models;
using static Parser.Models.Constants.DATATYPES;

namespace Interpreter.Services;

/// <summary>
/// Conservative, string-based bit-width inference for the right-hand side of a
/// signal assignment, used by the C4/C5 type/width validation
/// (<see cref="Operation.ValidateAssignmentTypeWidth"/>).
/// </summary>
/// <remarks>
/// It returns a width ONLY when that width is PROVABLY certain, and null (→ no
/// check) for every form whose width is not statically known (generic/expression
/// ranges, non-literal slice bounds, arithmetic, type conversions, function calls,
/// aggregates, parenthesised sub-expressions). This keeps the width check free of
/// false positives on valid VHDL — the overriding project constraint. It works on
/// the raw source RHS, where the Interpreter re-joins tokens with spaces (e.g.
/// <c>a ( 22 downto 0 )</c>), so the patterns tolerate inner whitespace.
/// </remarks>
internal static class ExpressionWidthInference
{
    // name ( HI downto LO )  /  name ( LO to HI )  — integer-literal bounds only.
    private static readonly Regex LiteralSlice = new(
        @"^[A-Za-z_]\w*\s*\(\s*(\d+)\s+(?:downto|to)\s+(\d+)\s*\)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // name ( N )  — single integer-literal index.
    private static readonly Regex LiteralIndex = new(
        @"^[A-Za-z_]\w*\s*\(\s*\d+\s*\)$", RegexOptions.Compiled);
    // bare identifier.
    private static readonly Regex Identifier = new(
        @"^[A-Za-z_]\w*$", RegexOptions.Compiled);

    /// <summary>
    /// True when <paramref name="rhs"/> contains a '&amp;' concatenation operator at
    /// the top level (paren-depth 0, outside string/char literals). A concatenation
    /// is always an array, so this is used to reject one assigned to a scalar target.
    /// </summary>
    public static bool HasTopLevelConcat(string rhs) => SplitTopLevelConcat(rhs).Count > 1;

    /// <summary>
    /// The certain bit-width of <paramref name="rhs"/>, or null when it cannot be
    /// determined with certainty (in which case no width check should run).
    /// </summary>
    public static int? TryComputeWidth(string rhs, HashSet<Variable> vars)
    {
        var segments = SplitTopLevelConcat(rhs);
        if (segments.Count == 0) return null;

        int total = 0;
        foreach (var segment in segments)
        {
            int? w = TryAtomWidth(segment.Trim(), vars);
            if (w is null) return null; // any uncertain part ⇒ the whole width is uncertain
            total += w.Value;
        }
        return total;
    }

    /// <summary>Splits on top-level '&amp;', respecting parens and string/char literals.</summary>
    private static List<string> SplitTopLevelConcat(string rhs)
    {
        var parts = new List<string>();
        if (string.IsNullOrWhiteSpace(rhs)) return parts;

        int depth = 0, start = 0;
        char quote = '\0';
        for (int i = 0; i < rhs.Length; i++)
        {
            char c = rhs[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') { quote = c; continue; }
            if (c == '(') depth++;
            else if (c == ')') { if (depth > 0) depth--; }
            else if (c == '&' && depth == 0)
            {
                parts.Add(rhs[start..i]);
                start = i + 1;
            }
        }
        parts.Add(rhs[start..]);
        return parts;
    }

    /// <summary>Certain width of a single (non-concatenated) operand, or null.</summary>
    private static int? TryAtomWidth(string atom, HashSet<Variable> vars)
    {
        if (atom.Length == 0) return null;

        // Char literal 'c' → a single std_logic value (1 bit).
        if (atom.Length == 3 && atom[0] == '\'' && atom[^1] == '\'') return 1;

        // Bit-string "01UXZWLH-…" → bit count ('_' are separators).
        if (atom.Length >= 2 && atom[0] == '"' && atom[^1] == '"')
        {
            string bits = atom[1..^1].Replace("_", string.Empty);
            if (bits.Length > 0 && bits.All(ch => "01UXZWLH-".IndexOf(char.ToUpperInvariant(ch)) >= 0))
                return bits.Length;
            return null;
        }

        // Based literals: x"…" (×4), o"…" (×3), b"…" (×1).
        if (atom.Length >= 3 && atom[^1] == '"' && atom[1] == '"')
        {
            char baseChar = char.ToLowerInvariant(atom[0]);
            string digits = atom[2..^1].Replace("_", string.Empty);
            if (digits.Length == 0) return null;
            return baseChar switch
            {
                'x' when digits.All(Uri.IsHexDigit) => digits.Length * 4,
                'o' when digits.All(d => d is >= '0' and <= '7') => digits.Length * 3,
                'b' when digits.All(d => d is '0' or '1') => digits.Length,
                _ => (int?)null,
            };
        }

        // Literal-bounded slice name(HI downto LO) / name(LO to HI). The literal
        // bounds give a certain width regardless of name's declared range — BUT only
        // when name is a plain 1-D bit vector; on an array-of-vectors a `(…)` indexes
        // a whole element, not bits.
        var m = LiteralSlice.Match(atom);
        if (m.Success)
        {
            if (!IsSimpleBitVector(Lookup(SliceBaseName(atom), vars))) return null;
            int a = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            int b = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            return Math.Abs(a - b) + 1;
        }

        // Single literal index name(N) → 1 bit, but ONLY on a plain 1-D bit vector
        // (on an array-of-vectors `name(N)` is a whole element → unknown here).
        if (LiteralIndex.IsMatch(atom))
            return IsSimpleBitVector(Lookup(SliceBaseName(atom), vars)) ? 1 : (int?)null;

        // Bare identifier: scalar → 1; 1-D vector with a RELIABLE declared range → Size;
        // otherwise unknown (generic/expression width, or array-of-vectors).
        if (Identifier.IsMatch(atom))
        {
            var v = Lookup(atom, vars);
            if (v is null) return null;
            if (v.Type == STD_LOGIC || v.Type == BIT) return 1;
            if (v.Type == STD_LOGIC_VECTOR && v.HasDeclaredRange && v.Size > 0 && v.ElementSize <= 0)
                return v.Size;
            return null;
        }

        // Function call, arithmetic, conversion, non-literal bounds, aggregate,
        // parenthesised sub-expression, integer, … → width not certain.
        return null;
    }

    /// <summary>Case-insensitive signal/variable lookup by name.</summary>
    private static Variable? Lookup(string name, HashSet<Variable> vars) =>
        vars?.FirstOrDefault(x =>
            x is not null && !string.IsNullOrEmpty(x.Name) &&
            x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The identifier before the first '(' in an indexed/sliced atom.</summary>
    private static string SliceBaseName(string atom)
    {
        int p = atom.IndexOf('(');
        return (p > 0 ? atom[..p] : atom).Trim();
    }

    /// <summary>
    /// True only for a plain 1-D bit vector (std_logic_vector / bit_vector), where a
    /// `(N)` index is a single bit and a `(HI downto LO)` slice is `|HI-LO|+1` bits.
    /// Excludes array-of-vectors (ElementSize &gt; 0), scalars, custom types and
    /// unknown names — on those, an index/slice width is NOT certain.
    /// </summary>
    private static bool IsSimpleBitVector(Variable? v) =>
        v is not null && v.Type == STD_LOGIC_VECTOR && v.ElementSize <= 0;
}
