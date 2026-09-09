using System.Diagnostics;
using System.Runtime.CompilerServices;
using Parser.Models;
using Parser.Repositories;

namespace Interpreter.Services;

/// <summary>
/// F-G-08 pass 4 (2026-07-17): env-var-gated parse-time diagnostic sink,
/// plus the shared iteration-guard used by every <c>Skip*</c> / <c>Parse*</c>
/// while-loop in the synthesis descent to convert an indefinite hang into a
/// bounded <see cref="ParserSkipOverflowException"/>.
///
/// The sink is silent unless <c>KMILA_PARSER_STEP_TRACE=1</c>. The iteration
/// guard is always active and throws when the cap is exceeded (regardless of
/// env-var state) so a pathological input can never wedge the parser
/// indefinitely.
/// </summary>
public static class ParseTrace
{
    /// <summary>Shared iteration ceiling for every capped Skip/Parse loop.</summary>
    public const int MAX_SKIP_ITER = 100_000;

    private static readonly Stopwatch _sw = Stopwatch.StartNew();
    private static readonly bool _enabled =
        Environment.GetEnvironmentVariable("KMILA_PARSER_STEP_TRACE") == "1";

    /// <summary>True when the parse trace should emit stderr lines.</summary>
    public static bool Enabled => _enabled;

    /// <summary>Milliseconds elapsed since process start (Stopwatch-based).</summary>
    public static long ElapsedMs => _sw.ElapsedMilliseconds;

    /// <summary>
    /// Emit a stderr progress line. Cheap when disabled — callers should
    /// still guard with <see cref="Enabled"/> to avoid the format-string
    /// allocation on the hot path.
    /// </summary>
    public static void Log(string tag, int sourceLine = 0,
                           [CallerMemberName] string caller = "")
    {
        if (!_enabled) return;
        Console.Error.WriteLine(
            $"[kmila-parse t={_sw.ElapsedMilliseconds}ms line={sourceLine}] {caller}: {tag}");
    }

    /// <summary>
    /// Increment <paramref name="iter"/> and, if it exceeds
    /// <see cref="MAX_SKIP_ITER"/>, throw a
    /// <see cref="ParserSkipOverflowException"/> tagged with the caller's
    /// method name plus the current token's position + value for post-mortem
    /// analysis. Also honours <paramref name="ct"/> so a stuck loop can be
    /// interrupted end-to-end from <c>KMILA_SIM_WALL_TIMEOUT_MS</c>.
    /// </summary>
    /// <param name="iter">Loop's iteration counter (passed by ref).</param>
    /// <param name="streamer">Current token stream, for the diagnostic snapshot.</param>
    /// <param name="ct">Cancellation token to honour every iteration.</param>
    /// <param name="cap">Override the shared cap for a specific loop.</param>
    /// <param name="caller">Auto-filled by <see cref="CallerMemberNameAttribute"/>.</param>
    public static void Step(ref int iter, TokenStream? streamer, CancellationToken ct,
                            int cap = MAX_SKIP_ITER,
                            [CallerMemberName] string caller = "")
    {
        ct.ThrowIfCancellationRequested();
        if (++iter > cap)
        {
            var tok = streamer?.Current;
            int line = tok != null ? tok.Line + 1 : 0;
            string ctx = tok != null
                ? $"'{tok.Value}' ({tok.Type}) at Line {line}:{tok.Col + 1}"
                : "<end of stream>";
            throw new ParserSkipOverflowException(caller, cap, line, ctx);
        }
    }

