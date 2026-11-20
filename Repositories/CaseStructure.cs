using System.Text;
using Interpreter.Interfaces;
using Interpreter.Models;
using Interpreter.Services;
using Parser.Models;
using Parser.Repositories;
using static Parser.Models.Constants.TYPE;

namespace Interpreter.Repositories;

/// <summary>
/// Represents a single 'when' branch of a case statement.
/// </summary>
public class CaseWhenBranch
{
    /// <summary>
    /// Gets or sets the choice values for this branch (e.g., "00", "01", or "others").
    /// </summary>
    public List<string> Choices { get; set; } = new();

    /// <summary>
    /// Gets or sets whether this is the 'others' branch.
    /// </summary>
    public bool IsOthers { get; set; } = false;

    /// <summary>
    /// Gets or sets the list of tasks to execute when this branch is taken.
    /// </summary>
    public List<object> Tasks { get; set; } = new();

    /// <summary>
    /// 1-based source line of the <c>when</c> keyword for this arm (#5). Lets
    /// the live engine emit a condition-evaluation signal tagged with the line
    /// so a breakpoint on a <c>when</c> arm pauses when that arm is selected.
    /// Zero means unknown.
    /// </summary>
    public int ConditionLine { get; set; }
}

/// <summary>
/// Represents and manages the logic for a VHDL 'case' statement.
/// Syntax: case selector is when choice1 => statements; when choice2 => statements; when others => statements; end case;
/// </summary>
/// <remarks>
/// <para><strong>Supported VHDL Syntax:</strong></para>
/// <code>
/// case selector is
///     when "0000" => statements;
///     when "0001" | "0010" => statements;  -- Multiple choices
///     when others => statements;           -- Default branch
/// end case;
/// </code>
/// <para><strong>Features:</strong></para>
/// <list type="bullet">
/// <item>Multiple choice values per branch using '|' separator</item>
/// <item>'when others =>' default/fallback branch</item>
/// <item>Nested control structures (if, case, loops) within branches</item>
/// <item>v0.13.0: Can be nested within if/elsif/else branches</item>
/// </list>
/// <para><strong>Design:</strong></para>
/// <para>
/// Uses CaseWhenBranch objects to represent each 'when' clause. During execution,
/// branches are evaluated in order until a match is found. The 'others' branch
/// always matches and should be last.
/// </para>
/// </remarks>
public class CaseStructure : ISControl
{
    /// <summary>The variables in scope during case selector and branch evaluation.</summary>
    private readonly HashSet<Variable> _variables = new();
    /// <summary>The token stream used for parsing the case structure.</summary>
    private readonly TokenStream _streamer;
    /// <summary>Reader of the branch bodies (null until <see cref="Syntetize"/> for a stand-alone case).</summary>
    private SequentialStatementReader? _reader;
    /// <summary>The raw name of the selector expression as parsed from the token stream.</summary>
    private string _selectorName = string.Empty;
    /// <summary>The resolved selector variable, or null if it could not be found in scope.</summary>
    private Variable? _selector = null;
    /// <summary>High bit of the selector slice when the case targets a sliced
    /// selector like <c>case op(3 downto 0) is</c>; -1 if the whole signal is
    /// used.</summary>
    private int _selectorSliceHigh = -1;
    /// <summary>Low bit of the selector slice; -1 if the whole signal is used.</summary>
    private int _selectorSliceLow  = -1;
    /// <summary>
    /// Full selector expression text as written in the source, e.g.
    /// <c>to_integer(cnt_q)</c> or <c>to_integer(unsigned(code_r))</c>. Captured
    /// for selectors that are NOT a plain (optionally sliced) signal — function
    /// calls, type conversions, arithmetic — so the engine can evaluate the
    /// expression at run time instead of failing to resolve a single
    /// <see cref="Variable"/>. Empty when the selector is a simple signal.
    /// </summary>
    private string _selectorExpr = string.Empty;

    /// <summary>
    /// Gets the selector variable for the case statement.
    /// </summary>
    public Variable? Selector => _selector;

    /// <summary>
    /// The full selector expression text (see <see cref="_selectorExpr"/>).
    /// </summary>
    public string SelectorExpression => _selectorExpr;

    /// <summary>
    /// True when the selector is an expression (function call / type conversion
    /// / arithmetic) rather than a plain signal — i.e. no <see cref="Selector"/>
    /// variable resolved but a <see cref="SelectorExpression"/> is available.
    /// Callers evaluate the expression via <see cref="EvaluateSelectorExpression"/>.
    /// </summary>
    public bool HasExpressionSelector => _selector is null && !string.IsNullOrEmpty(_selectorExpr);

