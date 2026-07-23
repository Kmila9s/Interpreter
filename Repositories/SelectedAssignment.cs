using System.Text;
using Interpreter.Interfaces;
using Interpreter.Models;
using Interpreter.Services;
using Parser.Models;
using Parser.Repositories;
using static Parser.Models.Constants.TYPE;

namespace Interpreter.Repositories;

/// <summary>
/// Represents a choice in a selected assignment (with...select).
/// </summary>
public class SelectionChoice
{
    /// <summary>
    /// Gets or sets the value expression for this choice.
    /// </summary>
    public string ValueExpression { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the choice values that select this branch.
    /// </summary>
    public List<string> Choices { get; set; } = new();

    /// <summary>
    /// Gets or sets whether this is the 'others' (default) branch.
    /// </summary>
    public bool IsOthers { get; set; } = false;
}

/// <summary>
/// Represents and executes a VHDL selected signal assignment (with...select).
/// </summary>
/// <remarks>
/// <para><strong>Supported VHDL Syntax:</strong></para>
/// <code>
/// with selector select
///     target &lt;= value1 when "00",
///               value2 when "01" | "10",
///               default when others;
/// </code>
/// <para><strong>Execution:</strong></para>
/// <para>
/// The selector expression is evaluated and compared against each choice.
/// When a match is found, the corresponding value is assigned to the target.
/// The 'others' clause provides a default value.
/// </para>
/// </remarks>
public class SelectedAssignment : ISControl
{
    /// <summary>The variables in scope during selector and value evaluation.</summary>
    private readonly HashSet<Variable> _variables;
    /// <summary>The token stream used for parsing the selected assignment, or null if constructed explicitly.</summary>
    private readonly TokenStream? _streamer;

