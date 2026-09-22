using System.Text.RegularExpressions;
using Interpreter.Models;
using Parser.Models;

namespace Interpreter.Services;

/// <summary>
/// Regex-based file scanner that walks VHDL package declarations and
/// registers each <c>subtype NAME is BASE(HI downto LO);</c> in
/// <see cref="PackageRegistry.Instance"/>. Enables cross-file resolution
/// of package-declared subtypes such as <c>q16_t</c> when a downstream
/// entity file uses them in port declarations.
/// </summary>
/// <remarks>
/// Session-6 hardening of the F-G-04 fallback (2026-07-09). Prior to
/// this scanner, package subtypes were invisible to <c>VariableSyntetizer</c>
/// because <c>EntityRegistry.ScanFile</c> only looks for entity blocks and
/// <c>Program.ParsePackageConstants</c> only extracts constants (not
/// subtypes). Ports typed with a subtype fell into the "unknown type"
/// branch and were registered under a best-effort <c>STD_LOGIC_VECTOR</c>
/// placeholder — that let elaboration finish, but arithmetic on those
/// ports read as zero because signed / unsigned semantics weren't
/// propagated. This scanner closes that gap for the common
/// <c>subtype NAME is (signed|unsigned|std_logic_vector) (HI downto LO);</c>
/// pattern.
/// </remarks>
public static class PackageSubtypeScanner
{
    // package X is ... end (package)? X;  — greedy body, single-line
    // Comments in the source are already stripped by the caller if needed;
    // we operate on the raw file text with re-multiline options set.
    private static readonly Regex PackageRe = new(
        @"\bpackage\s+(?<name>\w+)\s+is\b(?<body>[\s\S]*?)\bend\s+(?:package\s+)?\k<name>?\s*;",
        RegexOptions.IgnoreCase | RegexOptions.Multiline);

    // subtype NAME is (signed|unsigned|std_logic_vector|bit_vector) (HI downto LO);
    // Bounds allow simple arithmetic: literals, constants, and `NAME±LITERAL`.
    // Non-greedy on the operand halves so the outer `)` boundary catches.
    private static readonly Regex SubtypeRe = new(
        @"\bsubtype\s+(?<name>\w+)\s+is\s+(?<base>signed|unsigned|std_logic_vector|bit_vector|std_ulogic_vector)\s*\(\s*(?<hi>[\w\s\+\-]+?)\s+(?<dir>downto|to)\s+(?<lo>[\w\s\+\-]+?)\s*\)\s*;",
        RegexOptions.IgnoreCase | RegexOptions.Multiline);

    // constant NAME : TYPE := VALUE; — extracts VALUE as int when possible.
    // Only integer-literal RHS handled; range/vector literals ignored.
    private static readonly Regex ConstantRe = new(
        @"\bconstant\s+(?<name>\w+)\s*:\s*\w+(?:\s*\([^)]*\))?\s*:=\s*(?<val>[^;]+?)\s*;",
        RegexOptions.IgnoreCase | RegexOptions.Multiline);