    /// <summary>
    /// Evaluates the selector expression (e.g. <c>to_integer(cnt_q)</c>) against
    /// the current scope and returns its cleaned string value. Mirrors the
    /// selector handling in <see cref="SelectedAssignment"/> so a process
    /// <c>case &lt;expr&gt; is</c> matches identically to a concurrent
    /// <c>with &lt;expr&gt; select</c>. Returns an empty string when there is no
    /// expression to evaluate.
    /// </summary>
    public string EvaluateSelectorExpression()
    {
        if (string.IsNullOrEmpty(_selectorExpr)) return string.Empty;
        var op = new Operation(_selectorExpr, _variables);
        var result = op.Execute();
        string value = result is Variable vr ? (vr.Value?.ToString() ?? "") : (result?.ToString() ?? "");
        return value.Replace("\"", "").Replace("'", "").Trim();
    }

    /// <summary>
    /// Gets the raw selector name/expression as written in the source
    /// (e.g. <c>sel</c> in <c>case sel is</c>). Unlike <see cref="Selector"/>
    /// this is a plain string that survives even when the selector could not be
    /// resolved to a <see cref="Variable"/> in scope, so diagram/export walkers
    /// can label the case without reflecting into the Variable object.
    /// </summary>
    public string SelectorName => _selectorName;

    /// <summary>
    /// High bit of the slice the selector targets, or -1 if no slice was
    /// specified. Synthesizers that honor this should compare just the
    /// selector bits in <c>[<see cref="SelectorSliceLow"/>, <see cref="SelectorSliceHigh"/>]</c>.
    /// </summary>
    public int SelectorSliceHigh => _selectorSliceHigh;

    /// <summary>Low bit of the slice; -1 when no slice is specified.</summary>
    public int SelectorSliceLow  => _selectorSliceLow;

    /// <summary>True when the case targets a sliced selector.</summary>
    public bool HasSelectorSlice => _selectorSliceHigh >= 0 && _selectorSliceLow >= 0;

    /// <summary>
    /// Gets the list of tasks (for compatibility).
    /// </summary>
    public List<object> Tasks { get; private set; } = new();

    /// <summary>
    /// Gets all 'when' branches.
    /// </summary>
    public List<CaseWhenBranch> Branches { get; private set; } = new();

    /// <summary>
    /// True when the statement was written as VHDL-2008 <c>case? … is?</c>
    /// (matching-case). In this mode, a `-` character in any choice acts as
    /// a don't-care and matches any bit of the selector at that position.
    /// </summary>
    public bool IsMatching { get; private set; } = false;

    /// <summary>
    /// Initializes a new instance of the <see cref="CaseStructure"/> class.
    /// </summary>
    /// <param name="Variables">The set of variables available in the current scope.</param>
    /// <param name="Tokenizer">The token stream to read the case structure from.</param>
    public CaseStructure(HashSet<Variable> Variables, TokenStream Tokenizer)
    {
        _variables = Variables;
        _streamer = Tokenizer;
    }

    /// <summary>Creates a case statement read by <paramref name="reader"/> (stream on <c>case</c>).</summary>
    public CaseStructure(SequentialStatementReader reader) : this(reader.Scope, reader.Stream)
    {
        _reader = reader;
    }

    /// <summary>
    /// Executes the case structure asynchronously.
    /// </summary>
    public async Task ExecuteAsync()
    {
        // Expression selector (e.g. `case to_integer(cnt_q) is`) — evaluate the
        // expression at run time; otherwise fall through on a resolved signal.
        bool exprSelector = HasExpressionSelector;
        if (!exprSelector && _selector is null) return;

        string selectorValue = exprSelector
            ? EvaluateSelectorExpression()
            : (_selector!.Value?.ToString() ?? "");

        CaseWhenBranch? othersBranch = null;
        foreach (var branch in Branches)
        {
            // Save 'others' as the fallback but keep scanning for an exact arm.
            if (branch.IsOthers)
            {
                othersBranch ??= branch;
                continue;
            }

            // Check if selector matches any of the choices
            foreach (var choice in branch.Choices)
            {
                bool hit = CaseChoice.Matches(choice, selectorValue, IsMatching, exprSelector);
                if (hit)
                {
                    Executer executer = new(branch.Tasks);
                    await executer.ExecuteAsync();
                    return;
                }
            }
        }

        // No explicit arm matched — run 'others' if present.
        if (othersBranch != null)
        {
            Executer executer = new(othersBranch.Tasks);
            await executer.ExecuteAsync();
        }
    }

