using System.Text.RegularExpressions;
using Parser.Models;

namespace Interpreter.Services;

/// <summary>
/// What a component instance is bound to: an entity (optionally a named
/// architecture), a configuration, or nothing (<c>use open</c>).
/// </summary>
internal sealed record Binding(string? Entity, string? Architecture, string? Configuration, bool Open = false)
{
    /// <summary>The binding's <c>generic map</c> associations (entity generic ⇒ expression over the component's), or null.</summary>
    public string? GenericMap { get; init; }

    /// <summary>The binding's <c>port map</c> associations (entity port ⇒ component port), or null.</summary>
    public string? PortMap { get; init; }
}

/// <summary>A component configuration: its binding and the block configuration applied inside the bound entity.</summary>
internal sealed class ComponentConfig
{
    public Binding? Binding { get; init; }
    public BlockConfig? Nested { get; init; }
}

/// <summary>
/// A block configuration (<c>for arch … end for;</c>) or the configuration
/// specifications of an architecture (<c>for u1 : c use …;</c>): the architecture
/// selected and the bindings of its component instances by label, by
/// <c>others</c> and by <c>all</c>.
/// </summary>
internal sealed class BlockConfig
{
    public string? Architecture { get; init; }
    public Dictionary<string, ComponentConfig> ByLabel { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ComponentConfig> OthersByComponent { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ComponentConfig> AllByComponent { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The configuration of instance <paramref name="label"/> of component <paramref name="component"/>, if any.</summary>
    public ComponentConfig? Find(string label, string component) =>
        ByLabel.GetValueOrDefault(label)
        ?? OthersByComponent.GetValueOrDefault(component)
        ?? AllByComponent.GetValueOrDefault(component);

    /// <summary>Records the configuration for the labels of one <c>for labels : comp</c> clause.</summary>
    public void Add(IEnumerable<string> labels, string component, ComponentConfig config)
    {
        foreach (var raw in labels)
        {
            string label = raw.Trim();
            if (label.Equals("all", StringComparison.OrdinalIgnoreCase)) AllByComponent[component] = config;
            else if (label.Equals("others", StringComparison.OrdinalIgnoreCase)) OthersByComponent[component] = config;
            else ByLabel[label] = config;
        }
    }
}

/// <summary>One architecture body, with the order it was analysed in.</summary>
internal sealed class ArchRecord
{
    public string Name { get; init; } = string.Empty;
    public string File { get; init; } = string.Empty;
    public long Order { get; init; }                          // (file index, offset) in analysis order
    public string Header { get; init; } = string.Empty;       // library/use (and packages) above it in its file
    public string Context { get; init; } = string.Empty;      // its own context clause (since the previous unit's end)
    public string Text { get; init; } = string.Empty;         // `architecture … end …;`
    public string DeclPart { get; init; } = string.Empty;     // between `is` and `begin`, without component declarations / configuration specifications
    public string Body { get; init; } = string.Empty;         // between `begin` and the closing `end`
    public List<string> LocalSignals { get; init; } = new();  // signals + constants of DeclPart (lowercased)
    public BlockConfig Specs { get; init; } = new();          // its configuration specifications
}

/// <summary>One entity declaration and every architecture analysed for it.</summary>
internal sealed class EntityRecord
{
    public string Name { get; init; } = string.Empty;
    public string File { get; init; } = string.Empty;
    public string Header { get; init; } = string.Empty;       // library/use clauses above the entity
    public string Context { get; init; } = string.Empty;      // its own context clause (since the previous unit's end)
    public string EntityText { get; init; } = string.Empty;   // `entity X is … end …;`
    public List<string> Ports { get; init; } = new();         // lowercased
    public List<string> Generics { get; init; } = new();      // lowercased
    public List<ArchRecord> Architectures { get; } = new();
}

/// <summary>A configuration declaration (<c>configuration c of e is for arch … end for; end c;</c>).</summary>
internal sealed class ConfigRecord
{
    public string Name { get; init; } = string.Empty;
    public string Entity { get; init; } = string.Empty;
    public BlockConfig Root { get; init; } = new();
}

/// <summary>
/// The design units of a project as GHDL's work library would hold them after
/// analysing its files in order: entities, every architecture of each (from any
/// file, G21), configuration declarations (G20) and the configuration
/// specifications of each architecture (G19). The single source of the
/// architecture choice: the named one, else the last analysed (G18).
/// </summary>
internal sealed class DesignLibrary
{
    public Dictionary<string, EntityRecord> Entities { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ConfigRecord> Configurations { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Entities named by architectures whose entity was not analysed (in these sources).</summary>
    public HashSet<string> ForeignEntities { get; } = new(StringComparer.OrdinalIgnoreCase);

    private static readonly object CacheLock = new();
    private static (string Key, DesignLibrary Library)? _lastScan;

    /// <summary>
    /// Analyses <paramref name="files"/> in the given order (see <see cref="SourceOrder"/>).
    /// One load asks several times (elaboration, top resolution, companion units of
    /// each file): the last scan is reused while the file list, sizes and write
    /// times are unchanged.
    /// </summary>
    public static DesignLibrary Scan(IReadOnlyList<string> files)
    {
        string key = string.Join('\n', files.Select(f =>
        {
            var info = new FileInfo(f);
            return info.Exists ? $"{f}|{info.Length}|{info.LastWriteTimeUtc.Ticks}" : f;
        }));
        lock (CacheLock)
            if (_lastScan is { } last && last.Key == key) return last.Library;
        var library = Read(files);
        lock (CacheLock) _lastScan = (key, library);
        return library;
    }

    private static DesignLibrary Read(IReadOnlyList<string> files)
    {
        var sources = new List<(string File, string Text)>();
        foreach (var path in files)
        {
            if (!path.EndsWith(".vhd", StringComparison.OrdinalIgnoreCase) &&
                !path.EndsWith(".vhdl", StringComparison.OrdinalIgnoreCase))
                continue;
            // F-G-10: never re-read an elaborator artefact (`_elaborated_<name>.vhd`).
            if (Path.GetFileName(path).StartsWith("_elaborated_", StringComparison.OrdinalIgnoreCase))
                continue;
            try { sources.Add((path, File.ReadAllText(path))); } catch { /* unreadable: skipped */ }
        }
        return FromSources(sources);
    }

    /// <summary>The design units of in-memory sources, analysed in order.</summary>
    public static DesignLibrary FromSources(IReadOnlyList<(string File, string Text)> sources)
    {
        var lib = new DesignLibrary();
        var stripped = sources.Select(s => (s.File, Text: VhdlText.StripLineComments(s.Text))).ToList();
        // Two passes so an architecture finds its entity in any file (G21).
        foreach (var (file, text) in stripped) lib.AddEntities(file, text);
        for (int i = 0; i < stripped.Count; i++) lib.AddArchitectures(stripped[i].File, stripped[i].Text, i);
        foreach (var (_, text) in stripped) lib.AddConfigurations(text);
        return lib;
    }

    /// <summary>
    /// The architecture of <paramref name="entity"/> named <paramref name="name"/>
    /// (case-insensitive), or the last analysed one when no name is given; null
    /// when the entity has none.
    /// </summary>
    /// <exception cref="ParserException">The named architecture does not exist.</exception>
    public static ArchRecord? SelectArchitecture(EntityRecord entity, string? name)
    {
        if (string.IsNullOrEmpty(name)) return entity.Architectures.MaxBy(a => a.Order);
        return entity.Architectures.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ParserException($"architecture \"{name}\" of \"{entity.Name}\" not found", 0, 0);
    }

    /// <summary>
    /// The top unit named <paramref name="entry"/>: an entity (with the block of its
    /// own configuration specifications), or a configuration (its entity and root
    /// block, G20); null when neither exists.
    /// </summary>
    public (EntityRecord Entity, BlockConfig? Block)? ResolveTop(string entry)
    {
        if (Entities.TryGetValue(entry, out var entity)) return (entity, null);
        return TryConfiguration(entry, out var configured, out var root) ? (configured, root) : null;
    }

    /// <summary>The entity configuration <paramref name="name"/> configures and its root block configuration.</summary>
    public bool TryConfiguration(string name, out EntityRecord entity, out BlockConfig root)
    {
        entity = null!;
        root = null!;
        if (!Configurations.TryGetValue(name, out var cfg) || !Entities.TryGetValue(cfg.Entity, out var e)) return false;
        (entity, root) = (e, cfg.Root);
        return true;
    }

    // ---------- Entities ----------

    // Closer forms accepted (IEEE 1076-1993+): `end;`, `end entity;`, `end <name>;`,
    // `end entity <name>;` (GAP-ELAB-001).
    private static readonly Regex EntityRe = new(
        @"^\s*entity\s+(?<name>\w+)\s+is\b(?<body>.*?)\bend\b\s*(?:entity\b\s*)?(?:\k<name>\b\s*)?;",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Multiline);


    /// <summary>
    /// The context clause of the unit starting at <paramref name="index"/>: the text
    /// since the previous unit's closing <c>end …;</c> (a <c>use</c> applies only to
    /// the unit right after it).
    /// </summary>
    private static string ContextBefore(string text, int index)
    {
        var ends = UnitEndRe.Matches(text[..index]);
        return ends.Count == 0 ? text[..index] : text[(ends[^1].Index + ends[^1].Length)..index];
    }

    private static readonly Regex UnitEndRe = new(@"\bend\b[^;]*;", RegexOptions.IgnoreCase);

    private void AddEntities(string file, string text)
    {
        var first = EntityRe.Match(text);
        string header = first.Success ? text[..first.Index] : string.Empty;
        foreach (Match em in EntityRe.Matches(text))
        {
            string body = em.Groups["body"].Value;
            Entities[em.Groups["name"].Value] = new EntityRecord
            {
                Name = em.Groups["name"].Value,
                File = file,
                Header = header,
                Context = ContextBefore(text, em.Index),
                EntityText = em.Value,
                Ports = Names(VhdlUnitText.ExtractParenBlockAfter(body, VhdlUnitText.PortStartRe)),
                Generics = Names(VhdlUnitText.ExtractParenBlockAfter(body, VhdlUnitText.GenericStartRe)),
            };
        }
    }

    private static List<string> Names(string? clauseList) =>
        clauseList == null ? [] : VhdlUnitText.SplitPortNames(clauseList).Select(n => n.ToLowerInvariant()).ToList();

    // ---------- Architectures ----------

    // `architecture <aname> of <ename> is`; the decl/body boundary is resolved by
    // VhdlUnitText.SplitArchitecture (inner begin/end pairs are skipped).
    private static readonly Regex ArchStartRe = new(@"\barchitecture\s+(?<aname>\w+)\s+of\s+(?<ename>\w+)\s+is\b", RegexOptions.IgnoreCase);

    // `is` after the component name is optional since VHDL-93.
    private static readonly Regex ComponentDeclRe = new(
        @"\bcomponent\s+(?<cname>\w+)(?:\s+is)?\b.*?\bend\s+component(?:\s+\k<cname>)?\s*;",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    // A configuration specification: `for (labels|all|others) : comp use <binding>;`.
    internal static readonly Regex ConfigSpecRe = new(
        @"\bfor\s+(?<labels>all|others|\w+(?:\s*,\s*\w+)*)\s*:\s*(?<comp>\w+)\s+use\s+(?<binding>[^;]*);",
        RegexOptions.IgnoreCase);

    private void AddArchitectures(string file, string text, int fileIndex)
    {
        string? header = null;
        LineIndex? lines = null;
        foreach (Match am in ArchStartRe.Matches(text))
        {
            if (!Entities.TryGetValue(am.Groups["ename"].Value, out var entity))
            {
                ForeignEntities.Add(am.Groups["ename"].Value);
                continue;
            }
            if (!VhdlUnitText.SplitArchitecture(text, am.Index + am.Length, out var decl, out var body, out var endIdx))
                continue;
            header ??= entity.File == file ? entity.Header : text[..am.Index];

            lines ??= new LineIndex(text);
            int declLine = lines.LineOf(am.Index + am.Length);
            var specs = new BlockConfig { Architecture = am.Groups["aname"].Value };
            foreach (Match sm in ConfigSpecRe.Matches(decl))
                specs.Add(sm.Groups["labels"].Value.Split(','), sm.Groups["comp"].Value,
                    new ComponentConfig { Binding = ParseBinding(sm.Groups["binding"].Value, declLine + new LineIndex(decl).LineOf(sm.Index) - 1) });
            decl = ConfigSpecRe.Replace(ComponentDeclRe.Replace(decl, ""), "");

            entity.Architectures.Add(new ArchRecord
            {
                Name = am.Groups["aname"].Value,
                File = file,
                Order = ((long)fileIndex << 32) | (uint)am.Index,
                Header = header,
                Context = ContextBefore(text, am.Index),
                Text = text[am.Index..endIdx],
                DeclPart = decl,
                Body = body,
                LocalSignals = VhdlUnitText.DeclaredNames(decl),
                Specs = specs,
            });
        }
    }

    /// <summary>1-based line of a text offset (line starts indexed once, binary search).</summary>
    private sealed class LineIndex
    {
        private readonly List<int> _starts = [0];

        public LineIndex(string text)
        {
            for (int i = 0; i < text.Length; i++) if (text[i] == '\n') _starts.Add(i + 1);
        }

        public int LineOf(int offset)
        {
            int at = _starts.BinarySearch(offset);
            return at >= 0 ? at + 1 : ~at;
        }
    }

    private static readonly Regex EntityBindingRe = new(
        @"^entity\s+(?:\w+\s*\.\s*)?(?<ent>\w+)(?:\s*\(\s*(?<arch>\w+)\s*\))?(?<rest>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex ConfigBindingRe = new(
        @"^configuration\s+(?:\w+\s*\.\s*)?(?<cfg>\w+)(?<rest>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// <summary>The binding indication after <c>use</c>.</summary>
    /// <exception cref="ParserException">A generic/port map in the binding, or an unknown form.</exception>
    private static Binding ParseBinding(string text, int line)
    {
        text = text.Trim();
        string rest;
        Binding binding;
        if (EntityBindingRe.Match(text) is { Success: true } em)
        {
            binding = new Binding(em.Groups["ent"].Value, em.Groups["arch"].Success ? em.Groups["arch"].Value : null, null);
            rest = em.Groups["rest"].Value;
        }
        else if (ConfigBindingRe.Match(text) is { Success: true } cm)
        {
            binding = new Binding(null, null, cm.Groups["cfg"].Value);
            rest = cm.Groups["rest"].Value;
        }
        else if (text.Equals("open", StringComparison.OrdinalIgnoreCase)) return new Binding(null, null, null, Open: true);
        else throw new ParserException($"binding indication \"use {text}\" is not supported yet", line, 0);
        if (string.IsNullOrWhiteSpace(rest)) return binding;
        // `generic map (…)` and `port map (…)` of the binding (G80).
        string? gmap = VhdlUnitText.ExtractParenBlockAfter(rest, VhdlUnitText.GenericMapStartRe);
        string? pmap = VhdlUnitText.ExtractParenBlockAfter(rest, VhdlUnitText.PortMapStartRe);
        if (gmap is null && pmap is null)
            throw new ParserException($"binding indication \"use {text}\" is not supported yet", line, 0);
        return binding with { GenericMap = gmap, PortMap = pmap };
    }

    // ---------- Configuration declarations ----------

    private static readonly Regex ConfigDeclRe = new(@"\bconfiguration\s+(?<name>\w+)\s+of\s+(?<ent>\w+)\s+is\b", RegexOptions.IgnoreCase);
    private static readonly Regex WordRe = new(@"\w+|=>|[():;,.]", RegexOptions.Compiled);

    private void AddConfigurations(string text)
    {
        string masked = VhdlText.MaskStringLiterals(text);
        LineIndex? lines = null;
        foreach (Match cm in ConfigDeclRe.Matches(masked))
        {
            lines ??= new LineIndex(text);
            var tokens = WordRe.Matches(masked, cm.Index + cm.Length).Select(m => (m.Value, Line: lines.LineOf(m.Index))).ToList();
            int i = 0;
            // Use clauses / attribute specifications before the block configuration.
            while (i < tokens.Count && !Is(tokens[i].Value, "for")) i++;
            if (i >= tokens.Count) continue;
            var root = ParseBlock(tokens, ref i);
            Configurations[cm.Groups["name"].Value] = new ConfigRecord
            {
                Name = cm.Groups["name"].Value,
                Entity = cm.Groups["ent"].Value,
                Root = root,
            };
        }
    }

    private static bool Is(string token, string word) => token.Equals(word, StringComparison.OrdinalIgnoreCase);

    /// <summary><c>for arch { use …; | component configuration | block configuration } end for;</c></summary>
    private static BlockConfig ParseBlock(List<(string Value, int Line)> t, ref int i)
    {
        i++; // for
        var block = new BlockConfig { Architecture = t[i++].Value };
        if (i < t.Count && t[i].Value == "(") SkipBalanced(t, ref i);   // generate index specification
        while (i < t.Count && !Is(t[i].Value, "end"))
        {
            if (Is(t[i].Value, "use")) { SkipPast(t, ref i, ";"); continue; }
            if (!Is(t[i].Value, "for")) { i++; continue; }
            if (IsComponentConfig(t, i))
            {
                i++; // for
                var labels = new List<string>();
                while (t[i].Value != ":") { if (t[i].Value != ",") labels.Add(t[i].Value); i++; }
                i++; // :
                string component = t[i++].Value;
                Binding? binding = null;
                if (Is(t[i].Value, "use"))
                {
                    int line = t[i].Line;
                    i++;
                    var words = new List<string>();
                    while (t[i].Value != ";") words.Add(t[i++].Value);
                    i++; // ;
                    binding = ParseBinding(JoinBinding(words), line);
                }
                var nested = Is(t[i].Value, "for") ? ParseBlock(t, ref i) : null;
                block.Add(labels, component, new ComponentConfig { Binding = binding, Nested = nested });
            }
            else ParseBlock(t, ref i);   // block configuration of a block/generate statement: not modelled
            ExpectEndFor(t, ref i);
        }
        ExpectEndFor(t, ref i);
        return block;
    }

    /// <summary>A <c>for</c> that names <c>labels : component</c> (a component configuration).</summary>
    private static bool IsComponentConfig(List<(string Value, int Line)> t, int i)
    {
        for (int j = i + 1; j < t.Count; j++)
        {
            if (t[j].Value == ":") return true;
            if (t[j].Value is not ("," or "(" or ")") && !(char.IsLetterOrDigit(t[j].Value[0]) || t[j].Value[0] == '_')) return false;
            if (Is(t[j].Value, "for") || Is(t[j].Value, "end") || Is(t[j].Value, "use")) return false;
        }
        return false;
    }

    private static string JoinBinding(List<string> words) =>
        string.Join(' ', words).Replace(" . ", ".").Replace(" ( ", "(").Replace(" )", ")");

    private static void ExpectEndFor(List<(string Value, int Line)> t, ref int i)
    {
        if (i < t.Count && Is(t[i].Value, "end")) i++;
        if (i < t.Count && Is(t[i].Value, "for")) i++;
        if (i < t.Count && t[i].Value == ";") i++;
    }

    private static void SkipPast(List<(string Value, int Line)> t, ref int i, string token)
    {
        while (i < t.Count && t[i].Value != token) i++;
        i++;
    }

    private static void SkipBalanced(List<(string Value, int Line)> t, ref int i)
    {
        int depth = 0;
        do
        {
            if (t[i].Value == "(") depth++;
            else if (t[i].Value == ")") depth--;
            i++;
        } while (i < t.Count && depth > 0);
    }
}
