using System.Text;
using System.Text.RegularExpressions;
using Parser.Models;

namespace Interpreter.Services;

/// <summary>
/// Source-level pre-elaboration for multi-file VHDL designs. Inlines every
/// component instantiation under the entry-point entity so the simulator
/// — which today doesn't execute child architectures at runtime — sees a
/// flat single-entity source that mirrors the modular design's behaviour
/// exactly. The user's source files on disk are never modified; the
/// elaborator emits a temporary flattened file that the parser consumes.
/// </summary>
/// <remarks>
/// <para>The elaborator operates with three passes:</para>
/// <list type="number">
///   <item>Scan every .vhd / .vhdl file under the project directory and
///         build a registry of entity declarations (name + ports) plus the
///         raw source text of each architecture body.</item>
///   <item>Starting from the entry-point entity, find every component
///         instantiation in its architecture and recursively inline the
///         child's body. Port-map associations rename child ports to the
///         parent's connected signals; internal child signals are prefixed
///         with the instance label to keep names unique across instances.</item>
///   <item>Component declarations inside the parent (the
///         <c>component X is port (...) end component;</c> blocks) are
///         removed since their entities have been inlined.</item>
/// </list>
/// <para>What is intentionally NOT done here:</para>
/// <list type="bullet">
///   <item>Generic-map elaboration. Generics inside child entities default
///         to their declared values; explicit generic-map overrides are
///         applied via simple text substitution before inlining.</item>
///   <item>Architecture-name selection (<c>entity work.foo(arch_name)</c>).
///         The elaborator picks the first architecture associated with the
///         child entity in source order, which matches the VHDL "last
///         analyzed wins" rule for single-architecture designs.</item>
/// </list>
/// </remarks>
public sealed class SourceElaborator
{
    // ---------- Public entry point ----------

    /// <summary>
    /// Elaborates the entry-point entity by inlining every child component
    /// instantiation. Returns the flattened single-entity VHDL source as a
    /// string. The caller decides where to persist it.
    /// </summary>
    /// <param name="projectDir">Directory containing all .vhd / .vhdl files.</param>
    /// <param name="entryPointName">Name of the top-level entity to flatten.</param>
    /// <returns>The flattened source string, or null when the entry point
    /// has no component instantiations to inline (the caller should fall
    /// back to the original source in that case).</returns>
    public static string? ElaborateOrNull(string projectDir, string entryPointName)
    {
        var entities = ScanProject(projectDir);
        if (!entities.TryGetValue(entryPointName, out var top))
            return null;

        // Skip work when no component instances live under the entry point —
        // single-entity designs need no elaboration.
        if (!HasComponentInstance(top.ArchitectureBody))
            return null;

        var sb = new StringBuilder();

        // Library / use clauses from the entry-point file are preserved at
        // the top of the flat source; child files' library clauses are
        // already covered by the top's preamble for the simple practica
        // designs we care about.
        sb.Append(top.HeaderText);
        sb.Append('\n');

        // Emit the entity declaration unchanged.
        sb.Append(top.EntityText);
        sb.Append('\n');

        // Walk the architecture body, inlining each component instantiation.
        var inlinedArch = InlineComponents(top, entities, instancePathPrefix: string.Empty);
        sb.Append(inlinedArch);

        return sb.ToString();
    }

    // ---------- Project scan ----------

    /// <summary>
    /// Per-entity record produced by the project scan. Holds the verbatim
    /// `entity` and `architecture` source ranges plus the parsed port list
    /// and internal signal/constant declarations the elaborator needs to
    /// rename when inlining a child.
    /// </summary>
    private sealed class EntityRecord
    {
        public string Name { get; init; } = string.Empty;
        public string HeaderText { get; init; } = string.Empty;     // library/use clauses above the entity
        public string EntityText { get; init; } = string.Empty;     // the `entity X is ... end ...;` block
        public string ArchitectureText { get; init; } = string.Empty; // the full `architecture ... end ...;` block
        public string ArchitectureDeclPart { get; init; } = string.Empty; // between `is` and `begin`
        public string ArchitectureBody { get; init; } = string.Empty;     // between `begin` and the closing `end`
        public string ArchitectureCloseText { get; init; } = "end architecture;";
        public List<string> Ports { get; init; } = new();           // port names (lowercased)
        public List<string> LocalSignals { get; init; } = new();    // signals + constants declared in arch decl part
        public List<string> Generics { get; init; } = new();        // generic names (lowercased)
    }