    /// <summary>
    /// Scans a VHDL file for package-declared subtypes and registers each
    /// resolvable one in <see cref="PackageRegistry.Instance"/>. Silently
    /// skips unreadable files, missing packages, and unresolvable bounds
    /// (e.g. constant-expression widths that would need the constant
    /// table already in place).
    /// </summary>
    public static void ScanFile(string filePath)
    {
        if (!File.Exists(filePath)) return;

        string src;
        try { src = File.ReadAllText(filePath); }
        catch { return; }

        // Strip line-comments -- they can hide inside package bodies and
        // confuse the greedy match.
        src = Regex.Replace(src, @"--[^\n]*", string.Empty);

        var registry = PackageRegistry.Instance;

        foreach (Match pkg in PackageRe.Matches(src))
        {
            string pkgName = pkg.Groups["name"].Value;
            string body    = pkg.Groups["body"].Value;

            // Ensure the package exists in the registry — RegisterPackage /
            // RegisterConstant will lazily create it, but subtypes are
            // attached to PackageDefinition.Types which needs the package
            // record itself present.
            var pkgDef = registry.GetPackage(pkgName);
            if (pkgDef == null)
            {
                pkgDef = new PackageDefinition { Name = pkgName };
                registry.TryRegisterPackage(pkgDef);
            }

            // Pass 1: constants — must precede subtype resolution so a
            // `subtype q16_t is signed(WORD_W-1 downto 0);` can look up
            // `WORD_W` before computing the range width. Also captures
            // string-literal constants (`constant OP_ADD : opcode_t := "00001";`)
            // so downstream `case op is when OP_ADD =>` arms match at
            // simulation time — otherwise the case falls through to
            // `when others =>` and every arithmetic op returns zero.
            foreach (Match cn in ConstantRe.Matches(body))
            {
                string cName = cn.Groups["name"].Value;
                string cVal  = cn.Groups["val"].Value.Trim();

                // Skip if already registered — a later file's package
                // body shouldn't overwrite a canonical constant.
                if (pkgDef.Constants.Any(k => string.Equals(k.Name, cName, StringComparison.OrdinalIgnoreCase)))
                    continue;

                object? valObj = null;
                string cleaned = cVal.Replace("_", string.Empty);
                if (int.TryParse(cleaned, out int cInt))
                {
                    valObj = cInt;
                }
                else if (cVal.Length >= 2 && cVal[0] == '"' && cVal[^1] == '"')
                {
                    // Bit-string literal — strip outer quotes, keep the raw
                    // bit pattern so DeltaCycleEngine's case matcher can
                    // string-compare against it.
                    valObj = cVal.Substring(1, cVal.Length - 2);
                }
                else if (cVal.Length == 3 && cVal[0] == '\'' && cVal[^1] == '\'')
                {
                    // Single-bit literal — keep the bare character.
                    valObj = cVal.Substring(1, 1);
                }

                if (valObj != null)
                {
                    registry.RegisterConstant(new Variable { Name = cName, Value = valObj }, pkgName);
                }
            }

            // Pass 2: subtypes.
            foreach (Match st in SubtypeRe.Matches(body))
            {
                string subName = st.Groups["name"].Value;
                string baseTy  = st.Groups["base"].Value.ToLowerInvariant();
                string hiStr   = st.Groups["hi"].Value;
                string loStr   = st.Groups["lo"].Value;
                string dir     = st.Groups["dir"].Value.ToLowerInvariant();

                if (!TryResolveBound(hiStr, pkgDef, out int hi)) continue;
                if (!TryResolveBound(loStr, pkgDef, out int lo)) continue;

                int high = dir == "downto" ? hi : lo;
                int low  = dir == "downto" ? lo : hi;
                int width = high - low + 1;
                if (width <= 0) continue;

                var def = new TypeDefinition
                {
                    Name          = subName,
                    Kind          = TypeKind.Subtype,
                    BaseType      = Constants.DATATYPES.STD_LOGIC_VECTOR,
                    BaseTypeName  = baseTy,
                    SubtypeRange  = (high, low),
                    IsDownto      = (dir == "downto"),
                };

                pkgDef.Types.TryRegister(def);
#if DEBUG
                Console.WriteLine($"[PackageSubtypeScanner] {pkgName}.{subName} = {baseTy}({high} downto {low})  width={width}");
#endif
            }
        }
    }

    /// <summary>
    /// Resolves a range-bound expression. Handles integer literals,
    /// simple constant references, and one level of arithmetic
    /// (<c>WORD_W - 1</c>, <c>DEPTH + 1</c>). Anything more complex
    /// (parentheses, chained ops) falls out to "unresolved" and its
    /// subtype is skipped.
    /// </summary>
    private static bool TryResolveBound(string expr, PackageDefinition pkg, out int value)
    {
        string tok = expr.Replace("_", string.Empty).Trim();

        if (int.TryParse(tok, out value)) return true;

        // Simple arithmetic: NAME(+|-)LITERAL or LITERAL(+|-)NAME.
        var m = Regex.Match(tok, @"^\s*(?<a>\w+)\s*(?<op>[+\-])\s*(?<b>\w+)\s*$");
        if (m.Success)
        {
            if (TryResolveTerm(m.Groups["a"].Value, pkg, out int a)
             && TryResolveTerm(m.Groups["b"].Value, pkg, out int b))
            {
                value = m.Groups["op"].Value == "+" ? a + b : a - b;
                return true;
            }
        }

        return TryResolveTerm(tok, pkg, out value);
    }

    /// <summary>
    /// Resolves a bare term to an integer — literal or named constant.
    /// </summary>
    private static bool TryResolveTerm(string term, PackageDefinition pkg, out int value)
    {
        term = term.Trim();
        if (int.TryParse(term, out value)) return true;

        var local = pkg.Constants.FirstOrDefault(c =>
            string.Equals(c.Name, term, StringComparison.OrdinalIgnoreCase));
        if (local?.Value is int li) { value = li; return true; }
        if (local?.Value is string ls && int.TryParse(ls, out int lp)) { value = lp; return true; }

        var global = PackageRegistry.Instance.GetConstant(term);
        if (global?.Value is int gi) { value = gi; return true; }
        if (global?.Value is string gs && int.TryParse(gs, out int gp)) { value = gp; return true; }

        value = 0;
        return false;
    }

    /// <summary>
    /// Looks up a subtype by name across every registered package.
    /// Case-insensitive. Returns null if no match.
    /// </summary>
    public static TypeDefinition? LookupGlobally(string subtypeName)
    {
        if (string.IsNullOrWhiteSpace(subtypeName)) return null;
        foreach (var pkg in PackageRegistry.Instance.Packages.Values)
        {
            if (pkg.Types.TryGet(subtypeName, out var def) && def != null)
                return def;
        }
        return null;
    }
}
