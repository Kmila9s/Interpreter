using System.Text;
using System.Text.RegularExpressions;
using Interpreter.Models;
using Parser.Models;

namespace Interpreter.Services;

/// <summary>
/// Elaborates the <c>for … generate</c> / <c>if … generate</c> statements of an
/// architecture body that hold component instances or local declarations (G57),
/// as GHDL's elaboration does: one copy of the body per iteration with the
/// parameter replaced by its value, local signals/constants/types and statement
/// labels renamed <c>{label}__{i}__name</c> (moved to the architecture's
/// declarative part), and an <c>if</c> generate kept or dropped by its folded
/// condition. Each iteration therefore gets its own instance copies and signals.
/// A generate with neither (or whose range/condition cannot be folded, or whose
/// parameter is shadowed) is left as written for <see cref="Repositories.GenerateBlock"/>.
/// </summary>
internal static class GenerateUnroller
{
    /// <summary>The unrolled declarative part and body, and the local names it added (already prefixed).</summary>
    public sealed record Result(string Decl, string Body, List<string> NewLocals);

    private static readonly Regex HeadRe = new(@"(?<label>\b[A-Za-z]\w*)\s*:\s*(?<kind>for|if|case)\b", RegexOptions.IgnoreCase);
    /// <summary>A branch boundary of an if generate (<c>elsif C generate</c>, <c>else generate</c>), outside literals and nesting.</summary>
    private static readonly Regex IfBranchRe = new(@"\b(?:elsif\b(?<cond>[^;]*?)|else\s*)\bgenerate\b", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    /// <summary>An alternative of a case generate: <c>[label :] when choices =&gt;</c>.</summary>
    private static readonly Regex CaseAlternativeRe = new(@"(?:\b[A-Za-z]\w*\s*:\s*)?\bwhen\b(?<choices>[^;]*?)=>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex GenerateWordRe = new(@"\b(?:end\s+generate|generate)\b", RegexOptions.IgnoreCase);
    private static readonly Regex ForHeaderRe = new(@"^\s*(?<k>\w+)\s+in\s+(?<a>.+?)\s+(?<dir>to|downto)\s+(?<b>.+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex StatementLabelRe = new(@"(?:^|;|\bbegin\b|\bgenerate\b)\s*(?<l>[A-Za-z]\w*)\s*:(?!=)", RegexOptions.IgnoreCase | RegexOptions.Multiline);
    private static readonly Regex TypeNameRe = new(@"\b(?:sub)?type\s+(?<n>\w+)\s+is\b", RegexOptions.IgnoreCase);
    private static readonly Regex NestingRe = new(
        @"\b(process|block|function|procedure|generate|begin|end)\b(?:[ \t]+(process|block|function|procedure|generate|if|case|loop|record|units|component)\b)?",
        RegexOptions.IgnoreCase);

    /// <summary>True when <paramref name="body"/> has a generate that this pass would unroll.</summary>
    public static bool NeedsUnroll(string body) =>
        Generates(body).Any(g => ShouldUnroll(g) || HasBranches(g));

    /// <summary>Unrolls the generates of <paramref name="body"/>; names in ranges and conditions resolve through <paramref name="lookup"/>.</summary>
    public static Result Unroll(string decl, string body, Func<string, string?> lookup)
    {
        var declSb = new StringBuilder(decl);
        var locals = new List<string>();
        string unrolled = UnrollRegion(body, lookup, declSb, locals);
        return new Result(declSb.ToString(), unrolled, locals);
    }

    /// <summary>One generate statement: its span, label, kind, header, body and the body split at its own <c>begin</c>.</summary>
    private sealed record Generate(int Start, int End, string Label, bool IsFor, string Header, string Inner, bool IsCase = false)
    {
        private (string Decl, string Statements)? _split;
        public (string Decl, string Statements) Split => _split ??= SplitDecl(Inner);
    }

    private static readonly Regex BranchOpenerRe = new(@"^\s*(?:else|elsif)\b", RegexOptions.IgnoreCase);

    /// <summary>The top-level generate statements of <paramref name="text"/>, in order.</summary>
    private static IEnumerable<Generate> Generates(string text)
    {
        string masked = VhdlText.MaskStringLiterals(text);
        int cursor = 0;
        foreach (Match head in HeadRe.Matches(masked))
        {
            if (head.Index < cursor) continue;
            var gen = GenerateWordRe.Match(masked, head.Index + head.Length);
            if (!gen.Success || gen.Value.StartsWith("end", StringComparison.OrdinalIgnoreCase)) continue;
            string header = masked[(head.Index + head.Length)..gen.Index];
            // A labelled loop (`lp: for … loop`) or if statement (`chk: if … then`) is no generate.
            if (header.Contains(';') || Regex.IsMatch(header, @"\b(loop|then)\b", RegexOptions.IgnoreCase)) continue;

            int depth = 1, innerStart = gen.Index + gen.Length;
            var w = gen.NextMatch();
            for (int previous = innerStart; w.Success; previous = w.Index + w.Length, w = w.NextMatch())
            {
                if (w.Value.StartsWith("end", StringComparison.OrdinalIgnoreCase)) depth--;
                // `else generate` / `elsif c generate` continue the same if generate.
                else if (!BranchOpenerRe.IsMatch(masked[Math.Max(previous, masked.LastIndexOf(';', w.Index) + 1)..w.Index])) depth++;
                if (depth == 0) break;
            }
            if (!w.Success) yield break;
            int semi = masked.IndexOf(';', w.Index);
            if (semi < 0) yield break;
            cursor = semi + 1;
            yield return new Generate(head.Index, cursor, head.Groups["label"].Value,
                head.Groups["kind"].Value.Equals("for", StringComparison.OrdinalIgnoreCase),
                text[(head.Index + head.Length)..gen.Index], text[innerStart..w.Index],
                head.Groups["kind"].Value.Equals("case", StringComparison.OrdinalIgnoreCase));
        }
    }

    private static bool ShouldUnroll(Generate g) =>
        VhdlUnitText.HasComponentInstance(g.Inner) || g.Split.Decl.Trim().Length > 0;

    /// <summary>Splits a generate body at its own <c>begin</c> (declarative part, statements).</summary>
    private static (string Decl, string Statements) SplitDecl(string inner)
    {
        string masked = VhdlText.MaskStringLiterals(inner);
        int depth = 0;
        foreach (Match m in NestingRe.Matches(masked))
        {
            string kw = m.Groups[1].Value.ToLowerInvariant();
            if (kw == "begin")
            {
                if (depth == 0) return (inner[..m.Index], inner[(m.Index + m.Length)..]);
            }
            else if (kw == "end")
            {
                string closer = m.Groups[2].Value.ToLowerInvariant();
                if (closer is "" or "process" or "block" or "function" or "procedure" or "generate") depth--;
            }
            else depth++;
        }
        return (string.Empty, inner);
    }

    private static string UnrollRegion(string text, Func<string, string?> lookup, StringBuilder declSb, List<string> locals)
    {
        var sb = new StringBuilder();
        int cursor = 0;
        foreach (var found in Generates(text))
        {
            var g = found;
            sb.Append(text, cursor, g.Start - cursor);
            cursor = g.End;
            string original = text[g.Start..g.End];
            string? parameter;
            List<long> iterations;
            if (HasBranches(g))
            {
                // if … elsif … else generate / case … generate (G80): elaborated to the one
                // branch whose condition or choice holds, which is then an if generate.
                if (!TrySelectBranch(g, lookup, out string? branch)) { sb.Append(original); continue; }
                g = g with { Inner = branch ?? string.Empty, IsCase = false };
                (parameter, iterations) = (null, branch is null ? [] : [0]);
            }
            else if (!ShouldUnroll(g) || !TryIterations(g, lookup, out parameter, out iterations))
            {
                sb.Append(original);
                continue;
            }
            var (decl, statements) = g.Split;
            // Renamed per iteration: the local declarations and the statement labels.
            var names = VhdlUnitText.DeclaredNames(decl)
                .Concat(TypeNameRe.Matches(decl).Select(m => m.Groups["n"].Value.ToLowerInvariant())).ToList();
            var labels = OwnLabels(statements).ToList();
            foreach (var value in iterations)
            {
                string suffix = g.IsFor ? $"({value})" : "";
                string prefix = g.IsFor ? $"{g.Label}__{(value < 0 ? "m" + (-value) : value.ToString())}__" : $"{g.Label}__";
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (parameter != null) map[parameter] = value < 0 ? $"({value})" : value.ToString();
                foreach (var name in names)
                {
                    map[name] = prefix + name;
                    locals.Add(prefix + name);
                }
                foreach (var label in labels)
                    map.TryAdd(label, prefix + label);

                if (decl.Trim().Length > 0)
                {
                    declSb.AppendLine();
                    declSb.AppendLine($"  -- ↓ declarations of generate {g.Label}{suffix}");
                    declSb.Append(VhdlUnitText.RenameTokens(decl, map));
                }
                string copy = VhdlUnitText.RenameTokens(statements, map);
                sb.AppendLine();
                sb.AppendLine($"  -- ↓ generate {g.Label}{suffix}");
                sb.Append(UnrollRegion(copy, lookup, declSb, locals));
                sb.AppendLine();
            }
        }
        sb.Append(text, cursor, text.Length - cursor);
        return sb.ToString();
    }

    /// <summary>
    /// Statement labels of <paramref name="statements"/> outside nested generate
    /// bodies (a nested generate's own label is included; the labels inside it are
    /// renamed when that generate is unrolled).
    /// </summary>
    private static IEnumerable<string> OwnLabels(string statements)
    {
        var nested = Generates(statements).ToList();
        foreach (Match lm in StatementLabelRe.Matches(VhdlText.MaskStringLiterals(statements)))
        {
            int at = lm.Groups["l"].Index;
            if (!nested.Any(g => at > g.Start && at < g.End)) yield return lm.Groups["l"].Value;
        }
    }

    /// <summary>True for a case generate, or an if generate with <c>elsif</c>/<c>else</c> branches.</summary>
    private static bool HasBranches(Generate g) =>
        g.IsCase || !g.IsFor && TopLevel(IfBranchRe, g.Inner).Any();

    /// <summary>Matches of <paramref name="re"/> in <paramref name="inner"/> outside its nested generates.</summary>
    private static IEnumerable<Match> TopLevel(Regex re, string inner)
    {
        var nested = Generates(inner).ToList();
        return re.Matches(VhdlText.MaskStringLiterals(inner)).Where(m => !nested.Any(n => m.Index >= n.Start && m.Index < n.End));
    }

    /// <summary>
    /// The statements of the branch an if/case generate elaborates to (null when no
    /// condition holds and there is no <c>else</c>/<c>others</c>); false when a
    /// condition or the case expression cannot be folded.
    /// </summary>
    private static bool TrySelectBranch(Generate g, Func<string, string?> lookup, out string? branch)
    {
        branch = null;
        if (g.IsCase)
        {
            if (!StaticExpression.TryEvaluate(g.Header, lookup, out long selector)) return false;
            var alternatives = TopLevel(CaseAlternativeRe, g.Inner).ToList();
            for (int i = 0; i < alternatives.Count; i++)
            {
                int start = alternatives[i].Index + alternatives[i].Length;
                int end = i + 1 < alternatives.Count ? alternatives[i + 1].Index : g.Inner.Length;
                foreach (var choice in alternatives[i].Groups["choices"].Value.Split('|'))
                {
                    string c = choice.Trim();
                    long v = 0;
                    bool others = c.Equals("others", StringComparison.OrdinalIgnoreCase);
                    if (!others && !StaticExpression.TryEvaluate(c, lookup, out v)) return false;
                    if (others || v == selector) { branch = g.Inner[start..end]; return true; }
                }
            }
            return true;
        }
        var boundaries = TopLevel(IfBranchRe, g.Inner).ToList();
        string? condition = g.Header;
        for (int i = 0; i <= boundaries.Count; i++)
        {
            int start = i == 0 ? 0 : boundaries[i - 1].Index + boundaries[i - 1].Length;
            int end = i < boundaries.Count ? boundaries[i].Index : g.Inner.Length;
            bool holds = true;
            if (condition is not null)
            {
                if (!StaticExpression.TryEvaluate(condition, lookup, out long c)) return false;
                holds = c != 0;
            }
            if (holds) { branch = g.Inner[start..end]; return true; }
            condition = i < boundaries.Count && boundaries[i].Groups["cond"].Success ? boundaries[i].Groups["cond"].Value : null;
        }
        return true;
    }

    /// <summary>
    /// The parameter values of a for generate (range order), or one/zero
    /// iterations of an if generate; false when the range or condition cannot be
    /// folded, the if generate has <c>elsif</c>/<c>else</c> branches, or the
    /// parameter is shadowed inside the body.
    /// </summary>
    private static bool TryIterations(Generate g, Func<string, string?> lookup, out string? parameter, out List<long> values)
    {
        values = [];
        parameter = null;
        if (!g.IsFor)
        {
            if (!StaticExpression.TryEvaluate(g.Header, lookup, out long cond)) return false;
            if (cond != 0) values.Add(0);
            return true;
        }
        var h = ForHeaderRe.Match(g.Header);
        if (!h.Success) return false;
        string k = parameter = h.Groups["k"].Value;
        if (Regex.IsMatch(g.Inner, $@"\bfor\s+{Regex.Escape(k)}\s+in\b|\b(?:signal|constant|variable)\s+{Regex.Escape(k)}\b", RegexOptions.IgnoreCase))
            return false;
        if (!StaticExpression.TryEvaluate(h.Groups["a"].Value, lookup, out long a)
            || !StaticExpression.TryEvaluate(h.Groups["b"].Value, lookup, out long b))
            return false;
        bool down = h.Groups["dir"].Value.Equals("downto", StringComparison.OrdinalIgnoreCase);
        if (Math.Abs(b - a) > 4096) return false;
        for (long i = a; down ? i >= b : i <= b; i += down ? -1 : 1) values.Add(i);
        return true;
    }
}
