using System.Text;
using System.Text.RegularExpressions;
using Parser.Models;

namespace Interpreter.Services;

/// <summary>
/// Source-level elaboration of a VHDL design hierarchy. Inlines every component
/// instance under the top unit so the simulator — which doesn't execute child
/// architectures at run time — sees one flat entity that mirrors the modular
/// design's behaviour. The user's files are never modified; the flattened text
/// is handed to the parser.
/// </summary>
/// <remarks>
/// <para>The design units come from <see cref="DesignLibrary"/> (files analysed
/// in <see cref="SourceOrder"/>): each instance is bound by its explicit
/// <c>(arch)</c>, a configuration declaration, a configuration specification, or
/// by default to the entity of the same name and its last analysed architecture
/// (G18–G20). Port-map associations rename child ports to alias signals wired to
/// the parent's actuals; child signals are prefixed with the instance path.
/// Generate statements holding instances or local declarations are unrolled
/// first (<see cref="GenerateUnroller"/>, G57).</para>
/// </remarks>
public sealed partial class SourceElaborator
{
    // ---------- Public entry points ----------

    /// <summary>
    /// Elaborates the unit <paramref name="entry"/> (an entity, or a configuration
    /// of one) of the design in <paramref name="files"/> (analysis order) into one
    /// flattened source.
    /// </summary>
    /// <returns>The flattened source, or null when the parser can take the top's
    /// own file as is (no instance or generate to elaborate, the selected
    /// architecture in the entity's file, no configuration) or the unit is unknown.</returns>
    /// <exception cref="ParserException">A named architecture, configuration or binding cannot be resolved.</exception>
    public static string? ElaborateOrNull(IReadOnlyList<string> files, string entry)
    {
        var lib = DesignLibrary.Scan(files);
        if (lib.ResolveTop(entry) is not var (top, block)) return null;
        var arch = DesignLibrary.SelectArchitecture(top, block?.Architecture);
        if (arch == null) return null;
        if (block == null && arch.File == top.File
            && !VhdlUnitText.HasComponentInstance(arch.Body) && !GenerateUnroller.NeedsUnroll(arch.Body))
            return null;

        var generics = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ExtractGenericDefaults(generics, top.EntityText);
        PackageRegistry.Instance.RegisterUnitArithmetic(string.Empty, top.Context + "\n" + arch.Context);
        var (decl, body, _) = Inline(arch.DeclPart, arch.Body, arch.Specs, block, lib, generics, depth: 0, pathPrefix: string.Empty);

        var sb = new StringBuilder();
        // Library / use clauses (and packages) of the top's files; children's
        // clauses are covered by the top's preamble for the designs we target.
        sb.Append(top.Header);
        if (arch.File != top.File) sb.Append('\n').Append(arch.Header);
        sb.Append('\n');
        sb.Append(top.EntityText);
        sb.Append('\n');
        sb.AppendLine($"architecture {arch.Name} of {top.Name} is");
        sb.Append(decl);
        sb.AppendLine();
        sb.AppendLine("begin");
        sb.Append(body);
        sb.AppendLine();
        sb.AppendLine("end architecture;");
        return sb.ToString();
    }

    /// <summary>
    /// The entity a top unit name designates: the entity itself, or the entity a
    /// configuration <paramref name="entry"/> configures (G20); null when the design
    /// has neither.
    /// </summary>
    public static string? ResolveEntityName(IReadOnlyList<string> files, string entry) =>
        DesignLibrary.Scan(files).ResolveTop(entry)?.Entity.Name;