    /// <summary>
    /// Synthesizes the case structure from the token stream.
    /// </summary>
    public void Syntetize()
    {
        Interpreter.Services.ParseTrace.Log($"CaseStructure @{(_streamer.Current?.Line ?? 0) + 1}");
        _reader ??= new SequentialStatementReader(_streamer, _variables);
        _streamer.Expect(KEYWORDS); // consume 'case'
        // VHDL-2008 matching-case (Tier 4.2, 2026-07-28) — the lexer splits
        // `case?` into two tokens (`case` + `?`) because `?` is a special
        // char. Consume the optional `?` here, again after `is`, and again
        // after the closing `end case`. IsMatching gates the wildcard
        // semantics in ExecuteAsync.
        if (_streamer.Current?.Value == "?")
        {
            IsMatching = true;
            _streamer.MoveNext();
        }

        // v0.16.1: Handle optional parentheses around selector: case (selector) is
        bool hasParens = _streamer.Current?.Value == "(";
        if (hasParens)
        {
            _streamer.MoveNext(); // consume '('
        }

        // Get selector expression
        _selectorName = _streamer.Current?.Value ?? "";
        _selector = _variables.FirstOrDefault(v => v.Name.Equals(_selectorName, StringComparison.OrdinalIgnoreCase));
        _streamer.MoveNext(); // consume selector

        // Reconstruct the full selector expression text so a non-signal selector
        // (function call / type conversion / arithmetic) can be evaluated at run
        // time — e.g. `case to_integer(cnt_q) is`. For a plain signal this is
        // just its name; for a sliced/parenthesised selector we append the group.
        var selectorExprBuilder = new StringBuilder(_selectorName);

        // Slice/index after the selector identifier:
        //   case op(3 downto 0) is — slice
        //   case mem(idx) is        — single bit (also handled, slice = idx..idx)
        // Anything more complex (function calls, expressions) is consumed but
        // leaves the slice fields at -1 so the synthesizer falls back to the
        // whole-vector comparison — and the captured expression text is used
        // instead (see _selectorExpr / EvaluateSelectorExpression).
        if (_streamer.Current?.Value == "(")
        {
            var innerTokens = ParseSelectorSlice();
            selectorExprBuilder.Append('(').Append(string.Join(" ", innerTokens)).Append(')');
        }

        // Only keep the expression text when it isn't a plain resolvable signal;
        // the plain-signal path (and sliced-signal path) is handled by _selector
        // and the slice fields, byte-identical to before this change.
        if (_selector is null)
            _selectorExpr = selectorExprBuilder.ToString();

        // v0.16.1: Consume closing parenthesis if we had an opening one
        if (hasParens && _streamer.Current?.Value == ")")
        {
            _streamer.MoveNext(); // consume ')'
        }

        _streamer.Expect(KEYWORDS); // consume 'is'
        if (IsMatching && _streamer.Current?.Value == "?") _streamer.MoveNext();

        // Parse all 'when' branches (each one consumes its tokens).
        while (_streamer.Current?.Value?.ToLower() != "end")
        {
            if (_streamer.Current?.Value?.ToLower() != "when")
                throw new ParserException("'when' expected in a case statement", _streamer.Current ?? _streamer.Previous);
            SyntetizeWhenBranch();
        }

        // C7 (duplicate choice): VHDL forbids the same value appearing in two
        // branches ("choice already covered"). Compare by value so `"00"` and a
        // constant/decimal that resolve to the same number are caught too.
        var seenChoices = new List<string>();
        foreach (var br in Branches)
        {
            // Ranges (`a to b`) are currently stored as loose tokens
            // ("a", "to", "b"), not as a single value — comparing those tokens
            // would false-positive (e.g. two ranges both contribute a "to").
            // Skip duplicate-checking any branch that contains a range.
            bool hasRange = br.Choices.Any(c =>
                c.Equals("to", StringComparison.OrdinalIgnoreCase) ||
                c.Equals("downto", StringComparison.OrdinalIgnoreCase));
            if (hasRange) continue;

            foreach (var ch in br.Choices)
            {
                if (seenChoices.Any(prev => CaseChoice.Matches(prev, ch, numeric: true)))
                    throw new ParserException(
                        $"duplicate case choice '{ch}'", _streamer.Current ?? _streamer.Previous);
                seenChoices.Add(ch);
            }
        }

        // C6 (non-exhaustive): a std_logic / std_logic_vector / bit selector
        // always requires a `when others` branch — those types carry meta-values
        // (U/X/Z/W/L/H/-) that no finite set of 0/1 choices can cover, so GHDL
        // rejects such a case without `others`. Enum/custom/integer selectors and
        // matching-case (`case?`) are left out of this check.
        if (!IsMatching
            && !Branches.Any(b => b.IsOthers)
            && _selector is { CustomTypeName: null }
            && (_selector.Type == Constants.DATATYPES.STD_LOGIC
                || _selector.Type == Constants.DATATYPES.STD_LOGIC_VECTOR
                || _selector.Type == Constants.DATATYPES.BIT))
        {
            throw new ParserException(
                $"case on '{_selectorName}' does not cover all choices; add 'when others =>'",
                _streamer.Current ?? _streamer.Previous);
        }

        // Populate Tasks with first branch for compatibility
        if (Branches.Count > 0)
        {
            Tasks = Branches[0].Tasks;
        }
    }