    /// <summary>
    /// Gets the selector expression.
    /// </summary>
    public string SelectorExpression { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the target signal name.
    /// </summary>
    public string TargetName { get; private set; } = string.Empty;

    /// <summary>
    /// Integer bit index when the selected assignment targets a single
    /// element of a vector signal (e.g. <c>y(0) &lt;= …</c>). Null when
    /// the assignment targets the whole signal. Only literal-integer
    /// indices are captured; runtime indices fall through to whole-signal.
    /// Bench bug #5 / #9 fix (2026-06-03).
    /// </summary>
    public int? TargetBitIndex { get; private set; }

    /// <summary>
    /// Gets the target variable.
    /// </summary>
    public Variable? Target { get; private set; }

    /// <summary>
    /// Gets the list of selection choices.
    /// </summary>
    public List<SelectionChoice> Choices { get; private set; } = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="SelectedAssignment"/> class.
    /// </summary>
    /// <param name="variables">The variables in scope.</param>
    /// <param name="streamer">The token stream for parsing.</param>
    public SelectedAssignment(HashSet<Variable> variables, TokenStream streamer)
    {
        _variables = variables;
        _streamer = streamer;
    }

    /// <summary>
    /// Initializes a new instance with explicit values.
    /// </summary>
    /// <param name="variables">The variables in scope.</param>
    /// <param name="selectorExpression">The selector expression.</param>
    /// <param name="targetName">The target signal name.</param>
    public SelectedAssignment(HashSet<Variable> variables, string selectorExpression, string targetName)
    {
        _variables = variables;
        _streamer = null;
        SelectorExpression = selectorExpression;
        TargetName = targetName;
        Target = variables.FirstOrDefault(v => v.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Executes the selected assignment asynchronously.
    /// </summary>
    public Task ExecuteAsync() => ExecuteAsync(scheduler: null);

    /// <summary>
    /// Scheduler-aware execution: when a <paramref name="scheduler"/> is
    /// supplied, the target update is routed through it (proper VHDL
    /// delta-cycle semantics + SignalHistory recording). Otherwise it
    /// falls back to immediate assignment, matching the legacy behavior
    /// kept for non-engine callers. Bench Phase E bug #4 fix.
    /// </summary>
    public Task ExecuteAsync(Services.SignalScheduler? scheduler)
    {
        if (Target == null || string.IsNullOrEmpty(SelectorExpression) || Choices.Count == 0)
            return Task.CompletedTask;

        // Evaluate selector
        Operation selectorOp = new(SelectorExpression, _variables);
        var selectorResult = selectorOp.Execute();
        string selectorValue = ExtractValue(selectorResult);

        SelectionChoice? matched = null;
        SelectionChoice? defaultChoice = null;

        // 2026-07-05 (STATUS §1): compare via numeric-value normalisation
        // when both sides parse as integers. Previously a vector selector
        // ("0001") never matched a decimal alternative ("1") because the
        // clean-value string compare hit "0001" != "1". Now the check
        // first normalises both sides to integers via BinaryOrDecimalToInt
        // and falls back to string equality only when either side isn't
        // numeric. Also handles bit-string chars ('0', '1').
        foreach (var choice in Choices)
        {
            if (choice.IsOthers) { defaultChoice = choice; continue; }
            foreach (var choiceVal in choice.Choices)
            {
                if (ChoicesMatch(choiceVal, selectorValue))
                {
                    matched = choice;
                    break;
                }
            }
            if (matched != null) break;
        }

        var chosen = matched ?? defaultChoice;
        if (chosen != null) AssignValue(chosen.ValueExpression, scheduler);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Extracts value from result.
    /// </summary>
    /// <param name="result">The result object, which may be a Variable or a raw value.</param>
    /// <returns>The string representation of the extracted value.</returns>
    private static string ExtractValue(object? result)
    {
        if (result is Variable varResult)
            return varResult.Value?.ToString() ?? "";
        return result?.ToString() ?? "";
    }

    /// <summary>
    /// Cleans a value by removing quotes.
    /// </summary>
    /// <param name="value">The value to clean.</param>
    /// <returns>The value with single and double quotes removed and whitespace trimmed.</returns>
    private static string CleanValue(string value)
    {
        return value.Replace("\"", "").Replace("'", "").Trim();
    }

    /// <summary>
    /// Numeric-first alternative match for with…select. Tries both sides as
    /// integers (accepting bit-string, decimal, or hex); falls back to case-
    /// insensitive string equality when either side isn't numeric. Closes
    /// STATUS §1: `when 0|1|7` vs. an `std_logic_vector` selector.
    /// </summary>
    private static bool ChoicesMatch(string choiceVal, string selectorValue)
    {
        var a = CleanValue(choiceVal);
        var b = CleanValue(selectorValue);
        if (TryParseInt(a, out long ai) && TryParseInt(b, out long bi))
            return ai == bi;
        return a.Equals(b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Parse decimal, binary bit-string (all 0/1), or hex (0x…, x"…") into
    /// a long. Empty/mixed strings return false so the caller falls back to
    /// string comparison. Guards STATUS §1 with…select mixed alternatives.
    /// </summary>
    private static bool TryParseInt(string s, out long value)
    {
        value = 0;
        if (string.IsNullOrEmpty(s)) return false;
        // Bit-string: all 0/1 → binary. Guard against being fooled by
        // decimal integers that happen to be `0` or `1`.
        bool allBits = s.All(c => c == '0' || c == '1');
        if (allBits)
        {
            try { value = Convert.ToInt64(s, 2); return true; }
            catch { /* fall through */ }
        }
        if (long.TryParse(s, System.Globalization.NumberStyles.Integer,
                          System.Globalization.CultureInfo.InvariantCulture, out value))
            return true;
        // Simple hex: "0x…" or "x…".
        var h = s;
        if (h.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) h = h.Substring(2);
        else if (h.StartsWith("x", StringComparison.OrdinalIgnoreCase)) h = h.Substring(1);
        else if (h.StartsWith("X", StringComparison.OrdinalIgnoreCase)) h = h.Substring(1);
        else return false;
        return long.TryParse(h, System.Globalization.NumberStyles.HexNumber,
                             System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// Assigns a value to the target signal. When <paramref name="scheduler"/>
    /// is non-null, routes through the SignalScheduler so the change goes
    /// into SignalHistory and respects VHDL delta-cycle semantics. The
    /// scheduler-less path is the legacy fallback used by tests and callers
    /// that haven't been wired into the engine yet.
    /// </summary>
    private void AssignValue(string valueExpression, Services.SignalScheduler? scheduler = null)
    {
        if (Target == null)
            return;

        // Strip `after N <unit>` from the value expression and capture the
        // delay in nanoseconds so we can route through ScheduleAfterNs.
        // Without this the AFTER clause was silently dropped and the
        // assignment fired in the current delta — bench bug #8.
        long afterNs = 0;
        string valExpr = valueExpression;
        {
            var m = System.Text.RegularExpressions.Regex.Match(
                valueExpression,
                @"\bafter\s+(\d+)\s*(fs|ps|ns|us|ms|sec|s)?\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success && long.TryParse(m.Groups[1].Value, out long n))
            {
                string unit = m.Groups[2].Success ? m.Groups[2].Value.ToLowerInvariant() : "ns";
                afterNs = unit switch
                {
                    "fs" => n / 1_000_000L,
                    "ps" => n / 1_000L,
                    "ns" => n,
                    "us" => n * 1_000L,
                    "ms" => n * 1_000_000L,
                    "sec" or "s" => n * 1_000_000_000L,
                    _ => n,
                };
                valExpr = valueExpression.Substring(0, m.Index).Trim();
            }
        }

        Operation valueOp = new(valExpr, _variables);
        var result = valueOp.Execute();
        object value = (result is Variable varResult) ? varResult.Value : result!;

        if (scheduler != null)
        {
            if (TargetBitIndex is int bitIdx)
            {
                // Per-bit selected assignment: splice via ScheduleBitWrite
                // so sibling per-bit `with…select` blocks (one per output
                // bit of a demux/decoder) merge instead of clobbering.
                string s = value?.ToString() ?? "0";
                char bit = s.TrimStart('\'').TrimEnd('\'').Trim() is { Length: > 0 } str ? str[^1] : '0';
                if (afterNs > 0)
                {
                    // After-delay with bit-target: build a full-vector
                    // future value by splicing the bit into the current
                    // signal value and schedule the whole result.
                    string current = Target.Value as string ?? string.Empty;
                    int width = Math.Max(Target.Size, current.Length);
                    if (width == 0) width = bitIdx + 1;
                    if (current.Length < width) current = current.PadLeft(width, '0');
                    int pos = width - 1 - bitIdx;
                    if (pos >= 0 && pos < current.Length)
                    {
                        var chars = current.ToCharArray();
                        chars[pos] = bit;
                        scheduler.ScheduleAfterNs(Target, new string(chars), afterNs);
                    }
                }
                else
                {
                    scheduler.ScheduleBitWrite(Target, bitIdx, bit);
                }
            }
            else if (afterNs > 0)
            {
                scheduler.ScheduleAfterNs(Target, value, afterNs);
            }
            else
            {
                scheduler.Schedule(Target, value);
            }
        }
        else
        {
            Target.Value = value;
        }
    }

    /// <summary>
    /// Synthesizes the selected assignment from the token stream.
    /// </summary>
    public void Syntetize()
    {
        if (_streamer == null) return;

        // Expect 'with'
        if (_streamer.Current?.Value.ToLower() == "with")
        {
            _streamer.MoveNext();
        }

        // Parse selector expression until 'select'
        var selectorBuilder = new StringBuilder();
        while (_streamer.Current != null && _streamer.Current.Value.ToLower() != "select")
        {
            selectorBuilder.Append(_streamer.Current.Value + " ");
            _streamer.MoveNext();
        }
        SelectorExpression = selectorBuilder.ToString().Trim();

        // Expect 'select'
        if (_streamer.Current?.Value.ToLower() == "select")
        {
            _streamer.MoveNext();
        }

        // Parse target
        if (_streamer.Current?.Type == IDENTIFIERS)
        {
            TargetName = _streamer.Current.Value;
            Target = _variables.FirstOrDefault(v => v.Name.Equals(TargetName, StringComparison.OrdinalIgnoreCase));
            _streamer.MoveNext();
        }

        // Capture array indexing like `y(0)` so the assignment lands on
        // the right bit. Previously this just skipped the index tokens,
        // making per-bit selected assignments collapse to whole-signal
        // writes that clobbered each other. Bench bug #5/#9 fix.
        if (_streamer.Current?.Value == "(")
        {
            ParseTargetIndex();
        }

        // Expect '<='
        if (_streamer.Current?.Value == "<=")
        {
            _streamer.MoveNext();
        }

        // Parse choices
        ParseChoices();
    }

    /// <summary>
    /// Parses array indexing like `(0)`. Captures a literal-integer index
    /// into <see cref="TargetBitIndex"/>; falls back to no-index (whole-
    /// signal write) when the inner expression isn't a plain integer.
    /// </summary>
    private void ParseTargetIndex()
    {
        if (_streamer?.Current?.Value != "(") return;
        _streamer.MoveNext();

        // Collect tokens until the matching closing paren.
        var inner = new StringBuilder();
        int depth = 1;
        while (_streamer.Current != null)
        {
            if (_streamer.Current.Value == "(") { depth++; inner.Append('('); _streamer.MoveNext(); continue; }
            if (_streamer.Current.Value == ")")
            {
                depth--;
                if (depth == 0) { _streamer.MoveNext(); break; }
                inner.Append(')'); _streamer.MoveNext(); continue;
            }
            inner.Append(_streamer.Current.Value);
            _streamer.MoveNext();
        }

        if (int.TryParse(inner.ToString().Trim(), out int idx))
            TargetBitIndex = idx;
    }

    /// <summary>
    /// Parses the selection choices.
    /// </summary>
    private void ParseChoices()
    {
        while (_streamer != null && _streamer.Current != null && _streamer.Current.Value != ";")
        {
            var choice = new SelectionChoice();

            // Parse value expression until 'when'
            var valueBuilder = new StringBuilder();
            int parenDepth = 0;

            while (_streamer.Current != null && _streamer.Current.Value != ";")
            {
                if (_streamer.Current.Value == "(")
                    parenDepth++;
                else if (_streamer.Current.Value == ")")
                    parenDepth--;

                // Stop at 'when' (but not inside parentheses)
                if (_streamer.Current.Value.ToLower() == "when" && parenDepth == 0)
                    break;

                valueBuilder.Append(_streamer.Current.Value + " ");
                _streamer.MoveNext();
            }

            choice.ValueExpression = valueBuilder.ToString().Trim();

            // Expect 'when'
            if (_streamer.Current?.Value.ToLower() == "when")
            {
                _streamer.MoveNext();
            }

            // Parse choices until ',' or ';'
            while (_streamer.Current != null && _streamer.Current.Value != "," && _streamer.Current.Value != ";")
            {
                string val = _streamer.Current.Value;

                if (val.ToLower() == "others")
                {
                    choice.IsOthers = true;
                }
                else if (val != "|")
                {
                    // Add as a choice value
                    choice.Choices.Add(val);
                }

                _streamer.MoveNext();
            }

            Choices.Add(choice);

            // Check for ','
            if (_streamer.Current?.Value == ",")
            {
                _streamer.MoveNext();
            }
        }

        // Consume ';'
        if (_streamer?.Current?.Value == ";")
        {
            _streamer.MoveNext();
        }
    }

    /// <summary>
    /// Not implemented - use Syntetize() instead.
    /// </summary>
    /// <param name="data">The raw data for synthesis (unused).</param>
    public void Syntetize(string[] data) => throw new NotImplementedException();
}