    /// <summary>
    /// Design units of other files that <paramref name="source"/> needs to parse on
    /// its own (G21): the entity declarations of architectures it holds whose entity
    /// lives elsewhere, and the selected architecture of each entity it declares
    /// without one.
    /// </summary>
    public static (IReadOnlyList<string> Entities, IReadOnlyList<string> Architectures) CompanionUnits(
        IReadOnlyList<string> files, string source)
    {
        var own = DesignLibrary.FromSources([("<source>", source)]);
        var foreignEntities = own.ForeignEntities.ToList();
        var bareEntities = own.Entities.Values.Where(e => e.Architectures.Count == 0).Select(e => e.Name).ToList();
        if (foreignEntities.Count == 0 && bareEntities.Count == 0) return ([], []);   // the common case: no scan

        var lib = DesignLibrary.Scan(files);
        var entities = foreignEntities.Where(lib.Entities.ContainsKey).Select(n => lib.Entities[n].EntityText).ToList();
        var architectures = new List<string>();
        foreach (var name in bareEntities)
            if (lib.Entities.TryGetValue(name, out var rec) && DesignLibrary.SelectArchitecture(rec, null) is { } a)
                architectures.Add(a.Text);
        return (entities, architectures);
    }

    private static readonly Regex ConstantValueRe = new(
        @"\bconstant\s+(?<names>[\w,\s]+?)\s*:[^:;]*:=\s*(?<value>[^;]+);", RegexOptions.IgnoreCase);