    /// <summary>
    /// F-G-08 pass 4 (2026-07-17): consume tokens through the next <c>;</c>.
    /// Stops early at <c>end</c> as a safety net so a missing semicolon
    /// doesn't drift into the next statement. Mirrors
    /// <c>Process.SkipToSemicolonInclusive</c> so nested body dispatchers
    /// (IfStructure / CaseStructure / WhileLoop / ForLoop / SimpleLoop) can
    /// reuse the same testbench-statement skip. Without this, a
    /// <c>wait for … ns;</c> or <c>report … ;</c> nested inside one of those
    /// bodies mis-dispatches its <c>for</c> as a for-loop header and the
    /// range parser then swallows unbounded tokens.
    /// </summary>
    public static void SkipToSemicolonInclusive(TokenStream streamer)
    {
        int iter = 0;
        // Track whether we're currently inside a string literal — the
        // tokenizer splits `"TB: end of stimulus"` across whitespace into
        // fragments `"TB:`, `end`, `of`, `stimulus"`, so a naive `end`
        // early-exit stops mid-string when a report/assert message contains
        // the word "end". A fragment with an unmatched `"` toggles the state.
        bool insideString = false;
        while (streamer.Current != null)
        {
            string v = streamer.Current.Value ?? string.Empty;
            if (!insideString)
            {
                if (v == ";") break;
                if (v.ToLower() == "end") break;
            }
            // Toggle insideString when we cross an unmatched double-quote.
            int quoteCount = 0;
            foreach (var c in v) if (c == '"') quoteCount++;
            if (quoteCount % 2 == 1) insideString = !insideString;
            Step(ref iter, streamer, CancellationToken.None);
            streamer.MoveNext();
        }
        if (streamer.Current?.Value == ";")
        {
            streamer.MoveNext();
        }
    }

    /// <summary>
    /// F-G-08 pass 4 (2026-07-17): consume a <c>wait</c> statement:
    /// <c>wait for X ns;</c>, <c>wait until cond;</c>, <c>wait on sig;</c>,
    /// or bare <c>wait;</c>. Testbench-only; not synthesizable.
    /// </summary>
    public static void SkipWaitStatement(TokenStream streamer)
    {
        streamer.MoveNext(); // consume 'wait'
        SkipToSemicolonInclusive(streamer);
    }

    /// <summary>
    /// F-G-15 (2026-07-18): shared render for VHDL <c>report</c> expression
    /// tokens. Used by <see cref="Interpreter.Repositories.ReportStatement"/>
    /// (top-level reports) and
    /// <see cref="Interpreter.Repositories.ProcedureCallStatement"/> (reports
    /// inlined from a procedure body) so both paths produce identical output.
    ///
    /// Rendering:
    /// <list type="bullet">
    /// <item><description>String literals lose their quotes.</description></item>
    /// <item><description>Fragmented string literals (the tokenizer splits
    /// <c>"a b c"</c> on whitespace into <c>"a</c>, <c>b</c>, <c>c"</c>) are
    /// rebuilt.</description></item>
    /// <item><description>Bare identifiers try variable-scope substitution.</description></item>
    /// <item><description><c>to_string(&lt;name&gt;)</c> and
    /// <c>to_string(&lt;name&gt;(&lt;hi&gt; downto|to &lt;lo&gt;))</c> are
    /// evaluated at report time — the referenced signal's current
    /// <see cref="Variable.Value"/> is sliced and rendered as its bit
    /// string. Kmila stores <c>std_logic_vector</c> values as strings with
    /// index 0 being the leftmost (MSB) character.</description></item>
    /// <item><description>The <c>&amp;</c> concat operator is dropped.</description></item>
    /// <item><description>Everything else passes through with single-space
    /// separation.</description></item>
    /// </list>
    /// </summary>
    public static string RenderReportTokens(List<string> tokens, HashSet<Variable> variables)
    {
        var sb = new System.Text.StringBuilder();
        bool needSpace = false;
        void Append(string s)
        {
            if (s.Length == 0) return;
            if (needSpace) sb.Append(' ');
            sb.Append(s);
            needSpace = true;
        }

        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t == "&") continue;

            // F-G-15+ (2026-07-18): to_string / to_hstring / to_bstring
            // formatter calls. Same pattern matcher, different output
            // format depending on the caller name. `to_string` renders
            // integers as decimals, booleans as lowercase VHDL truth,
            // std_logic_vector as bit-string. `to_hstring` renders vectors
            // as hex (4 bits per nibble). `to_bstring` is an explicit
            // alias for the vector-as-binary path. Ignoring case per LRM.
            var lower = t.ToLowerInvariant();
            if ((lower == "to_string" || lower == "to_hstring" || lower == "to_bstring") &&
                TryEvalTypedString(tokens, i, variables, lower, out var evaluated, out int consumed))
            {
                Append(evaluated);
                i += consumed - 1; // -1 because the for++ will add 1 more
                continue;
            }

            // Single-token full string literal: `"whole thing"`.
            if (t.Length >= 2 && t.StartsWith('"') && t.EndsWith('"'))
            {
                Append(t.Substring(1, t.Length - 2));
                continue;
            }

