using System.Text;
using System.Text.RegularExpressions;
using Parser.Models;

namespace Interpreter.Services;

/// <summary>
/// Text helpers over VHDL design units shared by <see cref="DesignLibrary"/>,
/// <see cref="SourceElaborator"/> and <see cref="GenerateUnroller"/>: port/generic
/// clauses, the architecture's declarative part and body, declared names,
/// component instances and whole-word renaming.
/// </summary>
internal static class VhdlUnitText
{
    // PortStartRe/GenericStartRe locate the start of a `port (` or `generic (`
    // clause; the matching `)` is found by counting parens (ExtractParenBlockAfter).
    internal static readonly Regex PortStartRe = new(@"\bport\s*\(", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    internal static readonly Regex GenericStartRe = new(@"\bgeneric\s*\(", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    internal static readonly Regex PortMapStartRe = new(@"\bport\s+map\s*\(", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    internal static readonly Regex GenericMapStartRe = new(@"\bgeneric\s+map\s*\(", RegexOptions.IgnoreCase | RegexOptions.Singleline);

    // Match a signal/constant declaration at line start OR after a `;`, so
    // several on one line (`signal s1 : ...; signal c1 : ...; signal c2 : ...;`)
    // are all collected — otherwise only the first is prefixed when inlining an
    // instance, and the rest collide across repeated instances (duplicate c1).
    private static readonly Regex SignalDeclRe = new(
        @"(?:^|;)\s*(signal|constant)\s+(?<names>[\w,\s]+?)\s*:",
        RegexOptions.IgnoreCase | RegexOptions.Multiline);

    /// <summary>Names (lowercased) of the signals and constants a declarative part declares.</summary>
    internal static List<string> DeclaredNames(string decl)
    {
        var names = new List<string>();
        foreach (Match sd in SignalDeclRe.Matches(decl))
            foreach (var nm in sd.Groups["names"].Value.Split(','))
            {
                var t = nm.Trim();
                if (!string.IsNullOrEmpty(t)) names.Add(t.ToLowerInvariant());
            }
        // Subprograms too: each inlined instance gets its own copy, bound to its
        // own signals (G67) instead of every instance sharing the last one.
        foreach (Match sp in SubprogramDeclRe.Matches(decl))
        {
            string name = sp.Groups["name"].Value.ToLowerInvariant();
            if (!names.Contains(name)) names.Add(name);
        }
        return names;
    }

    private static readonly Regex SubprogramDeclRe = new(
        @"\b(?:pure\s+|impure\s+)?(?:function|procedure)\s+(?<name>[A-Za-z]\w*)",
        RegexOptions.IgnoreCase);

    // Splits the body of a `port(...)` or `generic(...)` block into
    // individual identifier names. Each clause looks like
    //   `name1, name2 : direction type := default; name3 : ...`
    // and we want just the identifiers on the left of every colon.
    internal static List<string> SplitPortNames(string raw)
    {
        var names = new List<string>();
        foreach (var clause in SplitBalanced(raw, ';'))
        {
            int colon = clause.IndexOf(':');
            if (colon < 0) continue;
            var left = clause.Substring(0, colon);
            foreach (var n in left.Split(','))
            {
                var t = n.Trim();
                if (!string.IsNullOrEmpty(t)) names.Add(t);
            }
        }
        return names;
    }

    /// <summary>
    /// Walks the architecture starting at <paramref name="startIdx"/> (just
    /// past the architecture's `is`) and locates the architecture-level
    /// `begin` and the matching final `end ...;`. Inner constructs that
    /// also use begin/end (function bodies, processes, if-statements,
    /// case-statements, generate / block / loop blocks) are tracked by
    /// depth so we don't terminate on them.
    /// </summary>
    /// <returns>True when both boundaries were found, in which case
    /// <paramref name="declText"/> is the text between the architecture's
    /// `is` and its top-level `begin`, <paramref name="bodyText"/> is
    /// the text between that `begin` and the matching final `end`, and
    /// <paramref name="endIdx"/> is the offset just past the closing
    /// semicolon.</returns>
    internal static bool SplitArchitecture(
        string source, int startIdx,
        out string declText, out string bodyText, out int endIdx)
    {
        // F-G-09 fix (2026-07-17): mask string-literal contents before
        // running the keyword regex so `end` inside e.g.
        // `report "TB: end of stimulus";` doesn't false-match as a real
        // VHDL `end`. Indexes into `masked` line up with `source`, so we
        // substring from `source` for the output.
        string masked = VhdlText.MaskStringLiterals(source);
        // Opening keywords that introduce a nested begin/end pair.
        // `procedure` and `function` use `begin ... end function`. `process`
        // uses `process ... begin ... end process`. Generate/block also.
        // `if`/`case`/`loop` use `... end if/end case/end loop` but DON'T
        // have a begin — so they need to be tracked separately so a
        // top-level begin keyword isn't confused with a generate body.

        // We use a simple word-boundary tokenizer over `source` starting
        // at `startIdx`. At each keyword, we either increment/decrement
        // the begin-end nesting depth or mark a state transition.

        int depth = 0;                       // count of open begin/end blocks (including procs)
        int beginIdx = -1;                   // architecture-level begin position
        // Keyword matcher. The optional second group lets `end if` / `end
        // case` / `end process` / `end function` etc. consume the closer
        // keyword as one token so the inner `if`/`case`/`process`/...
        // isn't re-counted as a fresh opener on the next iteration.
        //
        // Critically: the inter-word gap allows ONLY horizontal whitespace
        // (`[ \t]+`), not `\s+`. VHDL puts `end <keyword>` on the same
        // line; allowing newlines made the regex eagerly consume
        // sequences like `begin\n  case` as a single match, which dropped
        // the `case` keyword from the depth counter and caused
        // SplitArchitecture to terminate prematurely at the inner
        // `end process` instead of the architecture-level `end`.
        var rx = new Regex(@"\b(architecture|process|function|procedure|generate|block|begin|end|if|elsif|else|case|loop|then|is)\b(?:[ \t]+(function|procedure|process|generate|block|architecture|if|case|loop)\b)?",
                           RegexOptions.IgnoreCase);
        // First locate the architecture-level `begin`.
        Match m = rx.Match(masked, startIdx);
        while (m.Success)
        {
            string kw = m.Groups[1].Value.ToLowerInvariant();
            if (kw == "begin")
            {
                if (depth == 0)
                {
                    beginIdx = m.Index;
                    break;
                }
                // begin inside a function/procedure already open — leave depth alone.
            }
            else if (kw == "function" || kw == "procedure")
            {
                // function/procedure declaration — adds one level (the body's begin/end will balance it).
                depth++;
            }
            else if (kw == "end")
            {
                // `end function` / `end procedure` closes a nested block.
                if (depth > 0) depth--;
            }
            m = m.NextMatch();
        }

        if (beginIdx < 0)
        {
            declText = string.Empty; bodyText = string.Empty; endIdx = startIdx;
            return false;
        }

        declText = source.Substring(startIdx, beginIdx - startIdx);

        // Now find the matching closing `end [architecture] [name] ;`.
        // From past the begin keyword, we walk forward tracking nested
        // begin/end blocks (process, function body, if, case, loop, etc.).
        int cursor = beginIdx + "begin".Length;
        int innerDepth = 0;
        bool conditionPending = false;   // inside an `if`/`case` header, before its `then`/`is`/`generate`
        Match em = rx.Match(masked, cursor);
        while (em.Success)
        {
            string kw = em.Groups[1].Value.ToLowerInvariant();
            switch (kw)
            {
                case "if":
                case "case":
                    // `lbl: if C generate` / `case … generate` close with one
                    // `end generate`: their `generate` must not open a second level.
                    innerDepth++;
                    conditionPending = true;
                    break;
                case "elsif":
                    // `elsif C then` (sequential) or `elsif C generate`: a branch of the
                    // open if, never a new level.
                    conditionPending = true;
                    break;
                case "else":
                    // `else generate` continues the open if generate.
                    if (em.Groups[2].Value.Equals("generate", StringComparison.OrdinalIgnoreCase)) conditionPending = false;
                    break;
                case "then":
                case "is":
                    conditionPending = false;
                    break;
                case "generate":
                    if (conditionPending) conditionPending = false;
                    else innerDepth++;
                    break;
                case "process":
                case "function":
                case "procedure":
                case "block":
                case "loop":
                    innerDepth++;
                    conditionPending = false;
                    break;
                case "end":
                    if (innerDepth == 0)
                    {
                        // Use masked so a `;` inside a string literal doesn't
                        // masquerade as the arch's closing punctuation.
                        int semi = masked.IndexOf(';', em.Index);
                        if (semi < 0)
                        {
                            bodyText = string.Empty; endIdx = em.Index;
                            return false;
                        }
                        bodyText = source.Substring(beginIdx + "begin".Length, em.Index - (beginIdx + "begin".Length));
                        endIdx = semi + 1;
                        return true;
                    }
                    innerDepth--;
                    break;
            }
            em = em.NextMatch();
        }

        bodyText = string.Empty; endIdx = source.Length;
        return false;
    }

    /// <summary>
    /// Finds the first match of <paramref name="startRe"/> in <paramref name="source"/>
    /// and returns the text inside the parentheses that follow, counting
    /// nested parens. Returns null when the keyword isn't present.
    /// </summary>
    internal static string? ExtractParenBlockAfter(string source, Regex startRe)
    {
        var m = startRe.Match(source);
        if (!m.Success) return null;
        int i = m.Index + m.Length;
        int depth = 1;
        int start = i;
        while (i < source.Length && depth > 0)
        {
            if (source[i] == '(') depth++;
            else if (source[i] == ')')
            {
                depth--;
                if (depth == 0) return source.Substring(start, i - start);
            }
            i++;
        }
        return null;
    }

    // Splits `s` by `delim` while ignoring `delim` characters inside
    // parentheses. Used for ports and port-map associations where
    // associations may include subscript expressions like `bcd(3 downto 0)`.
    internal static List<string> SplitBalanced(string s, char delim)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        int depth = 0;
        foreach (var ch in s)
        {
            if (ch == '(') depth++;
            else if (ch == ')') depth = Math.Max(0, depth - 1);

            if (ch == delim && depth == 0)
            {
                if (current.Length > 0)
                {
                    parts.Add(current.ToString().Trim());
                    current.Clear();
                }
            }
            else
            {
                current.Append(ch);
            }
        }
        if (current.Length > 0)
        {
            var last = current.ToString().Trim();
            if (!string.IsNullOrEmpty(last)) parts.Add(last);
        }
        return parts;
    }

    // ---------- Component-instantiation detection ----------

    // `label : [entity|component|configuration] [lib.]name [(arch)] [generic map (...)] port map (...);`
    internal static readonly Regex InstanceRe = new(
        @"(?<label>\b\w+)\s*:\s*" +
        @"(?:(?<kind>entity|component|configuration)\s+(?:(?<lib>\w+)\s*\.\s*)?)?(?<ent>\w+)(?:\s*\(\s*(?<arch>\w+)\s*\))?" +
        @"(?:\s*generic\s+map\s*\((?<gmap>.*?)\))?" +
        @"\s*port\s+map\s*\((?<pmap>.*?)\)\s*;",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// <summary>True when <paramref name="body"/> holds a component/entity/configuration instance.</summary>
    internal static bool HasComponentInstance(string body) =>
        InstanceRe.Matches(body).Any(m => !IsControlFlowLabel(m.Groups["label"].Value, m.Groups["ent"].Value));

    /// <summary>
    /// Filters out matches of the instance regex that are actually labelled
    /// concurrent statements (process / if-generate / case / for-generate / etc.)
    /// whose label is followed by a keyword the regex misinterprets as an
    /// entity name. The list is conservative — VHDL allows any identifier
    /// after `label :`, so we only reject when the "entity name" is a
    /// concurrent-statement keyword.
    /// </summary>
    internal static bool IsControlFlowLabel(string label, string entityName)
    {
        return entityName.Equals("process", StringComparison.OrdinalIgnoreCase)
            || entityName.Equals("entity", StringComparison.OrdinalIgnoreCase)
            || entityName.Equals("component", StringComparison.OrdinalIgnoreCase)
            || entityName.Equals("configuration", StringComparison.OrdinalIgnoreCase)
            || entityName.Equals("if", StringComparison.OrdinalIgnoreCase)
            || entityName.Equals("for", StringComparison.OrdinalIgnoreCase)
            || entityName.Equals("while", StringComparison.OrdinalIgnoreCase)
            || entityName.Equals("case", StringComparison.OrdinalIgnoreCase)
            || entityName.Equals("with", StringComparison.OrdinalIgnoreCase)
            || entityName.Equals("block", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whole-word identifier substitution. Replaces every standalone
    /// occurrence of a key in <paramref name="rename"/> with its mapped
    /// value. Identifiers are matched case-insensitively; substring matches
    /// (e.g. `clk` inside `clkx`), names after a <c>.</c> (record fields,
    /// selected names) and text inside string literals are NOT replaced.
    /// </summary>
    internal static string RenameTokens(string source, Dictionary<string, string> rename)
    {
        if (rename.Count == 0 || string.IsNullOrEmpty(source)) return source;

        // Build one alternation regex from the keys (longest-first so longer
        // names aren't shadowed by shorter ones that share a prefix).
        // Matched on a copy with string literals masked, and never right after
        // a `.`: record fields (`p.lo`) and selected names (`work.pkg.x`) keep
        // their spelling.
        var keys = rename.Keys
                         .OrderByDescending(k => k.Length)
                         .Select(Regex.Escape);
        var pattern = @"(?<![\w.])(" + string.Join("|", keys) + @")\b";
        var rx = new Regex(pattern, RegexOptions.IgnoreCase);
        var sb = new StringBuilder(source.Length);
        int last = 0;
        foreach (Match m in rx.Matches(VhdlText.MaskStringLiterals(source)))
        {
            sb.Append(source, last, m.Index - last).Append(rename[m.Value]);
            last = m.Index + m.Length;
        }
        return sb.Append(source, last, source.Length - last).ToString();
    }
}