    /// <summary>
    /// Resolves names in generate ranges/conditions: <paramref name="generics"/>,
    /// then the constants <paramref name="decl"/> declares, then package constants.
    /// </summary>
    private static Func<string, string?> Lookup(string decl, IReadOnlyDictionary<string, string> generics)
    {
        var constants = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in ConstantValueRe.Matches(decl))
            foreach (var n in m.Groups["names"].Value.Split(','))
                constants[n.Trim()] = m.Groups["value"].Value.Trim();
        return name => generics.TryGetValue(name, out var g) ? g
            : constants.TryGetValue(name, out var c) ? c
            : PackageRegistry.Instance.GetConstant(name)?.Value?.ToString();
    }

    // ---------- Inlining ----------

    // Recursion cap — 3-level and beyond testbenches (tb → dut → sub) inline
    // correctly, but malformed circular references bail after this many hops.
    private const int MaxElaborationDepth = 8;

    /// <summary>What an instance elaborates to: the entity, its architecture and the block configuration that applies inside it.</summary>
    private sealed record Resolved(EntityRecord Entity, ArchRecord Arch, BlockConfig? Block, Binding? Binding = null);

    /// <summary>
    /// Binds instance <paramref name="im"/>; null when it is left as written (unknown
    /// unit) or bound to <c>open</c> (<paramref name="open"/>).
    /// </summary>
    private static Resolved? Resolve(Match im, BlockConfig specs, BlockConfig? config, DesignLibrary lib, out bool open)
    {
        open = false;
        string label = im.Groups["label"].Value, name = im.Groups["ent"].Value;
        string kind = im.Groups["kind"].Value.ToLowerInvariant();
        string? archName = im.Groups["arch"].Success ? im.Groups["arch"].Value : null;
        BlockConfig? block = null;
        Binding? binding = null;
        string entityName = name;

        if (kind == "configuration")
        {
            if (!lib.TryConfiguration(name, out var configured, out var root)) return null;
            (entityName, archName, block) = (configured.Name, root.Architecture, root);
        }
        else if (kind != "entity")
        {
            var cc = config?.Find(label, name) ?? specs.Find(label, name);
            binding = cc?.Binding;
            if (cc?.Binding is { Open: true }) { open = true; return null; }
            if (cc?.Binding?.Configuration is { } cfgName)
            {
                if (!lib.TryConfiguration(cfgName, out var configured, out var root)) return null;
                (entityName, archName, block) = (configured.Name, root.Architecture, cc.Nested ?? root);
            }
            else
            {
                entityName = cc?.Binding?.Entity ?? name;
                archName = cc?.Binding?.Architecture;
                block = cc?.Nested;
            }
        }
        if (!lib.Entities.TryGetValue(entityName, out var entity))
        {
            // An entity instantiation names its unit: missing is an error (GHDL: unit not
            // found), never a silently empty instance. An unbound component stays open.
            if (kind == "entity")
                throw new ParserException($"entity \"{entityName}\" of instance \"{label}\" not found in library \"work\"", 0, 0, label);
            return null;
        }
        var arch = DesignLibrary.SelectArchitecture(entity, archName ?? block?.Architecture);
        if (arch == null && kind == "entity")
            throw new ParserException($"entity \"{entityName}\" of instance \"{label}\" has no architecture{(archName is null ? "" : $" \"{archName}\"")}", 0, 0, label);
        return arch == null ? null : new Resolved(entity, arch, block, binding);
    }

    /// <summary>
    /// Flattens one architecture (<paramref name="decl"/>/<paramref name="body"/>):
    /// unrolls its generates, then inlines every instance — recursively, so a child
    /// with further instances arrives already flattened (three-level
    /// <c>tb → dut → sub</c>, audit F-G-06). Returns the declarative part, the body
    /// and the local names the generate unrolling added (unprefixed by the caller's
    /// instance path yet).
    /// </summary>
    private static (string Decl, string Body, List<string> NewLocals) Inline(
        string decl, string body, BlockConfig specs, BlockConfig? config,
        DesignLibrary lib, IReadOnlyDictionary<string, string> generics, int depth, string pathPrefix)
    {
        if (depth > MaxElaborationDepth)
        {
            Console.Error.WriteLine($"[SourceElaborator] Recursion depth {depth} exceeded — leaving remaining child instances unresolved.");
            return (decl, body, []);
        }

        var unrolled = GenerateUnroller.Unroll(decl, body, Lookup(decl, generics));
        decl = unrolled.Decl;
        body = unrolled.Body;

        var declSb = new StringBuilder(decl);
        var bodySb = new StringBuilder();
        // Type declarations already in the flattened declarative part (name → text).
        var emittedTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, text, _, _) in TypeDeclarations(decl))
            emittedTypes.TryAdd(name, text);

        int cursor = 0;
        foreach (Match im in VhdlUnitText.InstanceRe.Matches(body))
        {
            bodySb.Append(body, cursor, im.Index - cursor);
            cursor = im.Index + im.Length;

            string label = im.Groups["label"].Value;
            string entName = im.Groups["ent"].Value;
            string portMapRaw = im.Groups["pmap"].Value;
            string genericMapRaw = im.Groups["gmap"].Success ? im.Groups["gmap"].Value : string.Empty;

            // Labelled concurrent statements the instance regex misreads.
            if (VhdlUnitText.IsControlFlowLabel(label, entName)) { bodySb.Append(im.Value); continue; }

            var resolved = Resolve(im, specs, config, lib, out bool open);
            if (open)
            {
                bodySb.AppendLine($"  -- {label} : {entName} bound to open (not elaborated)");
                continue;
            }
            // Unknown unit: left as written, so the parser reports it.
            if (resolved == null) { bodySb.Append(im.Value); continue; }
            var (child, childArch, childBlock, binding) = resolved;
            // A binding with its own maps (G80): entity ⇐ component ⇐ actual.
            if (binding?.PortMap is { } bindingPorts) portMapRaw = ComposeMap(bindingPorts, portMapRaw);
            if (binding?.GenericMap is { } bindingGenerics) genericMapRaw = ComposeMap(bindingGenerics, genericMapRaw, substitute: true);

            // For each port, allocate an alias signal in the parent's decl section.
            // The inlined body references the alias rather than the mapped
            // expression directly, which keeps sensitivity lists, subscript LHS
            // targets and concurrent assignments valid — even when the parent maps
            // a slice or a literal to a child port. `in` aliases are driven from the
            // actual, `out` aliases drive it.
            var rename = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var portMappings = BuildPortMappings(child.Ports, portMapRaw);
            var portInfo = ExtractPortInfo(child.EntityText);

            // F-G-06 sibling-collision fix: the caller's path prefix composes into
            // the alias prefix (`u1__u_scan__foo` / `u2__u_scan__foo`).
            string aliasPrefix = $"{pathPrefix}{label}__";
            // The numeric package the child's own context clause selects (std_logic_signed…).
            PackageRegistry.Instance.RegisterUnitArithmetic(aliasPrefix, child.Context + "\n" + childArch.Context);
            // A formal written with selectors (`p.lo => a`, `v(0) => b`) renames
            // and declares its base port once; each association wires its part.
            // An input left `open` or not associated takes its default (LRM 6.5.6.3): its
            // alias is declared with the default and has no driver; an output left
            // `open` gets its own alias too, so it never drives a parent signal of
            // the same name.
            portMappings.RemoveAll(kv => kv.Value.Equals("open", StringComparison.OrdinalIgnoreCase));
            var associated = portMappings.Select(kv => FormalBase(kv.Key)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unassociated = portInfo.Where(p => !associated.Contains(p.Key))
                .Select(p => p.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            // An `inout` formal is its actual (G53): the child reads the resolved
            // value and its drivers join the actual's, with no copy in either
            // direction (two copies of a resolved signal would latch each other).
            var inoutActuals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in portMappings)
            {
                if (!portInfo.TryGetValue(FormalBase(kv.Key), out var pi) || !pi.IsInout) continue;
                if (!string.Equals(kv.Key, FormalBase(kv.Key), StringComparison.OrdinalIgnoreCase) || !StaticNameRe().IsMatch(kv.Value.Trim()))
                    throw new ParserException($"inout port \"{kv.Key}\" of {label} must be associated with a signal name (\"{kv.Value.Trim()}\" is not supported yet)", 0, 0);
                inoutActuals[kv.Key] = kv.Value.Trim();
            }
            foreach (var kv in portMappings)
                rename[FormalBase(kv.Key)] = inoutActuals.TryGetValue(kv.Key, out var actual) ? actual : aliasPrefix + FormalBase(kv.Key);
            foreach (var formal in unassociated)
                rename[formal] = aliasPrefix + formal;

            // Generic values: the child's defaults, overridden by the generic map
            // (P4: without the defaults the alias declarations would keep
            // unresolved `WIDTH-1` bounds).
            var genericValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            ExtractGenericDefaults(genericValues, child.EntityText);
            ApplyGenericMap(genericValues, child.Generics, genericMapRaw);

            // Flatten the child's own instances and generates first, so the
            // fragments renamed here already have every descendant inlined.
            var (grandDecl, grandBody, childLocals) = Inline(childArch.DeclPart, childArch.Body, childArch.Specs,
                childBlock, lib, genericValues, depth + 1, aliasPrefix);
            foreach (var sig in childArch.LocalSignals.Concat(childLocals))
                rename[sig] = aliasPrefix + sig;

            // Substitute port + signal names in the child's body & decl.
            string childDecl = VhdlUnitText.RenameTokens(grandDecl, rename);
            string childBody = VhdlUnitText.RenameTokens(grandBody, rename);

            // Apply generic substitutions on top.
            foreach (var kv in genericValues)
            {
                childDecl = VhdlUnitText.RenameTokens(childDecl, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    { [kv.Key] = kv.Value });
                childBody = VhdlUnitText.RenameTokens(childBody, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    { [kv.Key] = kv.Value });
            }

            childDecl = DeduplicateTypes(childDecl, ref childBody, emittedTypes, aliasPrefix);

            declSb.AppendLine();
            declSb.AppendLine($"  -- ↓ inlined from component {label} : {entName}");
            // Declaration order = association order (the flattened text must stay stable), then the defaults.
            foreach (var formal in portMappings.Select(kv => FormalBase(kv.Key)).Distinct(StringComparer.OrdinalIgnoreCase).Concat(unassociated))
            {
                if (inoutActuals.ContainsKey(formal)) continue;
                var aliasName = aliasPrefix + formal;
                if (portInfo.TryGetValue(formal, out var info))
                {
                    // P4: port type text may reference the child's generics
                    // (`STD_LOGIC_VECTOR(WIDTH-1 downto 0)`): substitute them so the
                    // parent's parser sees concrete bounds.
                    string typeText = info.TypeText;
                    foreach (var gv in genericValues)
                    {
                        typeText = VhdlUnitText.RenameTokens(typeText,
                            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                                { [gv.Key] = gv.Value });
                    }
                    string init = unassociated.Contains(formal) && info.Default is { } d
                        ? $" := {d}" : "";
                    declSb.AppendLine($"  signal {aliasName} : {typeText}{init};");
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
                if (inoutActuals.ContainsKey(kv.Key)) continue;
                if (!portInfo.TryGetValue(FormalBase(kv.Key), out var info))
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
        }
        // Trailing text after the last instance.
        bodySb.Append(body, cursor, body.Length - cursor);

        return (declSb.ToString(), bodySb.ToString(), unrolled.NewLocals);
    }

    /// <summary>
    /// Builds an ordered mapping of child-port-name → parent-mapped-expression.
    /// Handles both named and positional associations.
    /// </summary>
    private static List<KeyValuePair<string, string>> BuildPortMappings(
        List<string> childPorts,
        string portMapRaw)
    {
        var assocs = VhdlUnitText.SplitBalanced(portMapRaw, ',');
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
        /// <summary>True for an <c>inout</c> port: renamed to its actual, which it reads and drives.</summary>
        public bool   IsInout { get; init; }
        /// <summary>The port's default expression (<c>:= …</c>), or null.</summary>
        public string? Default { get; init; }
    }

    private static Dictionary<string, PortInfo> ExtractPortInfo(string entityText)
    {
        var info = new Dictionary<string, PortInfo>(StringComparer.OrdinalIgnoreCase);
        var portsText = VhdlUnitText.ExtractParenBlockAfter(entityText, VhdlUnitText.PortStartRe);
        if (portsText == null) return info;
        foreach (var clause in VhdlUnitText.SplitBalanced(portsText, ';'))
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
                var (type, @default) = SplitDefault(typeText);
                info[t.ToLowerInvariant()] = new PortInfo
                {
                    TypeText = type,
                    IsOutput = isOutput,
                    IsInout = direction == "inout",
                    Default = @default,
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
        // `type := default` → (type, default or null).
        static (string Type, string? Default) SplitDefault(string typeText)
        {
            int idx = typeText.IndexOf(":=");
            return idx >= 0 ? (typeText[..idx].Trim(), typeText[(idx + 2)..].Trim()) : (typeText.Trim(), null);
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
        string? genericsText = VhdlUnitText.ExtractParenBlockAfter(entityText, VhdlUnitText.GenericStartRe);
        if (genericsText == null) return;
        foreach (var clause in VhdlUnitText.SplitBalanced(genericsText, ';'))
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
        var assocs = VhdlUnitText.SplitBalanced(genericMapRaw, ',');
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

    /// <summary>Port named by a formal that may carry selectors: <c>p</c> for <c>p.lo</c> or <c>p(3)</c>.</summary>
    private static string FormalBase(string formal)
    {
        int cut = formal.IndexOfAny(['.', '(']);
        return (cut < 0 ? formal : formal[..cut]).Trim();
    }

    private static readonly Regex TypeDeclStartRe = new(@"\btype\s+(?<name>\w+)\s+is\s+(?<kind>record\b)?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EndRecordRe = new(@"\bend\s+record\b[^;]*;", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// <c>type … ;</c> declarations of a declarative part: name, normalised text
    /// (lowercase, single spaces) and the span they occupy.
    /// </summary>
    private static IEnumerable<(string Name, string Text, int Start, int Length)> TypeDeclarations(string decl)
    {
        string masked = VhdlText.MaskStringLiterals(VhdlText.BlankLineComments(decl));
        foreach (Match m in TypeDeclStartRe.Matches(masked))
        {
            int end = m.Groups["kind"].Success
                ? EndRecordRe.Match(masked, m.Index) is { Success: true } er ? er.Index + er.Length : -1
                : masked.IndexOf(';', m.Index) + 1;
            if (end <= m.Index) continue;
            string text = Regex.Replace(masked[m.Index..end], @"\s+", " ").Trim().ToLowerInvariant();
            yield return (m.Groups["name"].Value, text, m.Index, end - m.Index);
        }
    }

    /// <summary>
    /// Keeps one copy of each type in the flattened architecture: a child's type
    /// identical to one already emitted is dropped; one with the same name but
    /// different text (e.g. sized by a generic) is renamed with the instance
    /// prefix in the child's declarations and body (fields keep their names).
    /// </summary>
    private static string DeduplicateTypes(string childDecl, ref string childBody,
        Dictionary<string, string> emitted, string aliasPrefix)
    {
        var drop = new List<(int Start, int Length)>();
        var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, text, start, length) in TypeDeclarations(childDecl))
        {
            if (!emitted.TryGetValue(name, out var existing)) emitted[name] = text;
            else if (existing == text) drop.Add((start, length));
            else
            {
                renames[name] = aliasPrefix + name;
                emitted[aliasPrefix + name] = text;
            }
        }
        foreach (var (start, length) in drop.OrderByDescending(d => d.Start))
            childDecl = childDecl.Remove(start, length);
        if (renames.Count > 0)
        {
            childDecl = VhdlUnitText.RenameTokens(childDecl, renames);
            childBody = VhdlUnitText.RenameTokens(childBody, renames);
        }
        return childDecl;
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

        if (!DesignLibrary.FromSources([("<source>", sourceText)]).Entities.TryGetValue(entityName, out var rec))
            return null;

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
        string archText   = DesignLibrary.SelectArchitecture(rec, null)?.Text ?? string.Empty;
        foreach (var kv in values)
        {
            var one = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [kv.Key] = kv.Value };
            entityText = VhdlUnitText.RenameTokens(entityText, one);
            archText   = VhdlUnitText.RenameTokens(archText, one);
        }

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(rec.Header)) sb.AppendLine(rec.Header.Trim());
        sb.AppendLine(entityText.Trim());
        sb.AppendLine(archText.Trim());
        return sb.ToString();
    }

    /// <summary>Removes the <c>generic (...)</c> clause (and its trailing
    /// semicolon) from an entity declaration, balancing nested parens.</summary>
    private static string RemoveGenericClause(string entityText)
    {
        var m = VhdlUnitText.GenericStartRe.Match(entityText);
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

    /// <summary>
    /// The associations of a binding's map (<c>entity formal =&gt; component name</c>) with
    /// each component name replaced by what the instance associates with it, giving
    /// <c>entity formal =&gt; actual</c>. A port the instance leaves out becomes
    /// <c>open</c>; with <paramref name="substitute"/> (generic maps) names inside an
    /// expression are replaced and others kept (the component's default).
    /// </summary>
    private static string ComposeMap(string bindingMap, string instanceMap, bool substitute = false)
    {
        var actuals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in VhdlUnitText.SplitBalanced(instanceMap, ','))
        {
            int arrow = a.IndexOf("=>", StringComparison.Ordinal);
            if (arrow > 0) actuals[a[..arrow].Trim()] = a[(arrow + 2)..].Trim();
        }
        var composed = new List<string>();
        foreach (var a in VhdlUnitText.SplitBalanced(bindingMap, ','))
        {
            int arrow = a.IndexOf("=>", StringComparison.Ordinal);
            if (arrow < 0) continue;
            string formal = a[..arrow].Trim(), local = a[(arrow + 2)..].Trim();
            string actual = substitute
                ? (actuals.Count == 0 ? local : VhdlUnitText.RenameTokens(local, actuals))
                : actuals.TryGetValue(local, out var mapped) ? mapped : "open";
            composed.Add($"{formal} => {actual}");
        }
        return string.Join(", ", composed);
    }

    /// <summary>A signal name, optionally with one static index or range: the actuals an <c>inout</c> formal may take.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^[A-Za-z]\w*(?:\.[A-Za-z]\w*)*(?:\s*\([^()]*\))?$")]
    private static partial System.Text.RegularExpressions.Regex StaticNameRe();
}