            // Fragmented string literal opener: `"foo` with no matching close.
            if (t.StartsWith('"'))
            {
                var frag = new System.Text.StringBuilder(t.Substring(1));
                string prev = t.Substring(1);
                while (++i < tokens.Count && !tokens[i].EndsWith('"'))
                {
                    var cur = tokens[i];
                    // F-G-16-polish (2026-07-18): the tokenizer splits string
                    // content on special chars, so `========` in the source
                    // arrives as 8 separate `=` tokens. Naive
                    // space-joining renders as `= = = = = = = =` which is
                    // unreadable. When a run of consecutive single-char
                    // tokens all equal the SAME char, join without spaces
                    // so the source `====` shape is preserved. Multi-char
                    // tokens (identifiers) and heterogeneous runs keep the
                    // space separator.
                    if (cur.Length == 1 && prev.Length >= 1 && prev[^1] == cur[0])
                        frag.Append(cur);
                    else
                        frag.Append(' ').Append(cur);
                    prev = cur;
                }
                if (i < tokens.Count)
                {
                    var last = tokens[i];
                    var lastContent = last.Substring(0, last.Length - 1);
                    if (lastContent.Length == 1 && prev.Length >= 1 && prev[^1] == lastContent[0])
                        frag.Append(lastContent);
                    else if (lastContent.Length > 0)
                        frag.Append(' ').Append(lastContent);
                }
                Append(frag.ToString());
                continue;
            }

            // Bare identifier: try variable-scope substitution.
            var v = variables.FirstOrDefault(x =>
                x.Name.Equals(t, StringComparison.OrdinalIgnoreCase));
            if (v?.Value is string vs) { Append(vs); continue; }
            if (v?.Value != null)      { Append(v.Value.ToString() ?? string.Empty); continue; }