    /// <summary>
    /// Consumes the parenthesised slice/index expression that may follow a
    /// case selector identifier (e.g. <c>case op(3 downto 0) is</c>) and,
    /// when the expression is a simple literal range, populates
    /// <see cref="SelectorSliceHigh"/> / <see cref="SelectorSliceLow"/>.
    /// </summary>
    /// <remarks>
    /// More complex expressions (function calls, arithmetic, generic-derived
    /// indices) are still consumed cleanly so the parser can advance past
    /// the <c>)</c> — they just don't populate the slice fields and the
    /// downstream synthesizer falls back to the whole-vector comparison.
    /// </remarks>
    private List<string> ParseSelectorSlice()
    {
        _streamer.MoveNext(); // consume the leading '('

        var inner = new List<string>();
        int depth = 1;
        while (_streamer.Current != null && depth > 0)
        {
            string val = _streamer.Current.Value;
            if (val == "(") { depth++; inner.Add(val); _streamer.MoveNext(); continue; }
            if (val == ")")
            {
                depth--;
                if (depth == 0) break;
                inner.Add(val);
                _streamer.MoveNext();
                continue;
            }
            inner.Add(val);
            _streamer.MoveNext();
        }
        if (_streamer.Current?.Value == ")") _streamer.MoveNext();

        // Pattern: literal direction literal — a clean slice we can record.
        if (inner.Count == 3)
        {
            string dir = inner[1].ToLowerInvariant();
            if ((dir == "downto" || dir == "to")
                && int.TryParse(inner[0], out int a)
                && int.TryParse(inner[2], out int b))
            {
                _selectorSliceHigh = Math.Max(a, b);
                _selectorSliceLow  = Math.Min(a, b);
            }
        }
        // Pattern: single literal — bit index.
        else if (inner.Count == 1 && int.TryParse(inner[0], out int idx))
        {
            _selectorSliceHigh = idx;
            _selectorSliceLow  = idx;
        }
        // Anything else: leave slice fields at -1, synth uses full vector.

        return inner;
    }

    /// <summary>
    /// True when the selector resolves to a built-in numeric/vector type, where
    /// a case choice is always a literal or a constant — never an enum literal.
    /// Enum/custom-typed selectors (CustomTypeName set) and unresolved selectors
    /// return false so choice-identifier validation stays lenient for them.
    /// </summary>
    private bool SelectorIsNumericOrVector()
    {
        if (_selector is null || _selector.CustomTypeName != null) return false;
        return _selector.Type is Constants.DATATYPES.STD_LOGIC
            or Constants.DATATYPES.STD_LOGIC_VECTOR
            or Constants.DATATYPES.BIT
            or Constants.DATATYPES.INTEGER
            or Constants.DATATYPES.NATURAL;
    }