    private static Dictionary<string, EntityRecord> ScanProject(string projectDir)
    {
        var dict = new Dictionary<string, EntityRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(projectDir, "*.vhd*", SearchOption.AllDirectories))
        {
            if (!path.EndsWith(".vhd", StringComparison.OrdinalIgnoreCase) &&
                !path.EndsWith(".vhdl", StringComparison.OrdinalIgnoreCase))
                continue;
            // F-G-10 (2026-07-18): skip our own elaborator artefacts. When a
            // previous run of the same project wrote `_elaborated_<name>.vhd`
            // to disk, the next elaboration would pick that flat file up
            // too and OVERWRITE `dict[<name>]` with the leftover record —
            // whose arch body has zero component instances (they were all
            // inlined), so `HasComponentInstance` returns false and
            // `ElaborateOrNull` returns null. The parser then falls back to
            // the source and sees only the outer testbench signals (11 for
            // Ring 5 capstone, missing all DUT internals).
            var fileName = Path.GetFileName(path);
            if (fileName.StartsWith("_elaborated_", StringComparison.OrdinalIgnoreCase))
                continue;
            string src;
            try { src = File.ReadAllText(path); } catch { continue; }
            foreach (var rec in ParseEntities(src))
            {
                dict[rec.Name] = rec;
            }
        }
        return dict;
    }

    // ---------- Entity / architecture extraction ----------

    // Closer forms accepted (IEEE 1076-1993+): `end;`, `end entity;`,
    // `end <name>;`, `end entity <name>;`. GAP-ELAB-001: the keyword and the
    // label are each optional, so `end entity;` must not require trailing
    // whitespace after `entity`.
    private static readonly Regex EntityRe = new(
        @"^\s*entity\s+(?<name>\w+)\s+is\b(?<body>.*?)\bend\b\s*(?:entity\b\s*)?(?:\k<name>\b\s*)?;",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Multiline);

    // Locates the start of an architecture (`architecture <aname> of <ename> is`).
    // The body / decl boundary is then resolved manually via SplitArchitecture
    // so that `begin`/`end` keywords belonging to inner functions, processes
    // or generate blocks don't get mistaken for the architecture-level pair.
    private static readonly Regex ArchStartRe = new(
        @"\barchitecture\s+(?<aname>\w+)\s+of\s+(?<ename>\w+)\s+is\b",
        RegexOptions.IgnoreCase);

    // PortRe/GenericRe locate the start of a `port (` or `generic (`
    // clause; the matching `)` is found by manually counting parens via
    // ExtractParenBlock (the regex's `.*?` stops at the first `)` it
    // sees, which is wrong when port types contain vector ranges like
    // `STD_LOGIC_VECTOR(9 downto 0)`).
    private static readonly Regex PortStartRe = new(
        @"\bport\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex GenericStartRe = new(
        @"\bgeneric\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    // Match a signal/constant declaration at line start OR after a `;`, so
    // several on one line (`signal s1 : ...; signal c1 : ...; signal c2 : ...;`)
    // are all collected — otherwise only the first is prefixed when inlining an
    // instance, and the rest collide across repeated instances (duplicate c1).
    private static readonly Regex SignalDeclRe = new(
        @"(?:^|;)\s*(signal|constant)\s+(?<names>[\w,\s]+?)\s*:",
        RegexOptions.IgnoreCase | RegexOptions.Multiline);

    // `is` after the component name is optional since VHDL-93.
    private static readonly Regex ComponentDeclRe = new(
        @"\bcomponent\s+(?<cname>\w+)(?:\s+is)?\b.*?\bend\s+component(?:\s+\k<cname>)?\s*;",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static IEnumerable<EntityRecord> ParseEntities(string source)
    {
        // Strip line comments first (string-aware, line breaks kept) so they
        // don't poison the regexes — each file is small, a full scan is OK.
        var stripped = VhdlText.StripLineComments(source);

        // Capture library/use lines that precede the first entity declaration —
        // we want to forward those into the flat output.
        string headerText = string.Empty;
        var firstEntityMatch = EntityRe.Match(stripped);
        if (firstEntityMatch.Success)
        {
            headerText = stripped.Substring(0, firstEntityMatch.Index);
        }

        foreach (Match em in EntityRe.Matches(stripped))
        {
            string name = em.Groups["name"].Value;
            string entityText = em.Value;
            string entityBody = em.Groups["body"].Value;

            var ports = new List<string>();
            string? portsText = ExtractParenBlockAfter(entityBody, PortStartRe);
            if (portsText != null)
            {
                foreach (var portName in SplitPortNames(portsText))
                    ports.Add(portName.ToLowerInvariant());
            }

            var generics = new List<string>();
            string? genericsText = ExtractParenBlockAfter(entityBody, GenericStartRe);
            if (genericsText != null)
            {
                foreach (var gn in SplitPortNames(genericsText))
                    generics.Add(gn.ToLowerInvariant());
            }

            // Find a matching architecture by entity-name. We scan from the
            // entity's end forward so the closest architecture wins.
            string archText = string.Empty;
            string archDecl = string.Empty;
            string archBody = string.Empty;
            foreach (Match am in ArchStartRe.Matches(stripped, em.Index + em.Length))
            {
                if (!string.Equals(am.Groups["ename"].Value, name, StringComparison.OrdinalIgnoreCase))
                    continue;
                int startIdx = am.Index + am.Length;
                if (SplitArchitecture(stripped, startIdx, out var declText, out var bodyText, out var endIdx))
                {
                    archText = stripped.Substring(am.Index, endIdx - am.Index);
                    archDecl = declText;
                    archBody = bodyText;
                }
                break;
            }

            // Strip component declarations from archDecl — once inlined we
            // don't need them anymore.
            archDecl = ComponentDeclRe.Replace(archDecl, "");

            // Collect names of signals/constants declared in archDecl so we
            // can prefix them when inlining this entity inside a parent.
            var locals = new List<string>();
            foreach (Match sd in SignalDeclRe.Matches(archDecl))
            {
                var raw = sd.Groups["names"].Value;
                foreach (var nm in raw.Split(','))
                {
                    var t = nm.Trim();
                    if (!string.IsNullOrEmpty(t)) locals.Add(t.ToLowerInvariant());
                }
            }

            yield return new EntityRecord
            {
                Name = name,
                HeaderText = headerText,
                EntityText = entityText,
                ArchitectureText = archText,
                ArchitectureDeclPart = archDecl,
                ArchitectureBody = archBody,
                ArchitectureCloseText = $"end architecture;",
                Ports = ports,
                LocalSignals = locals,
                Generics = generics,
            };
        }
    }

    // Splits the body of a `port(...)` or `generic(...)` block into
    // individual identifier names. Each clause looks like
    //   `name1, name2 : direction type := default; name3 : ...`
    // and we want just the identifiers on the left of every colon.
    private static List<string> SplitPortNames(string raw)
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
    private static bool SplitArchitecture(
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
        var rx = new Regex(@"\b(architecture|process|function|procedure|generate|block|begin|end|if|case|loop)\b(?:[ \t]+(function|procedure|process|generate|block|architecture|if|case|loop)\b)?",
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
        Match em = rx.Match(masked, cursor);
        while (em.Success)
        {
            string kw = em.Groups[1].Value.ToLowerInvariant();
            switch (kw)
            {
                case "process":
                case "function":
                case "procedure":
                case "generate":
                case "block":
                case "if":
                case "case":
                case "loop":
                    innerDepth++;
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
    private static string? ExtractParenBlockAfter(string source, Regex startRe)
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
    private static List<string> SplitBalanced(string s, char delim)
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

    // Matches `label : EntityName [generic map (...)] port map (...);` and
    // also the direct-instantiation form `label : entity [lib.]EntityName [(arch)]`.
    private static readonly Regex InstanceRe = new(
        @"(?<label>\b\w+)\s*:\s*" +
        @"(?:entity\s+(?:(?<lib>\w+)\s*\.\s*)?)?(?<ent>\w+)(?:\s*\(\s*\w+\s*\))?" +
        @"(?:\s*generic\s+map\s*\((?<gmap>.*?)\))?" +
        @"\s*port\s+map\s*\((?<pmap>.*?)\)\s*;",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static bool HasComponentInstance(string archBody)
        => InstanceRe.IsMatch(archBody);

    // ---------- Inlining ----------

    // Recursion cap for InlineComponentsToFragments — 3-level and beyond
    // testbenches (tb → dut → sub) inline correctly, but malformed circular
    // references bail after this many hops and log-warn on stderr.
    private const int MaxElaborationDepth = 8;

    private static string InlineComponents(
        EntityRecord parent,
        Dictionary<string, EntityRecord> all,
        string instancePathPrefix)
    {
        var (declPart, bodyPart) = InlineComponentsToFragments(parent, all, depth: 0, pathPrefix: instancePathPrefix);

        var arch = new StringBuilder();
        arch.AppendLine($"architecture rtl of {parent.Name} is");
        arch.Append(declPart);
        arch.AppendLine();
        arch.AppendLine("begin");
        arch.Append(bodyPart);
        arch.AppendLine();
        arch.AppendLine("end architecture;");
        return arch.ToString();
    }

    /// <summary>
    /// Recursively flattens <paramref name="parent"/> by inlining every child
    /// component instance found in its architecture body. When a child body
    /// itself contains further instances (three-level `tb → dut → sub`
    /// testbenches, audit F-G-06), those descendant instances are elaborated
    /// via a nested call before the child fragments are stitched into the
    /// parent. The parent-side rename dict is applied over the *already
    /// flattened* child fragments so descendant signal renames survive.
    /// Returns the parent's decl-part text and body text as a fragment pair
    /// (no `architecture … is / begin / end` wrapper) so the caller can
    /// either stitch a fresh architecture around it (top-level path) or
    /// re-embed the pair inside a further parent (recursion path).
    /// </summary>
    private static (string DeclText, string BodyText) InlineComponentsToFragments(
        EntityRecord parent,
        Dictionary<string, EntityRecord> all,
        int depth,
        string pathPrefix = "")
    {
        // Depth guard: refuse to recurse forever on circular designs.
        if (depth > MaxElaborationDepth)
        {
            Console.Error.WriteLine($"[SourceElaborator] Recursion depth {depth} exceeded for '{parent.Name}' — leaving remaining child instances unresolved.");
            return (parent.ArchitectureDeclPart, parent.ArchitectureBody);
        }

        var declSb = new StringBuilder(parent.ArchitectureDeclPart);
        var bodySb = new StringBuilder();

        int cursor = 0;
        foreach (Match im in InstanceRe.Matches(parent.ArchitectureBody))
        {
            // Append unchanged text from cursor up to the start of this instance.
            bodySb.Append(parent.ArchitectureBody, cursor, im.Index - cursor);

            string label = im.Groups["label"].Value;
            string entName = im.Groups["ent"].Value;
            string portMapRaw = im.Groups["pmap"].Value;
            string genericMapRaw = im.Groups["gmap"].Success ? im.Groups["gmap"].Value : string.Empty;

            // Reserved control-flow keywords sometimes match the instance
            // regex (label is greedy on `\w+`); skip them.
            if (IsControlFlowLabel(label, entName))
            {
                bodySb.Append(im.Value);
                cursor = im.Index + im.Length;
                continue;
            }

            if (!all.TryGetValue(entName, out var child))
            {
                // Couldn't find the child — leave the instantiation as-is
                // (the parser will then error out with a clear "unknown
                // component" message rather than silent zeros).
                bodySb.Append(im.Value);
                cursor = im.Index + im.Length;
                continue;
            }

            // For each port, allocate an alias signal in the parent's
            // decl section. The inlined body references the alias rather
            // than the mapped expression directly, which keeps sensitivity
            // lists, subscript LHS targets, and concurrent assignments all
            // valid — even when the parent maps a slice or a literal to a
            // child port. Direction handling:
            //   * `in`  : alias is driven from the mapped expression
            //   * `out` : alias drives the mapped expression
            //   * `inout`/`buffer` : both directions wired
            // We look up the child's port direction + type from its entity
            // text so the alias has the correct width.
            var rename = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var portMappings = BuildPortMappings(child.Ports, portMapRaw);
            var portInfo = ExtractPortInfo(child.EntityText);

            // F-G-06 sibling-collision fix (session 16, 2026-07-12): compose the
            // caller's pathPrefix into aliasPrefix so nested elaborations produce
            // fully-qualified `u1__u_scan__foo` / `u2__u_scan__foo` names for
            // sibling instances of the same mid-level entity. Without this,
            // `tb : u1 = calc_top, u2 = calc_top` collapsed both grandchild
            // signals to the same `u_scan__foo` and the parser saw duplicates.
            string aliasPrefix = $"{pathPrefix}{label}__";
            foreach (var kv in portMappings)
            {
                rename[kv.Key] = aliasPrefix + kv.Key;
            }

            // Internal child signals → prefix with `<instance_label>__`.
            foreach (var sig in child.LocalSignals)
            {
                rename[sig] = aliasPrefix + sig;
            }

            // Generics: substitute the generic-map values into the child text.
            // P4 bug: when the parent doesn't pass a generic map, fall back
            // to the child's own default values (e.g. `WIDTH : natural := 16`)
            // — without this, the alias signal declarations carry unresolved
            // identifier references (`STD_LOGIC_VECTOR(WIDTH-1 downto 0)`)
            // that crash the parent's parser long after the offending line.
            var genericValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            ExtractGenericDefaults(genericValues, child.EntityText);
            ApplyGenericMap(genericValues, child.Generics, genericMapRaw); // explicit overrides defaults

            // F-G-06 (2026-07-09): if the child's own body has further
            // component instances, elaborate them first so the fragments we
            // rename here already have all descendants inlined. Without this
            // step a three-level testbench (`tb → dut → sub`) would leave
            // `sub`'s instantiation lines verbatim in the flattened output,
            // and the parser would then reject them as "unknown component".
            string effectiveChildDecl = child.ArchitectureDeclPart;
            string effectiveChildBody = child.ArchitectureBody;
            if (HasComponentInstance(child.ArchitectureBody))
            {
                // Propagate aliasPrefix as the grandchild's pathPrefix so its
                // own aliasPrefix becomes `u1__u_scan__` (chained), not just
                // `u_scan__`. See sibling-collision fix note above.
                var (grandDecl, grandBody) = InlineComponentsToFragments(child, all, depth + 1, aliasPrefix);
                effectiveChildDecl = grandDecl;
                effectiveChildBody = grandBody;
            }

            // Substitute port + signal names in the child's body & decl.
            string childDecl = RenameTokens(effectiveChildDecl, rename);
            string childBody = RenameTokens(effectiveChildBody, rename);

            // Apply generic substitutions on top.
            foreach (var kv in genericValues)
            {
                childDecl = RenameTokens(childDecl, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    { [kv.Key] = kv.Value });
                childBody = RenameTokens(childBody, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    { [kv.Key] = kv.Value });
            }

            declSb.AppendLine();
            declSb.AppendLine($"  -- ↓ inlined from component {label} : {entName}");
            foreach (var kv in portMappings)
            {
                var aliasName = aliasPrefix + kv.Key;
                if (portInfo.TryGetValue(kv.Key, out var info))
                {
                    // P4 bug: port type text may reference the child's
                    // generics (e.g. `STD_LOGIC_VECTOR(WIDTH-1 downto 0)`).
                    // Apply the generic substitutions to the type text so
                    // the parent's parser sees concrete bounds instead of
                    // unresolved identifiers. Without this, every alias
                    // typed by a generic-bounded vector silently corrupts
                    // the entire architecture's parse.
                    string typeText = info.TypeText;
                    foreach (var gv in genericValues)
                    {
                        typeText = RenameTokens(typeText,
                            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                                { [gv.Key] = gv.Value });
                    }
                    declSb.AppendLine($"  signal {aliasName} : {typeText};");
                }
                else
                {
                    declSb.AppendLine($"  signal {aliasName} : STD_LOGIC;");
                }
            }
            declSb.Append(childDecl);

            bodySb.AppendLine();
            bodySb.AppendLine($"  -- ↓ inlined body for component {label} : {entName}");
            foreach (var kv in portMappings)
            {
                var aliasName = aliasPrefix + kv.Key;
                var mapped = kv.Value;
                if (!portInfo.TryGetValue(kv.Key, out var info))
                {
                    bodySb.AppendLine($"  {aliasName} <= {mapped};");
                    continue;
                }
                if (info.IsOutput)
                {
                    bodySb.AppendLine($"  {mapped} <= {aliasName};");
                }
                else
                {
                    bodySb.AppendLine($"  {aliasName} <= {mapped};");
                }
            }
            // The child's actual concurrent body.
            bodySb.Append(childBody);
            bodySb.AppendLine();
            bodySb.AppendLine($"  -- ↑ end inlined body for {label} : {entName}");

            cursor = im.Index + im.Length;
        }
        // Trailing text after the last instance.
        bodySb.Append(parent.ArchitectureBody, cursor, parent.ArchitectureBody.Length - cursor);

        return (declSb.ToString(), bodySb.ToString());
    }

    private static readonly Regex SimpleIdentRe = new(@"^\s*[a-zA-Z_][a-zA-Z0-9_]*\s*$", RegexOptions.Compiled);

    /// <summary>
    /// True when the port-map RHS is a bare identifier (no subscript, slice,
    /// concatenation, or function call). Used by the elaborator to decide
    /// whether an output port can be direct-bound to the parent's signal
    /// (bypassing the alias-hop) or whether the alias has to stay so the
    /// sliced LHS works inside the inlined child body.
    /// </summary>
    private static bool IsSimpleIdentifier(string mapped)
        => !string.IsNullOrWhiteSpace(mapped) && SimpleIdentRe.IsMatch(mapped);

    /// <summary>
    /// Filters out matches of the instance regex that are actually labelled
    /// concurrent statements (process / if-generate / case / for-generate / etc.)
    /// whose label is followed by a keyword the regex misinterprets as an
    /// entity name. The list is conservative — VHDL allows any identifier
    /// after `label :`, so we only reject when the "entity name" is a
    /// concurrent-statement keyword.
    /// </summary>
    private static bool IsControlFlowLabel(string label, string entityName)
    {
        return entityName.Equals("process", StringComparison.OrdinalIgnoreCase)
            || entityName.Equals("if", StringComparison.OrdinalIgnoreCase)
            || entityName.Equals("for", StringComparison.OrdinalIgnoreCase)
            || entityName.Equals("while", StringComparison.OrdinalIgnoreCase)
            || entityName.Equals("case", StringComparison.OrdinalIgnoreCase)
            || entityName.Equals("with", StringComparison.OrdinalIgnoreCase)
            || entityName.Equals("block", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Builds an ordered mapping of child-port-name → parent-mapped-expression.
    /// Handles both named and positional associations.
    /// </summary>
    private static List<KeyValuePair<string, string>> BuildPortMappings(
        List<string> childPorts,
        string portMapRaw)
    {
        var assocs = SplitBalanced(portMapRaw, ',');
        var result = new List<KeyValuePair<string, string>>();
        bool anyArrow = assocs.Any(a => a.Contains("=>"));

        if (anyArrow)
        {
            foreach (var a in assocs)
            {
                int arrow = a.IndexOf("=>");
                if (arrow < 0) continue;
                var left  = a.Substring(0, arrow).Trim();
                var right = a.Substring(arrow + 2).Trim();
                if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)) continue;
                result.Add(new KeyValuePair<string, string>(left.ToLowerInvariant(), right));
            }
        }
        else
        {
            for (int i = 0; i < Math.Min(assocs.Count, childPorts.Count); i++)
            {
                result.Add(new KeyValuePair<string, string>(childPorts[i], assocs[i]));
            }
        }
        return result;
    }

    /// <summary>
    /// Information about a single port: direction (in/out/inout) and type text.
    /// Extracted from a child entity's `port (...)` clause.
    /// </summary>
    private sealed class PortInfo
    {
        public string TypeText { get; init; } = "STD_LOGIC";
        public bool   IsOutput { get; init; }
    }

    private static Dictionary<string, PortInfo> ExtractPortInfo(string entityText)
    {
        var info = new Dictionary<string, PortInfo>(StringComparer.OrdinalIgnoreCase);
        var portsText = ExtractParenBlockAfter(entityText, PortStartRe);
        if (portsText == null) return info;
        foreach (var clause in SplitBalanced(portsText, ';'))
        {
            int colon = clause.IndexOf(':');
            if (colon < 0) continue;
            var left  = clause.Substring(0, colon).Trim();
            var right = clause.Substring(colon + 1).Trim();

            // Parse direction + type. VHDL grammar:
            //   <names> : [direction] <type>
            // direction ∈ {in, out, inout, buffer, linkage}
            string direction = "in";
            string typeText = right;
            var firstSpace = SpaceOrTab(right);
            if (firstSpace > 0)
            {
                var maybeDir = right.Substring(0, firstSpace).ToLowerInvariant();
                if (maybeDir is "in" or "out" or "inout" or "buffer" or "linkage")
                {
                    direction = maybeDir;
                    typeText = right.Substring(firstSpace + 1).Trim();
                }
            }
            bool isOutput = direction is "out" or "buffer";

            foreach (var name in left.Split(','))
            {
                var t = name.Trim();
                if (string.IsNullOrEmpty(t)) continue;
                info[t.ToLowerInvariant()] = new PortInfo
                {
                    TypeText = StripDefault(typeText),
                    IsOutput = isOutput,
                };
            }
        }
        return info;

        static int SpaceOrTab(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r') return i;
            }
            return -1;
        }
        static string StripDefault(string typeText)
        {
            // Drop any `:= default` clause.
            int idx = typeText.IndexOf(":=");
            return (idx >= 0 ? typeText.Substring(0, idx) : typeText).Trim();
        }
    }

    /// <summary>
    /// Reads default values out of a child entity's <c>generic (...)</c>
    /// clause and seeds <paramref name="values"/> with them. The parent's
    /// explicit <c>generic map</c> overrides these in the subsequent
    /// <see cref="ApplyGenericMap"/> call. Generics declared without a
    /// default (e.g. <c>generic (WIDTH : natural)</c>) are left alone so
    /// the existing legacy behaviour is preserved.
    /// </summary>
    private static void ExtractGenericDefaults(
        Dictionary<string, string> values,
        string entityText)
    {
        string? genericsText = ExtractParenBlockAfter(entityText, GenericStartRe);
        if (genericsText == null) return;
        foreach (var clause in SplitBalanced(genericsText, ';'))
        {
            int colon = clause.IndexOf(':');
            if (colon < 0) continue;
            string left  = clause.Substring(0, colon);
            string right = clause.Substring(colon + 1);
            int assign = right.IndexOf(":=");
            if (assign < 0) continue;
            string defaultValue = right.Substring(assign + 2).Trim();
            foreach (var name in left.Split(','))
            {
                var n = name.Trim();
                if (!string.IsNullOrEmpty(n))
                    values[n.ToLowerInvariant()] = defaultValue;
            }
        }
    }

    private static void ApplyGenericMap(
        Dictionary<string, string> values,
        List<string> childGenerics,
        string genericMapRaw)
    {
        if (string.IsNullOrWhiteSpace(genericMapRaw)) return;
        var assocs = SplitBalanced(genericMapRaw, ',');
        bool anyArrow = assocs.Any(a => a.Contains("=>"));
        if (anyArrow)
        {
            foreach (var a in assocs)
            {
                int arrow = a.IndexOf("=>");
                if (arrow < 0) continue;
                var left  = a.Substring(0, arrow).Trim();
                var right = a.Substring(arrow + 2).Trim();
                if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)) continue;
                values[left.ToLowerInvariant()] = right;
            }
        }
        else
        {
            for (int i = 0; i < Math.Min(assocs.Count, childGenerics.Count); i++)
            {
                values[childGenerics[i]] = assocs[i];
            }
        }
    }

    /// <summary>
    /// Whole-word identifier substitution. Replaces every standalone
    /// occurrence of a key in <paramref name="rename"/> with its mapped
    /// value. Identifiers are matched case-insensitively; substring matches
    /// (e.g. `clk` inside `clkx`) are NOT replaced.
    /// </summary>
    private static string RenameTokens(string source, Dictionary<string, string> rename)
    {
        if (rename.Count == 0 || string.IsNullOrEmpty(source)) return source;

        // Build one alternation regex from the keys (longest-first so longer
        // names aren't shadowed by shorter ones that share a prefix).
        var keys = rename.Keys
                         .OrderByDescending(k => k.Length)
                         .Select(Regex.Escape);
        var pattern = @"\b(" + string.Join("|", keys) + @")\b";
        var rx = new Regex(pattern, RegexOptions.IgnoreCase);
        return rx.Replace(source, m =>
        {
            return rename[m.Value.ToLowerInvariant()];
        });
    }

    // ---------- Standalone generic specialization (for resource estimation) ----------

    /// <summary>
    /// Produces a <em>standalone</em> VHDL source for a single entity with its
    /// generics resolved to concrete values — the entity's own declared defaults,
    /// overridden by <paramref name="genericOverrides"/>. Unlike
    /// <see cref="ElaborateOrNull"/>, the entity keeps its ports (it is not
    /// inlined), so a resource estimate over the result counts clocked <em>port</em>
    /// targets the same way the un-parameterized child would. The
    /// <c>generic (...)</c> clause is stripped since every generic becomes a
    /// constant, and generic references in the port/signal widths are substituted
    /// with their values.
    /// </summary>
    /// <remarks>
    /// Intended for the hierarchical resource estimator: when a parent instantiates
    /// a width-parameterized component (e.g. <c>reg_n generic map (N =&gt; 16)</c>),
    /// re-parsing this specialized source yields the child's resource usage at the
    /// <em>instantiated</em> width instead of its default. Returns null when the
    /// entity isn't found in <paramref name="sourceText"/> or declares no generics.
    /// Nested generic expressions (a generic whose value references another
    /// generic) are not resolved to a fixed point and may be left unsubstituted.
    /// </remarks>
    /// <param name="sourceText">The VHDL source containing the entity (its own file).</param>
    /// <param name="entityName">Name of the entity to specialize.</param>
    /// <param name="genericOverrides">Generic name → value (e.g. "N" → "16").</param>
    /// <returns>Standalone specialized source, or null.</returns>
    public static string? SpecializeEntitySource(
        string sourceText, string entityName,
        IReadOnlyDictionary<string, string> genericOverrides)
    {
        if (string.IsNullOrEmpty(sourceText) || string.IsNullOrEmpty(entityName))
            return null;

        EntityRecord? rec = null;
        foreach (var r in ParseEntities(sourceText))
            if (string.Equals(r.Name, entityName, StringComparison.OrdinalIgnoreCase))
            { rec = r; break; }
        if (rec == null) return null;

        // Resolve generics: the entity's declared defaults, then explicit overrides.
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ExtractGenericDefaults(values, rec.EntityText);
        if (genericOverrides != null)
            foreach (var kv in genericOverrides)
                if (!string.IsNullOrWhiteSpace(kv.Key))
                    values[kv.Key.Trim().ToLowerInvariant()] = (kv.Value ?? string.Empty).Trim();
        if (values.Count == 0) return null; // no generics → nothing to specialize

        // Strip the generic clause first (all generics are now constants), THEN
        // substitute references, so we never corrupt the `generic (...)` block.
        string entityText = RemoveGenericClause(rec.EntityText);
        string archText   = rec.ArchitectureText;
        foreach (var kv in values)
        {
            var one = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [kv.Key] = kv.Value };
            entityText = RenameTokens(entityText, one);
            archText   = RenameTokens(archText, one);
        }

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(rec.HeaderText)) sb.AppendLine(rec.HeaderText.Trim());
        sb.AppendLine(entityText.Trim());
        sb.AppendLine(archText.Trim());
        return sb.ToString();
    }

    /// <summary>Removes the <c>generic (...)</c> clause (and its trailing
    /// semicolon) from an entity declaration, balancing nested parens.</summary>
    private static string RemoveGenericClause(string entityText)
    {
        var m = GenericStartRe.Match(entityText);
        if (!m.Success) return entityText;
        int open = m.Index;              // start of "generic ("
        int i = m.Index + m.Length;      // just past "generic ("
        int depth = 1;
        while (i < entityText.Length && depth > 0)
        {
            if (entityText[i] == '(') depth++;
            else if (entityText[i] == ')') depth--;
            i++;
        }
        if (depth != 0) return entityText; // unbalanced — leave untouched
        int j = i;
        while (j < entityText.Length && char.IsWhiteSpace(entityText[j])) j++;
        if (j < entityText.Length && entityText[j] == ';') j++;
        return entityText.Remove(open, j - open);
    }
}