            Append(t);
        }
        return sb.ToString();
    }

    /// <summary>
    /// F-G-15 (2026-07-18): tries to match and evaluate a
    /// <c>to_string(&lt;name&gt;)</c> or
    /// <c>to_string(&lt;name&gt;(&lt;hi&gt; downto|to &lt;lo&gt;))</c>
    /// pattern starting at <paramref name="startIdx"/> in
    /// <paramref name="tokens"/>. On success sets
    /// <paramref name="result"/> to the rendered bit string and
    /// <paramref name="consumed"/> to the number of tokens matched
    /// (including <c>to_string</c> itself) and returns true. On failure
    /// returns false so the caller falls back to source-text rendering.
    /// </summary>
    private static bool TryEvalTypedString(List<string> tokens, int startIdx,
                                           HashSet<Variable> variables,
                                           string caller,
                                           out string result, out int consumed)
    {
        result = string.Empty;
        consumed = 0;
        // Minimum: `<caller>` `(` `<name>` `)` → 4 tokens.
        if (startIdx + 3 >= tokens.Count) return false;
        if (tokens[startIdx + 1] != "(") return false;

        var sigName = tokens[startIdx + 2];
        var v = variables.FirstOrDefault(x =>
            x.Name.Equals(sigName, StringComparison.OrdinalIgnoreCase));
        if (v is null) return false;

        // Form 1: <caller>(<name>) — no slice.
        if (tokens[startIdx + 3] == ")")
        {
            result = FormatVariable(v, wholeBitstring: null, caller);
            consumed = 4;
            return true;
        }

        // Form 2: <caller>(<name>(<hi> downto|to <lo>)) — 9 tokens.
        //   caller ( name ( hi dir lo ) )
        //     0    1  2   3  4  5  6 7 8
        if (startIdx + 8 >= tokens.Count) return false;
        if (tokens[startIdx + 3] != "(") return false;
        if (tokens[startIdx + 7] != ")") return false;
        if (tokens[startIdx + 8] != ")") return false;

        var dir = tokens[startIdx + 5].ToLowerInvariant();
        if (dir != "downto" && dir != "to") return false;

        if (!int.TryParse(tokens[startIdx + 4], out int hi)) return false;
        if (!int.TryParse(tokens[startIdx + 6], out int lo)) return false;

        // Slice extraction only makes sense on a string-valued (bit vector)
        // Variable. In VHDL string storage: index 0 is the leftmost (MSB)
        // char. For a `signal(size-1 downto 0)`, char at raw[0] is the
        // MSB (index size-1). Slicing (hi downto lo) yields
        // raw[size-1-hi .. size-1-lo] inclusive.
        var raw = v.Value?.ToString() ?? string.Empty;
        int size = raw.Length;
        int high = dir == "downto" ? hi : lo;
        int low  = dir == "downto" ? lo : hi;
        if (high < low || high >= size || low < 0) return false;
        int startChar = size - 1 - high;
        int lengthChars = high - low + 1;
        string sliceBits = raw.Substring(startChar, lengthChars);

        result = FormatVariable(v, wholeBitstring: sliceBits, caller);
        consumed = 9;
        return true;
    }

    /// <summary>
    /// F-G-15+ (2026-07-18): renders a <see cref="Variable"/>'s current
    /// value according to the caller (<c>to_string</c>, <c>to_hstring</c>,
    /// <c>to_bstring</c>) and VHDL type conventions.
    ///
    /// <para>When <paramref name="wholeBitstring"/> is non-null, treat the
    /// input as already-extracted bit-vector text (from a slice) — for
    /// scalar Variable types the caller should pass null and this method
    /// stringifies <c>v.Value</c> directly.</para>
    ///
    /// Conventions:
    /// <list type="bullet">
    /// <item><description><c>to_string</c> on a bit vector → binary string
    /// as-is. On an integer / natural / positive → decimal. On a boolean →
    /// lowercase <c>"true"</c> / <c>"false"</c> per LRM.</description></item>
    /// <item><description><c>to_hstring</c> on a bit vector → hex digits
    /// (pad to a multiple of 4 with leading zeros so the MSB nibble
    /// includes all real bits). On integer → hex.</description></item>
    /// <item><description><c>to_bstring</c> — explicit binary; same as
    /// <c>to_string</c> for vectors, forces binary for integers.</description></item>
    /// </list>
    /// </summary>
    private static string FormatVariable(Variable v, string? wholeBitstring, string caller)
    {
        // If we have an explicit bit-string (from a slice), format that
        // as bits or hex depending on caller; else look at Value's type.
        string? bits = wholeBitstring;
        if (bits == null && v.Value is string s && LooksLikeBitString(s))
            bits = s;

        if (bits != null)
        {
            return caller switch
            {
                "to_hstring" => BitsToHex(bits),
                _            => bits,   // to_string / to_bstring — binary
            };
        }

        // Scalar / non-vector Variable.
        object? val = v.Value;
        if (val is bool b)
        {
            return b ? "true" : "false";   // VHDL LRM lowercase truth
        }
        if (val is int i)
        {
            return caller == "to_hstring"
                ? Convert.ToString(i, 16).ToUpperInvariant()
                : i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        if (val is long l)
        {
            return caller == "to_hstring"
                ? Convert.ToString(l, 16).ToUpperInvariant()
                : l.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        // Fallback: string-ify whatever it is.
        return val?.ToString() ?? string.Empty;
    }

    private static bool LooksLikeBitString(string s)
    {
        if (s.Length == 0) return false;
        foreach (var c in s)
            if (c != '0' && c != '1' && c != 'U' && c != 'X' && c != 'Z' && c != 'W' && c != 'L' && c != 'H' && c != '-')
                return false;
        return true;
    }

    private static string BitsToHex(string bits)
    {
        // Pad-left to nibble boundary so the MSB nibble carries all
        // meaningful bits (e.g. 5 bits `10101` → `10101` becomes `00010101`
        // → hex `15`).
        int padded = (bits.Length + 3) / 4 * 4;
        string s = bits.PadLeft(padded, '0');
        var hex = new System.Text.StringBuilder(s.Length / 4);
        for (int i = 0; i < s.Length; i += 4)
        {
            int nibble = 0;
            for (int j = 0; j < 4; j++)
            {
                char c = s[i + j];
                nibble <<= 1;
                // Treat any metavalue char as 0 for the hex render — an
                // arguable choice, but a printable one. Real VHDL would
                // emit an `X` or `?` here; keep it simple for now.
                if (c == '1' || c == 'H') nibble |= 1;
            }
            hex.Append("0123456789ABCDEF"[nibble]);
        }
        return hex.ToString();
    }
}