    /// <summary>
    /// True when an identifier case choice names a constant in the current scope
    /// or a package-level constant (via <see cref="PackageRegistry"/>).
    /// </summary>
    private bool IsKnownChoiceConstant(string name)
        => _variables.Any(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
           || PackageRegistry.Instance.ContainsConstant(name);

    /// <summary>
    /// True when an in-scope variable/constant named <paramref name="name"/> is
    /// itself of enum type <paramref name="enumType"/> — i.e. a legal value of the
    /// selector's type (e.g. `constant INIT : state_t`), so precise-C10 must not
    /// mistake it for a cross-enum literal that merely shares the name.
    /// </summary>
    private bool IsValueOfEnumType(string name, string enumType)
        => _variables.Any(v =>
               v.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(v.EnumTypeName, enumType, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Synthesizes a single 'when' branch.
    /// </summary>
    private void SyntetizeWhenBranch()
    {
        int condLine = _streamer.Current?.SourceLine ?? 0; // 1-based line of the when keyword (#5)
        _streamer.Expect(KEYWORDS); // consume 'when'

        var branch = new CaseWhenBranch { ConditionLine = condLine };

        // Parse choices (can be multiple separated by '|')
        // Safety: limit iterations to prevent infinite loop
        int maxIterations = 1000;
        int iterations = 0;
        // Note: '=>' is tokenized as a single OPERATORS token by the Parser
        while (_streamer.Current?.Value != "=>" && _streamer.Current != null && iterations < maxIterations)
        {
            iterations++;
            string currentValue = _streamer.Current?.Value ?? "";
            string lowerValue = currentValue.ToLowerInvariant();

            // A case choice may only contain literals, identifiers, 'others',
            // '|', and range keywords (to/downto). Hitting ';', an assignment
            // operator, or a block keyword before '=>' means the arrow is
            // missing — stop so the post-loop check reports it.
            if (currentValue == ";" || currentValue == "<=" || currentValue == ":=" ||
                (_streamer.Current?.Type == KEYWORDS &&
                 lowerValue != "others" && lowerValue != "to" && lowerValue != "downto"))
            {
                break;
            }

            if (currentValue.ToLower() == "others")
            {
                branch.IsOthers = true;
                _streamer.MoveNext();
            }
            else if (currentValue == "|")
            {
                _streamer.MoveNext(); // skip separator
            }
            else if (!string.IsNullOrWhiteSpace(currentValue))
            {
                // precise-C10: on an ENUM selector of a known type T, a choice that
                // is an enumeration literal of a DIFFERENT enum is illegal
                // (GHDL: `'X' is not a literal of type 'T'`). Fires ONLY when T's
                // literals are known, the choice is NOT one of them, and the choice
                // is definitively a literal of some other enum — so a valid literal
                // (incl. a literal colliding with a same-named signal) never trips
                // it; everything else defers to C1 below. Literals come from the
                // enum TYPE (EnumTypeRegistry), never from enum-literal Variables,
                // so the case-insensitive literal/signal collision can't hide one.
                if (_streamer.Current?.Type == IDENTIFIERS
                    && _selector?.EnumTypeName is string enumT
                    && EnumTypeRegistry.Instance.Literals(enumT) is { } selectorLits
                    && !selectorLits.Any(l => l.Equals(currentValue, StringComparison.OrdinalIgnoreCase))
                    && !IsValueOfEnumType(currentValue, enumT)
                    && EnumTypeRegistry.Instance.IsAnyEnumLiteral(currentValue))
                    throw new ValidationException(
                        $"'{currentValue}' is not a literal of type '{enumT}'", _streamer.Current);

                // C1 (undeclared case choice): on a numeric/vector selector a
                // choice written as an identifier must name a declared or package
                // constant — there are no enum literals to match. An unknown name
                // (e.g. `when C =>`) is undeclared, not a silent fall-through.
                // Enum selectors type numerically here too, so `when STOP =>` on an
                // enum signal is caught by this same check. Enum/custom-typed and
                // unresolved selectors are otherwise left lenient.
                if (_streamer.Current?.Type == IDENTIFIERS
                    && SelectorIsNumericOrVector()
                    && !IsKnownChoiceConstant(currentValue))
                    throw new ParserException(
                        $"no declaration for case choice '{currentValue}'", _streamer.Current);
                branch.Choices.Add(currentValue);
                _streamer.MoveNext();
            }
            else
            {
                _streamer.MoveNext();
            }
        }

        if (iterations >= maxIterations)
            throw new InvalidOperationException("Case statement parsing exceeded maximum iterations - possible malformed input");

        // Consume '=>' (single token) — required by VHDL after case choices.
        if (_streamer.Current?.Value != "=>")
            throw new ParserException(
                "'=>' expected after case choice", _streamer.Current ?? _streamer.Previous);
        _streamer.MoveNext(); // consume '=>'

        // Parse branch body until next 'when' or 'end'
        SyntetizeBranchBody(branch);

        Branches.Add(branch);
    }

    /// <summary>
    /// Synthesizes the body of a when branch.
    /// </summary>
    /// <param name="branch">The branch whose body is being synthesized.</param>
    private void SyntetizeBranchBody(CaseWhenBranch branch) =>
        _reader!.ReadUntil(branch.Tasks, "when", "end");
}
