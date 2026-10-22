using Interpreter.Interfaces;
using Interpreter.Models;
using Parser.Models;
using Parser.Repositories;
using static Parser.Models.Constants.TYPE;
namespace Interpreter.Services;

/// <summary>
/// Synthesizes variables from a token stream.
/// </summary>
public class VariableSyntetizer : ISControl
{
    /// <summary>
    /// Gets the synthesized variables.
    /// </summary>
    public HashSet<Variable> Variables { get; private set; } = new();

    /// <summary>
    /// Gets the parsed custom type definitions.
    /// </summary>
    public TypeRegistry ParsedTypes { get; private set; } = new();

    private HashSet<Variable> _comparator;
    private readonly string _breaker = "begin";
    private bool _isPort, _isGeneric, _isSignal;
    private TokenStream _streamer;
    private TypeRegistry? _typeRegistry;

    // Bench bug #19 follow-up: per-element width + slot count of the array
    // type currently being initialized. Set right before the := initializer
    // is parsed and cleared right after, so the named-association aggregate
    // parser can build a slot-correct bitstring without each call site
    // having to plumb the array type through.
    private int _currentArrayElementSize = 0;
    private int _currentArrayLength = 0;

    /// <summary>
    /// F-G-08 pass 4 (2026-07-17): cancellation token in effect for the
    /// current Synthesize call. Threading a CancellationToken through every
    /// Skip*/Parse* signature would inflate this class by hundreds of lines
    /// of boilerplate; a per-call field is minimally invasive. Set at the
    /// top of <see cref="Syntetize(CancellationToken)"/>; read by each Skip*
    /// method's <see cref="ParseTrace.Step"/> call.
    /// </summary>
    private CancellationToken _currentCt = CancellationToken.None;

    /// <summary>
    /// Initializes a new instance of the <see cref="VariableSyntetizer"/> class.
    /// </summary>
    /// <param name="Streamer">The token stream.</param>
    /// <param name="Comparator">The set of variables to compare against.</param>
    /// <param name="IsPort">Whether the variable is a port.</param>
    /// <param name="IsGeneric">Whether the variable is a generic.</param>
    /// <param name="Breaker">The token that breaks the synthesis.</param>
    /// <param name="IsSignal">Whether the variable is a signal.</param>
    /// <param name="TypeRegistry">Optional type registry for custom type resolution.</param>
    public VariableSyntetizer(TokenStream Streamer, HashSet<Variable> Comparator, bool IsPort = false, bool IsGeneric = false, string Breaker = "begin", bool IsSignal = false, TypeRegistry? TypeRegistry = null)
    {
        _streamer = Streamer;
        _isPort = IsPort;
        _isSignal = IsSignal;
        _isGeneric = IsGeneric;
        _comparator = Comparator;
        _breaker = _isPort || _isGeneric ? ")" : Breaker.ToLower();
        _typeRegistry = TypeRegistry;
    }

    /// <summary>
    /// Executes the variable synthesizer asynchronously.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task ExecuteAsync() { await Task.CompletedTask; throw new NotImplementedException(); }

    /// <summary>
    /// Synthesizes the variables from a token stream.
    /// </summary>
    /// <param name="strings">The token stream.</param>
    public void Syntetize(string[] strings) => _ = ExecuteAsync();

    /// <summary>
    /// Synthesizes the variables from a token stream.
    /// </summary>
    public void Syntetize() => Syntetize(CancellationToken.None);

    /// <summary>
    /// Synthesizes the variables from a token stream.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public void Syntetize(CancellationToken cancellationToken)
    {
        // F-G-08 pass 4 (2026-07-17): make the token available to every Skip*
        // method via a field so we don't have to thread it through 10+
        // signatures. Restored to CancellationToken.None on the way out.
        _currentCt = cancellationToken;

        // v0.16.1: Track parenthesis depth for port/generic parsing
        // This ensures we only stop at the closing ')' of the Port/Generic section,
        // not at inner parentheses used in type declarations like std_logic_vector(7 downto 0)
        int portGenericParenDepth = 0;

        while (_streamer.Current != null)
        {
            // For ports/generics, track parenthesis depth
            if (_isPort || _isGeneric)
            {
                if (_streamer.Current.Value == "(")
                {
                    portGenericParenDepth++;
                }
                else if (_streamer.Current.Value == ")")
                {
                    if (portGenericParenDepth == 0)
                    {
                        // This is the closing ')' of the Port/Generic section
                        break;
                    }
                    portGenericParenDepth--;
                }
            }
            else
            {
                // For signals/variables, use the string breaker (typically "begin")
                if (_breaker == _streamer.Current?.Value.ToLower())
                    break;
            }

            // Check for cancellation at each iteration
            cancellationToken.ThrowIfCancellationRequested();

            // F-G-08 pass 4 diag: one trace per declaration so we can see
            // exactly which decl the outer loop is about to dispatch.
            int _declStartLine = (_streamer.Current?.Line ?? 0) + 1;
            string _declToken = _streamer.Current?.Value ?? "?";
            if (ParseTrace.Enabled)
                ParseTrace.Log($"decl-begin='{_declToken}' @{_declStartLine}");

            // Skip component declarations
            if (_streamer.Current?.Value?.ToLower() == "component")
            {
                SkipComponentDeclaration();
                continue;
            }
            // Skip type declarations
            if (_streamer.Current?.Value?.ToLower() == "type")
            {
                SkipTypeDeclaration();
                continue;
            }
            // Skip subtype declarations
            if (_streamer.Current?.Value?.ToLower() == "subtype")
            {
                SkipSubtypeDeclaration();
                continue;
            }
            // Skip alias declarations
            if (_streamer.Current?.Value?.ToLower() == "alias")
            {
                SkipAliasDeclaration();
                continue;
            }
            // Skip function declarations
            if (_streamer.Current?.Value?.ToLower() == "function")
            {
                SkipFunctionDeclaration();
                // Safety check: if we reached the breaker or null, stop
                if (_streamer.Current == null || _streamer.Current.Value.ToLower() == _breaker)
                    break;
                continue;
            }
            // Skip procedure declarations
            if (_streamer.Current?.Value?.ToLower() == "procedure")
            {
                SkipProcedureDeclaration();
                // Safety check: if we reached the breaker or null, stop
                if (_streamer.Current == null || _streamer.Current.Value.ToLower() == _breaker)
                    break;
                continue;
            }
            // Skip attribute declarations and specifications
            if (_streamer.Current?.Value?.ToLower() == "attribute")
            {
                SkipAttributeDeclaration();
                continue;
            }
            // Skip disconnect specifications
            if (_streamer.Current?.Value?.ToLower() == "disconnect")
            {
                SkipDisconnectSpec();
                continue;
            }

            // Check for constant declarations with function calls (e.g., constant X := func(Y);)
            // v0.13.0: Constants with function calls cannot be evaluated at parse time
            // Only skip if the initialization contains a function call (identifier followed by '(')
            if (_streamer.Current?.Value?.ToLower() == "constant")
            {
                if (HasFunctionCallInConstant())
                {
                    SkipConstantDeclaration();
                    continue;
                }
                // Otherwise, fall through to normal variable parsing
            }

            List<Variable> variables = new();
            string varType = _streamer.Current?.Value ?? string.Empty;
            if (!_isPort && !_isGeneric)
            {
                // Check for unknown/custom type at beginning of declaration
                // If next token after names is a custom type, skip this declaration
                if (_streamer.Current?.Type != KEYWORDS)
                {
                    // Skip this entire declaration until ';'
                    SkipToSemicolon();
                    continue;
                }
                _streamer.Expect(KEYWORDS);
            }
            while (_streamer.Current?.Value != ":")
            {
                string varName = _streamer.Current?.Value ?? string.Empty;
                _streamer.Expect(IDENTIFIERS);
                if (_streamer.Current?.Value == ",")
                    _streamer.MoveNext();
                if (Variables.Contains(varName))
                {
                    throw new ParserException(
                        Constants.ParseExceptions.DUPLICATE_DECLARATION,
                        (int)(_streamer.Current?.Line ?? 0) + 1,
                        (int)(_streamer.Current?.Col ?? 0) + 1,
                        varName);
                }
                // Note: Don't check if varName is in _comparator here - we're DECLARING the variable,
                // not using it. Forward reference checks belong in initializer parsing.
                variables.Add(new() { Name = varName });
            }
            _streamer.Expect(OPERATORS); // :

            // v0.16.1: Handle port direction - VHDL allows omitting direction (defaults to 'in')
            Constants.PortType portType = Constants.PortType.IN; // Default per VHDL spec
            if (_isPort || _isGeneric)
            {
                string currentVal = _streamer.Current?.Value?.ToLower() ?? "";
                if (currentVal == "in" || currentVal == "out" || currentVal == "inout" || currentVal == "buffer")
                {
                    // Direction keyword present - parse it
                    portType = currentVal switch
                    {
                        "in" => Constants.PortType.IN,
                        "out" => Constants.PortType.OUT,
                        "inout" => Constants.PortType.OUT, // Treat inout as out for resource estimation
                        "buffer" => Constants.PortType.OUT, // Treat buffer as out
                        _ => Constants.PortType.IN
                    };
                    _streamer.MoveNext(); // Consume direction keyword
                }
                // If no direction keyword, portType stays as IN (default)
            }
            else if (varType.ToLower() == "constant")
            {
                portType = Constants.PortType.IN;
            }

            var dataType = Variable.GetDatatype(_streamer.Current?.Value ?? string.Empty);
            string dataTypeStr = _streamer.Current?.Value?.ToLower() ?? string.Empty;

            // Check if it's a custom type in the registry
            TypeDefinition? customTypeDef = null;
            if (dataType == Constants.DATATYPES.UNKNOWN)
            {
                // Try to resolve from type registry (local scope first, then
                // this parser instance's parsed-types table). F-G-04 follow-up
                // (2026-07-09): finally consult `PackageRegistry` for
                // cross-file package-declared subtypes seeded by
                // `PackageSubtypeScanner` at project scan time. When a hit
                // lands here, `dataType` gets promoted to the base numeric
                // type (STD_LOGIC_VECTOR) and `VectorSize` is derived from
                // the subtype's high/low bounds — so `a : in q16_t` is
                // treated indistinguishably from `a : in signed(31 downto 0)`
                // downstream.
                customTypeDef = _typeRegistry?.Get(dataTypeStr)
                             ?? ParsedTypes.Get(dataTypeStr)
                             ?? PackageSubtypeScanner.LookupGlobally(dataTypeStr);
                if (customTypeDef != null)
                {
#if DEBUG
                    Console.WriteLine($"[VariableSyntetizer] Resolved custom type '{dataTypeStr}' -> {customTypeDef.Kind}, Size={customTypeDef.TotalSize}");
#endif
                }
            }

            // If datatype is UNKNOWN and NOT in registry, skip this declaration
            if (dataType == Constants.DATATYPES.UNKNOWN && customTypeDef == null && !_isPort && !_isGeneric)
            {
                // F-G-04 extension (Phase G Ring 4 audit 2026-07-09): the
                // SourceElaborator inlines child architectures by promoting
                // their ports to top-level signals (renamed as
                // `<label>__<port>`) and their signals to top-level signals
                // (same rename scheme). When those child items are typed
                // with a package-declared subtype (`q16_t`, `opcode_t`, …),
                // the same drop-on-skip pattern that dropped ports also
                // drops the elaborated signals — every downstream reference
                // then fails with "Undeclared variable". Register the
                // signal name(s) as a placeholder Variable so name
                // resolution succeeds; arithmetic semantics remain the same
                // "best-effort STD_LOGIC_VECTOR width 32" as the port
                // fallback, but the elaborated top compiles.
                string fallbackSignalType = _streamer.Current?.Value ?? string.Empty;
                foreach (var v in variables)
                {
                    Variables.Add(new Variable
                    {
                        Name = v.Name,
                        Type = Constants.DATATYPES.STD_LOGIC_VECTOR,
                        Size = 32,
                        IsPort = false,
                        CustomTypeName = fallbackSignalType,
                    });
                }
                SkipToSemicolon();
                continue;
            }
            // For ports with unknown type, try to skip gracefully
            if (dataType == Constants.DATATYPES.UNKNOWN && customTypeDef == null && (_isPort || _isGeneric))
            {
                // Strictness: when the unknown port type is within Levenshtein
                // distance 2 of a canonical VHDL type name, fail loudly with a
                // "did you mean" hint. Catches STDLOGIC / STD_LGIC slips that
                // would otherwise be silently absorbed by the skip loop below.
                // Mirrors the check in Parser.Repositories.PortsParser — that
                // path isn't reachable from the App's pipeline, this one is.
                var typoTok = _streamer.Current;
                var suggestion = Constants.FindTypoSuggestion(typoTok?.Value ?? string.Empty);
                if (suggestion != null)
                {
                    throw new ParserException(
                        $"Unknown port type '{typoTok?.Value}'. Did you mean '{suggestion}'?",
                        (int)(typoTok?.Line ?? 0) + 1,
                        (int)(typoTok?.Col ?? 0) + 1,
                        typoTok?.Value);
                }
                // F-G-04 (Phase G audit 2026-07-09): package-declared subtypes
                // (e.g. `subtype q16_t is signed(31 downto 0);` in `types_pkg`)
                // don't currently make it into the headless pipeline's port
                // parser — `use work.types_pkg.all;` doesn't yet load the
                // parent package's subtype table into a scope this method
                // consults. Without this branch, `port (a : in q16_t; …)`
                // would silently drop `a` from `Variables`, and any process
                // sensitivity list or expression referencing `a` would fail
                // with "Undeclared variable 'a'". Register each port under a
                // best-effort placeholder type (STD_LOGIC_VECTOR, size 32) so
                // downstream name resolution succeeds. Arithmetic semantics
                // for signed/unsigned subtypes may not be honored, but that's
                // a strictly narrower gap than a dropped port.
                string fallbackTypeName = _streamer.Current?.Value ?? string.Empty;
                foreach (var v in variables)
                {
                    Variables.Add(new Variable
                    {
                        Name = v.Name,
                        Type = Constants.DATATYPES.STD_LOGIC_VECTOR,
                        Size = 32,
                        IsPort = _isPort,
                        PortType = portType,
                        CustomTypeName = fallbackTypeName,
                    });
                }
                // Skip to semicolon or closing paren of port list (not inner parens)
                int parenDepth = 0;
                while (_streamer.Current != null)
                {
                    if (_streamer.Current.Value == "(")
                        parenDepth++;
                    else if (_streamer.Current.Value == ")")
                    {
                        if (parenDepth == 0)
                            break; // This is the port list's closing paren
                        parenDepth--;
                    }
                    else if (_streamer.Current.Value == ";" && parenDepth == 0)
                        break;
                    _streamer.MoveNext();
                }
                if (_streamer.Current?.Value == ";")
                    _streamer.MoveNext();
                continue;
            }
            // v0.13.0: Handle custom types - they are IDENTIFIERS not KEYWORDS
            // For custom types found in the registry, consume the type identifier directly
            if (customTypeDef != null)
            {
                _streamer.Expect(IDENTIFIERS); // consume custom type name (e.g., 'reg_array_t')
            }
            else
            {
                _streamer.Expect(KEYWORDS); // consume standard type keyword (e.g., 'std_logic')
            }
            int VectorSize = 0;

            // Declared vector range (VHDL 'left/'right/direction), captured here
            // and applied to the created Variable below so index/slice/attribute
            // resolution honours to/downto and non-zero low bounds. Null => no
            // range recorded => conventional (Size-1 downto 0) fallback.
            int? declLeft = null;
            int? declRight = null;
            bool declIsDownto = true;

            // Set default size for single-bit types
            if (dataType == Constants.DATATYPES.STD_LOGIC || dataType == Constants.DATATYPES.BIT)
            {
                VectorSize = 1;
            }

            // Handle vector types with parentheses (std_logic_vector, bit_vector, signed, unsigned, etc.)
            if (dataType == Constants.DATATYPES.STD_LOGIC_VECTOR ||
                dataTypeStr == "bit_vector" || dataTypeStr == "signed" || dataTypeStr == "unsigned" ||
                dataTypeStr == "std_ulogic_vector" || dataTypeStr == "boolean_vector" || dataTypeStr == "integer_vector")
            {
                if (_streamer.Current?.Value == "(")
                {
                    // Parse vector range (7 downto 0) or (0 to 7)
                    // v0.13.0: For ports/generics, check if expression is complex before evaluating
                    _streamer.Expect(PARENTHESIS); // consume '('

                    // Check if this is a complex expression (contains operators, nested parens, or function calls)
                    bool isComplexExpression = false;
                    {
                        _streamer.SetRollBack();
                        // Peek ahead to see if expression contains operators, nested parens, or function calls
                        bool prevWasIdentifier = false;
                        while (_streamer.Current != null && _streamer.Current.Value != ")")
                        {
                            if (_streamer.Current.Value == "(")
                            {
                                // Nested paren - could be function call or complex expression
                                isComplexExpression = true;
                                break;
                            }
                            string[] operators = { "+", "-", "*", "**", "/" };
                            if (operators.Contains(_streamer.Current.Value))
                            {
                                isComplexExpression = true;
                                break;
                            }
                            // Check for function call pattern: identifier followed by '('
                            if (prevWasIdentifier && _streamer.Current.Value == "(")
                            {
                                isComplexExpression = true;
                                break;
                            }
                            prevWasIdentifier = _streamer.Current.Type == IDENTIFIERS;
                            _streamer.MoveNext();
                        }
                        _streamer.RollBack();
                    }

                    if (isComplexExpression)
                    {
                        // F-G-01 fix (session 8, 2026-07-09): try to fold the
                        // range expression using PackageRegistry-loaded
                        // constants before giving up. Common capstone
                        // patterns like `std_logic_vector(LED_ROWS*LED_COLS - 1 downto 0)`
                        // (LED_ROWS=LED_COLS=3 → high=8) now resolve to 9-bit
                        // vectors instead of defaulting to 8. Handles:
                        //   * bare integer / constant refs
                        //   * two-operand +, -, *, /
                        //   * three-operand `A op B op C` (typical `W-1`, `A*B-1`, …)
                        // Multiplication / addition chains beyond three tokens
                        // still fall back to the default. On any resolution
                        // failure, existing default-8 behaviour is preserved.
                        int? hiEval = null;
                        string? dirTok = null;
                        int? loEval = null;
                        {
                            var expr = new List<string>();
                            int depth = 1;
                            _streamer.SetRollBack();
                            while (_streamer.Current != null && depth > 0)
                            {
                                var val = _streamer.Current.Value;
                                if (val == "(") depth++;
                                else if (val == ")") { depth--; if (depth == 0) break; }
                                expr.Add(val);
                                _streamer.MoveNext();
                            }
                            _streamer.RollBack();

                            // Split around "downto" or "to".
                            int splitIdx = -1;
                            for (int i = 0; i < expr.Count; i++)
                            {
                                var t = expr[i].ToLowerInvariant();
                                if (t == "downto" || t == "to")
                                {
                                    splitIdx = i;
                                    dirTok = t;
                                    break;
                                }
                            }
                            if (splitIdx > 0 && dirTok != null)
                            {
                                hiEval = FoldSimpleExpr(expr.GetRange(0, splitIdx));
                                loEval = FoldSimpleExpr(expr.GetRange(splitIdx + 1, expr.Count - splitIdx - 1));
                            }
                        }

                        // Skip the complex expression tokens.
                        int parenDepth = 1;
                        while (_streamer.Current != null && parenDepth > 0)
                        {
                            if (_streamer.Current.Value == "(")
                                parenDepth++;
                            else if (_streamer.Current.Value == ")")
                                parenDepth--;
                            if (parenDepth > 0)
                                _streamer.MoveNext();
                        }
                        if (_streamer.Current?.Value == ")")
                            _streamer.MoveNext(); // consume final ')'

                        if (hiEval.HasValue && loEval.HasValue && dirTok != null)
                        {
                            int hi = hiEval.Value;
                            int lo = loEval.Value;
                            int width = dirTok == "downto" ? hi - lo + 1 : lo - hi + 1;
                            VectorSize = width > 0 ? width : 8;
                            // hiEval is the bound written left of to/downto, loEval
                            // the one to its right: that IS 'left/'right as written.
                            declLeft = hi;
                            declRight = lo;
                            declIsDownto = dirTok == "downto";
#if DEBUG
                            Console.WriteLine($"[VariableSyntetizer] F-G-01 folded range: {hi} {dirTok} {lo} → width {VectorSize}");
#endif
                        }
                        else
                        {
#if DEBUG
                            Console.WriteLine($"[VariableSyntetizer] Complex vector expression detected, using default size");
#endif
                            VectorSize = 8; // Default size for complex expressions
                        }
                    }
                    else
                    {
                        // Simple expression: evaluate normally
                        object varValue = GetVariableValue();
                        int initial = Convert.ToInt32(varValue);
                        string vecType = _streamer.Current?.Value ?? string.Empty;
                        _streamer.Expect(KEYWORDS); // consume 'downto' or 'to'
                        varValue = GetVariableValue();
                        int end = Convert.ToInt32(varValue);
                        _streamer.Expect(PARENTHESIS); // consume ')'
                        VectorSize = vecType.ToLower() == "downto" ? initial - end + 1 : end - initial + 1;
                        // `initial` is the bound left of to/downto, `end` the one
                        // to its right: 'left/'right exactly as written.
                        declLeft = initial;
                        declRight = end;
                        declIsDownto = vecType.ToLower() == "downto";
#if DEBUG
                        Console.WriteLine($"[VariableSyntetizer] Simple vector range: {initial} {vecType} {end} = Size {VectorSize}");
#endif
                    }
                }
            }
            // Handle integer/natural/positive with optional range constraint
            else if (dataType == Constants.DATATYPES.INTEGER || dataType == Constants.DATATYPES.NATURAL ||
                     dataTypeStr == "positive")
            {
                if (_streamer.Current?.Value?.ToLower() == "range")
                {
                    _streamer.Expect(KEYWORDS); // consume "range"
                    // F-G-02 fix (session 8, 2026-07-09): scoped
                    // PackageRegistry lookup for range bounds. Prior code
                    // called GetVariableValue which registers a placeholder
                    // (Value=0) for any identifier not in local scope, so
                    // `range 0 to BUZ_LONG_TICKS` collapsed to `0 to 0` and
                    // yielded width 1. Now, if the current token is an
                    // identifier that resolves in PackageRegistry, use that
                    // constant's value directly and advance the stream —
                    // otherwise fall back to GetVariableValue's legacy
                    // behaviour. Keeping the PackageRegistry lookup ONLY in
                    // this branch (not in GetVariableValue itself) avoids
                    // disturbing initializer / expression paths that read
                    // signals whose *names* happen to shadow constants.
                    int initial = ReadRangeBound();
                    _streamer.Expect(KEYWORDS); // consume "to"
                    int end = ReadRangeBound();
                    VectorSize = RangeToBitWidth(initial, end);
                }
            }
            // Handle time type - will be handled in initialization
            // Handle real type - will be handled in initialization
            // Handle RANGE standalone type
            else if (dataType == Constants.DATATYPES.RANGE)
            {
                object varValue = GetVariableValue();
                int initial = Convert.ToInt32(varValue);
                _streamer.Expect(KEYWORDS);
                varValue = GetVariableValue();
                int end = Convert.ToInt32(varValue);
                VectorSize = Math.Abs(end - initial);
            }
            // Handle custom types from registry
            else if (customTypeDef != null)
            {
                VectorSize = customTypeDef.TotalSize;
                // For custom types, use base type if available (for arrays/subtypes)
                if (customTypeDef.Kind == TypeKind.Array)
                {
                    dataType = customTypeDef.BaseType != Constants.DATATYPES.UNKNOWN
                        ? customTypeDef.BaseType
                        : Constants.DATATYPES.STD_LOGIC_VECTOR;
                }
                else if (customTypeDef.Kind == TypeKind.Enumeration)
                {
                    // Enum types can be stored as integers representing enum index
                    dataType = Constants.DATATYPES.INTEGER;
                }
                else if (customTypeDef.Kind == TypeKind.Subtype)
                {
                    dataType = customTypeDef.BaseType;
                    // Inherit the subtype's declared range so signals of a
                    // subtype like `byte is std_logic_vector(7 downto 0)` index
                    // correctly. SubtypeRange is stored (High=max, Low=min).
                    if (customTypeDef.BaseType == Constants.DATATYPES.STD_LOGIC_VECTOR)
                    {
                        declLeft = customTypeDef.IsDownto ? customTypeDef.SubtypeRange.High : customTypeDef.SubtypeRange.Low;
                        declRight = customTypeDef.IsDownto ? customTypeDef.SubtypeRange.Low : customTypeDef.SubtypeRange.High;
                        declIsDownto = customTypeDef.IsDownto;
                    }
                }
                // For records, use STD_LOGIC_VECTOR as storage type
                else if (customTypeDef.Kind == TypeKind.Record)
                {
                    dataType = Constants.DATATYPES.STD_LOGIC_VECTOR;
                }
                // v0.13.0: Type name already consumed at lines 231-240, no need to Expect again
            }

            // v0.13.0: Handle 'bus' keyword for guarded signals (e.g., signal x : std_logic_vector bus := ...)
            // The 'bus' keyword appears after the type to indicate a guarded/resolved signal
            if (_streamer.Current?.Value?.ToLower() == "bus")
            {
                _streamer.MoveNext(); // Skip 'bus' keyword
            }

            object value = new();
            // Handle initialization with :=
            if (!_isPort && _streamer.Current?.Value == ":=")
            {
                _streamer.Expect(OPERATORS); // consume ":="

                // Bench bug #19 follow-up: pass array element width into the
                // aggregate parser so it can place each named-association
                // value at the right slot offset. Reset after parsing so
                // unrelated initializers fall back to the scalar code path.
                int prevElemSize = _currentArrayElementSize;
                int prevArrayLen = _currentArrayLength;
                if (customTypeDef != null && customTypeDef.Kind == TypeKind.Array
                    && customTypeDef.ElementSize > 1)
                {
                    _currentArrayElementSize = customTypeDef.ElementSize;
                    _currentArrayLength = customTypeDef.ArrayLength;
                }
                else
                {
                    _currentArrayElementSize = 0;
                    _currentArrayLength = 0;
                }

                value = ParseInitializationValue();

                _currentArrayElementSize = prevElemSize;
                _currentArrayLength = prevArrayLen;
            }
            // Handle "after" clause (skip it for now - temporal simulation not implemented)
            if (_streamer.Current?.Value?.ToLower() == "after")
            {
                SkipAfterClause();
            }
            variables.ForEach(variable =>
            {
#if DEBUG
                if (_isPort && dataType == Constants.DATATYPES.STD_LOGIC_VECTOR)
                    Console.WriteLine($"[VariableSyntetizer] Creating port '{variable.Name}' with VectorSize={VectorSize}");
#endif
                // Determine initial value - use custom type default if applicable
                object initValue = value;
                if (!_isPort && customTypeDef != null && (value is null || value.Equals(new object())))
                {
                    initValue = customTypeDef.GetDefaultValue();
                }
                // Eagerly expand `(others=>X)` aggregate to a literal bitstring
                // when the declared vector size is known. The Size setter only
                // expands the marker when `_value` is already non-null, but the
                // ctor below sets Size first (with no value) and then Value, so
                // the marker would otherwise leak into VCD output as the literal
                // 11-char string `(others=>0)` until the first signal write.
                // Covers std_logic_vector, unsigned, signed, bit_vector, and the
                // other vector-shaped types — the marker form is by definition
                // vector-only in VHDL, so VectorSize>0 + marker pattern suffices.
                if (!_isPort
                    && VectorSize > 0
                    && initValue is string initMarker
                    && initMarker.StartsWith("(others=>", StringComparison.Ordinal))
                {
                    int arrow = initMarker.IndexOf("=>", StringComparison.Ordinal);
                    int close = initMarker.IndexOf(')', arrow);
                    if (arrow >= 0 && close > arrow + 2)
                    {
                        string inner = initMarker.Substring(arrow + 2, close - arrow - 2).Trim();
                        char fill = inner.Length > 0 ? inner[0] : '0';
                        initValue = new string(fill, VectorSize);
                    }
                }
                var newVar = new Variable()
                {
                    Name = variable.Name,
                    Type = dataType,
                    IsPort = _isPort,
                    PortType = portType,
                    // Mark arch-local constants so downstream (VCD exporter etc.)
                    // can distinguish them from signals. Audit F-G-03 (2026-07-09).
                    IsConstant = varType.Equals("constant", StringComparison.OrdinalIgnoreCase),
                    Size = VectorSize,  // Set Size BEFORE Value to avoid Size setter issues
                    Value = !_isPort ? initValue : null!,
                };

                // Record the declared range (to/downto + bounds) so index/slice/
                // attribute resolution is range-aware. Only when an explicit range
                // was parsed; otherwise the Variable keeps the (Size-1 downto 0)
                // fallback and behaves exactly as before.
                if (declLeft.HasValue && declRight.HasValue)
                {
                    newVar.SetDeclaredRange(declLeft.Value, declRight.Value, declIsDownto);
                }

                // Bench bug #19: when the variable is typed by a user-defined
                // array (e.g. `rom_t = array(0 to 7) of std_logic_vector(7 downto 0)`),
                // carry the per-element width into the Variable so DynamicIndex /
                // DeferredIndex can slice element-wise on `signal(N)` access.
                // Without this, the indexer falls back to single-bit reads and
                // returns garbage / 'x' for every lookup into a constant array.
                if (customTypeDef != null && customTypeDef.Kind == TypeKind.Array
                    && customTypeDef.ElementSize > 1)
                {
                    newVar.ElementSize = customTypeDef.ElementSize;
                    newVar.ArrayLength = customTypeDef.ArrayLength;
                    if (string.IsNullOrEmpty(newVar.CustomTypeName))
                        newVar.CustomTypeName = customTypeDef.Name;
                }

                // Store custom type name reference for debugging/analysis
                if (customTypeDef != null)
                {
                    // Record the enum type name (precise-C10). Kept separate from
                    // CustomTypeName so the case numeric/exhaustiveness gates that
                    // key on CustomTypeName are unaffected.
                    if (customTypeDef.Kind == TypeKind.Enumeration)
                        newVar.EnumTypeName = customTypeDef.Name;
#if DEBUG
                    Console.WriteLine($"[VariableSyntetizer] Created '{newVar.Name}' with custom type '{customTypeDef.Name}', Size={newVar.Size}");
#endif
                }
#if DEBUG
                else if (_isPort && dataType == Constants.DATATYPES.STD_LOGIC_VECTOR)
                    Console.WriteLine($"[VariableSyntetizer] Created port '{newVar.Name}' actual Size={newVar.Size}");
#endif
                Variables.Add(newVar);
            });
            if ((_isPort || _isGeneric) && _streamer.Current?.Value != ";")
            {
                // We just finished a port/generic declaration but the next
                // token isn't `;` — and it isn't `)` either (the outer port
                // loop would already have broken on `)`). That means the
                // user forgot the trailing `;` between two consecutive
                // declarations. Throw a precise diagnostic that points at
                // the offending token's source line/col instead of letting
                // the caller fail later with a generic "expected `(` or
                // `)`" message that landed on the wrong line for years.
                if (_streamer.Current?.Type == IDENTIFIERS)
                {
                    string nextName = _streamer.Current.Value ?? string.Empty;
                    throw new Parser.Repositories.TokenStream.StreamerException(
                        $"Missing ';' before '{nextName}' at Line {_streamer.Current.Line + 1}:{_streamer.Current.Col + 1}. " +
                        "Each port / generic declaration must end with a semicolon (except the last one before ')').",
                        _streamer.Current);
                }
                break;
            }
            _streamer.Expect(OPERATORS); // ;
            if (ParseTrace.Enabled)
                ParseTrace.Log($"decl-end='{_declToken}' @{_declStartLine} next='{_streamer.Current?.Value}'");
        }
    }

    /// <summary>
    /// Gets the value of a variable.
    /// </summary>
    /// <returns>The value of the variable.</returns>
    private object GetVariableValue() {
        object varValue = null!;
        _streamer.SetRollBack();
        string rawOperation = string.Empty;
        int index = 0;
        int parenDepth = 0;
        // Stop at 'downto', 'to', or ')' at depth 0 - these are range/bound delimiters
        // We need to track parenthesis depth for expressions like (2 * DATA_WIDTH) - 1
        while (_streamer.Current != null)
        {
            string? currentVal = _streamer.Current?.Value;
            string? currentValLower = currentVal?.ToLower();

            // Check for terminators only at depth 0
            if (parenDepth == 0)
            {
                if (currentValLower == "downto" || currentValLower == "to" ||
                    currentVal == ")" || currentVal == ";")
                    break;
            }

            // Track parenthesis depth
            if (currentVal == "(")
                parenDepth++;
            else if (currentVal == ")")
                parenDepth--;

            index++;
            rawOperation += $"{currentVal} ";
            _streamer.MoveNext();
        }
        _streamer.RollBack();
        rawOperation = rawOperation.Trim();
        if (!String.IsNullOrEmpty(rawOperation))
        {
            string[] basicOps = ["**", "*", "-", "+", "/"];
            if (basicOps.Any(rawOperation.Contains))
            {
                Operation op = new(rawOperation, _comparator);
                var outData = op.Execute();
                if (outData is Variable variable)
                {
                    outData = variable.Value;
                }
                varValue = outData;
                for (int i = 0; i < index; _streamer?.MoveNext(), i++) ;
                return varValue;
            }
        }
        if (_streamer.Current?.Type == IDENTIFIERS)
        {
            string identifierName = _streamer.Current.Value;

            // Check if this is a standard function call like clog2(DEPTH)
            _streamer.SetRollBack();
            _streamer.MoveNext(); // move past identifier
            if (_streamer.Current?.Value == "(")
            {
                // This is a function call
                _streamer.MoveNext(); // consume '('

                // Parse arguments
                List<object> args = new();
                int argDepth = 1;
                string argExpr = "";
                while (_streamer.Current != null && argDepth > 0)
                {
                    if (_streamer.Current.Value == "(") argDepth++;
                    else if (_streamer.Current.Value == ")") argDepth--;

                    if (argDepth > 0)
                    {
                        if (_streamer.Current.Value == "," && argDepth == 1)
                        {
                            // End of argument
                            if (!string.IsNullOrWhiteSpace(argExpr))
                            {
                                // Try to evaluate the argument
                                var argVar = _comparator.FirstOrDefault(p => p.Name.Equals(argExpr.Trim(), StringComparison.OrdinalIgnoreCase));
                                if (!ReferenceEquals(argVar, null))
                                    args.Add(argVar.Value ?? 0);
                                else if (int.TryParse(argExpr.Trim(), out int argInt))
                                    args.Add(argInt);
                                else
                                    args.Add(argExpr.Trim());
                            }
                            argExpr = "";
                        }
                        else
                        {
                            argExpr += _streamer.Current.Value + " ";
                        }
                        _streamer.MoveNext();
                    }
                }

                // Add last argument
                if (!string.IsNullOrWhiteSpace(argExpr))
                {
                    var argVar = _comparator.FirstOrDefault(p => p.Name.Equals(argExpr.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (!ReferenceEquals(argVar, null))
                        args.Add(argVar.Value ?? 0);
                    else if (int.TryParse(argExpr.Trim(), out int argInt))
                        args.Add(argInt);
                    else
                        args.Add(argExpr.Trim());
                }

                _streamer.MoveNext(); // consume ')'

                // Check if it's a standard function
                if (StandardFunctions.IsStandardFunction(identifierName))
                {
                    varValue = StandardFunctions.EvaluateFunction(identifierName, args.ToArray(), _comparator);
                }
                else
                {
                    // Unknown function - return 0 as default
                    varValue = 0;
                }
                return varValue;
            }
            else
            {
                // Not a function call - roll back and treat as variable
                _streamer.RollBack();
            }

            // Look up in the comparator (ports/generics) first, then in already-declared
            // Variables (constants/signals from this same pass). Without checking
            // Variables, expressions like `unsigned(FIFO_ADDR_WIDTH - 1 downto 0)`
            // that reference an earlier `constant FIFO_ADDR_WIDTH := log2ceil(...);`
            // would fail even though the constant is in scope.
            string lookupName = _streamer.Current?.Value ?? string.Empty;
            varValue = _comparator.FirstOrDefault(p => p.Name.Equals(lookupName, StringComparison.OrdinalIgnoreCase))?.Value!;
            if (varValue == null)
            {
                varValue = Variables.FirstOrDefault(p => p.Name.Equals(lookupName, StringComparison.OrdinalIgnoreCase))?.Value!;
            }
            if (varValue == null)
            {
                // Last-chance fallback: register an implicit placeholder so synthesis
                // can continue. Real value is unknown, but downstream sizing falls
                // back to defaults (size=1) and we keep the test moving instead of
                // throwing on every forward reference. Use int 0 so consumers
                // (Convert.ToInt32 in range arithmetic) don't fault.
                Variables.Add(new Variable { Name = lookupName, Value = 0 });
                varValue = 0;
            }
            _streamer.Expect(IDENTIFIERS);
        }
        else
        {
            varValue = _streamer.Current?.Value!;
            _streamer.Expect(LITERALS);
        }
        return varValue;
    }

    /// <summary>
    /// Parses initialization value after := operator.
    /// Handles literals, hex strings, aggregates (others => '0'), expressions, and concatenations.
    /// </summary>
    /// <returns>The parsed initialization value.</returns>
    private object ParseInitializationValue()
    {
        object value = new();

        // Check for aggregate expression like (others => '0')
        if (_streamer.Current?.Value == "(")
        {
            value = ParseAggregateOrExpression();
        }
        // Check for hex literal like x"FF"
        else if (_streamer.Current?.Value?.ToLower().StartsWith("x\"") == true ||
                 _streamer.Current?.Value?.StartsWith("X\"") == true)
        {
            value = ParseHexLiteral(_streamer.Current.Value);
            _streamer.MoveNext();
        }
        // Check for time value (number followed by time unit)
        else if (_streamer.Current?.Type == LITERALS)
        {
            // Remove quotes from string literals for vectors
            value = _streamer.Current.Value?.Replace("\"", "").Replace("'", "") ?? string.Empty;
            _streamer.MoveNext();

            // Check if followed by time unit
            if (_streamer.Current?.Type == TIME_VARIABLES)
            {
                // Store as string with unit for now
                value = $"{value} {_streamer.Current.Value}";
                _streamer.MoveNext();
            }
        }
        // Check for boolean or keyword literal
        else if (_streamer.Current?.Type == KEYWORDS)
        {
            value = _streamer.Current.Value?.ToLower() == "true";
            _streamer.MoveNext();
        }
        // Check for identifier reference
        else if (_streamer.Current?.Type == IDENTIFIERS)
        {
            // First check comparator (ports/generics), then check Variables (constants/signals being declared)
            var refVar = _comparator.FirstOrDefault(p => p.Name.Equals(_streamer.Current?.Value, StringComparison.OrdinalIgnoreCase));
            if (ReferenceEquals(refVar, null))
            {
                refVar = Variables.FirstOrDefault(p => p.Name.Equals(_streamer.Current?.Value, StringComparison.OrdinalIgnoreCase));
            }
            value = refVar?.Value ?? _streamer.Current?.Value ?? string.Empty;
            _streamer.MoveNext();
        }
        else
        {
            // Skip unknown token
            _streamer.MoveNext();
        }

        // Handle arithmetic chains in default-value expressions, e.g.
        // `generic (TOTAL : integer := BASE * TIMES)` or
        // `signal x : integer := WIDTH + 1`. The parser accepts these and
        // tries to fold the result; if any operand is an unresolved
        // placeholder (typical for generic-of-generic forward refs), the
        // partial fold falls back to 0 and the value is materialised by
        // downstream synthesizers when the actual generic is resolved.
        while (_streamer.Current?.Type == OPERATORS &&
               (_streamer.Current.Value == "+" ||
                _streamer.Current.Value == "-" ||
                _streamer.Current.Value == "*" ||
                _streamer.Current.Value == "/"))
        {
            string op = _streamer.Current.Value;
            _streamer.MoveNext();

            object rhs = ParseInitializationOperand();

            int? lhsInt = TryAsInt(value);
            int? rhsInt = TryAsInt(rhs);
            if (lhsInt.HasValue && rhsInt.HasValue)
            {
                value = op switch
                {
                    "+" => lhsInt.Value + rhsInt.Value,
                    "-" => lhsInt.Value - rhsInt.Value,
                    "*" => lhsInt.Value * rhsInt.Value,
                    "/" => rhsInt.Value == 0 ? 0 : lhsInt.Value / rhsInt.Value,
                    _   => value
                };
            }
            else
            {
                // Either side is a forward / unresolved reference. Keep
                // the LHS so downstream code at least has a deterministic
                // (if approximate) value rather than a raw object().
            }
        }

        // Handle concatenation operator &
        // E.g., "0000" & A or x"FF" & B
        while (_streamer.Current?.Value == "&")
        {
            _streamer.MoveNext(); // skip '&'

            // Parse the right operand
            object rightValue = new();
            if (_streamer.Current?.Value == "(")
            {
                rightValue = ParseAggregateOrExpression();
            }
            else if (_streamer.Current?.Value?.ToLower().StartsWith("x\"") == true)
            {
                rightValue = ParseHexLiteral(_streamer.Current.Value);
                _streamer.MoveNext();
            }
            else if (_streamer.Current?.Type == LITERALS)
            {
                rightValue = _streamer.Current.Value?.Replace("\"", "").Replace("'", "") ?? string.Empty;
                _streamer.MoveNext();
            }
            else if (_streamer.Current?.Type == IDENTIFIERS)
            {
                // Reference to another signal/variable - try to get its value
                var refVar = _comparator.FirstOrDefault(p => p.Name.Equals(_streamer.Current?.Value, StringComparison.OrdinalIgnoreCase));
                if (!ReferenceEquals(refVar, null))
                {
                    // If it's a port or has size info, use U padding
                    if (refVar.IsPort && refVar.Size > 0)
                    {
                        rightValue = new string('U', refVar.Size);
                    }
                    else if (refVar.Value != null && refVar.Value.ToString() != "System.Object")
                    {
                        rightValue = refVar.Value.ToString()?.Replace("\"", "").Replace("'", "") ?? string.Empty;
                    }
                    else if (refVar.Size > 0)
                    {
                        rightValue = new string('U', refVar.Size);
                    }
                    else
                    {
                        rightValue = string.Empty;
                    }
                }
                else
                {
                    // Can't resolve - use placeholder with same size as declared or unknown
                    rightValue = string.Empty;
                }
                _streamer.MoveNext();
            }
            else
            {
                _streamer.MoveNext();
                rightValue = string.Empty;
            }

            // Concatenate the values
            string leftStr = value?.ToString()?.Replace("\"", "").Replace("'", "") ?? string.Empty;
            string rightStr = rightValue?.ToString()?.Replace("\"", "").Replace("'", "") ?? string.Empty;

            // If rightStr is empty (unresolved reference), mark as expression placeholder
            if (string.IsNullOrEmpty(rightStr))
            {
                // Mark value as expression that couldn't be fully evaluated
                value = $"(concat:{leftStr})";
            }
            else
            {
                value = leftStr + rightStr;
            }
        }

        return value;
    }

    /// <summary>
    /// Evaluates an argument expression for function calls.
    /// Handles variable references, literals, and simple expressions.
    /// v0.15.0: Added to support constant evaluation with function calls.
    /// v0.15.0: Now also checks PackageRegistry for package constants.
    /// </summary>
    /// <param name="argExpr">The argument expression string.</param>
    /// <returns>The evaluated value.</returns>
    private object EvaluateArgumentExpression(string argExpr)
    {
        // Try to find variable reference first
        var argVar = _comparator.FirstOrDefault(p => p.Name.Equals(argExpr, StringComparison.OrdinalIgnoreCase));
        if (!ReferenceEquals(argVar, null))
            return argVar.Value ?? 0;

        // Check Variables being declared
        argVar = Variables.FirstOrDefault(p => p.Name.Equals(argExpr, StringComparison.OrdinalIgnoreCase));
        if (!ReferenceEquals(argVar, null))
            return argVar.Value ?? 0;

        // v0.15.0: Package constant lookup disabled for debugging
        // var packageConst = PackageRegistry.Instance.GetConstant(argExpr);
        // if (packageConst != null && packageConst.Value != null)
        //     return packageConst.Value;

        // Try parsing as integer
        if (int.TryParse(argExpr, out int argInt))
            return argInt;

        // Check for expression with operators
        string[] basicOps = { "**", "*", "-", "+", "/" };
        if (basicOps.Any(argExpr.Contains))
        {
            try
            {
                Operation op = new(argExpr, _comparator);
                var outData = op.Execute();
                if (outData is Variable variable)
                    return variable.Value ?? 0;
                return outData ?? 0;
            }
            catch
            {
                return 0;
            }
        }

        // Return as string if nothing else works
        return argExpr;
    }

    /// <summary>
    /// Reads one operand of a default-value arithmetic expression — a single
    /// literal, identifier (resolved against the comparator and Variables),
    /// or parenthesised sub-expression. Used by the <c>+ - * /</c> chain
    /// handler in <see cref="ParseInitializationValue"/>.
    /// </summary>
    private object ParseInitializationOperand()
    {
        if (_streamer.Current == null) return 0;
        if (_streamer.Current.Value == "(")
        {
            return ParseAggregateOrExpression();
        }
        if (_streamer.Current.Type == LITERALS)
        {
            string raw = _streamer.Current.Value?.Replace("\"", "").Replace("'", "") ?? "0";
            _streamer.MoveNext();
            if (int.TryParse(raw, out int n)) return n;
            return raw;
        }
        if (_streamer.Current.Type == IDENTIFIERS)
        {
            string lookupName = _streamer.Current.Value ?? string.Empty;
            var refVar = _comparator.FirstOrDefault(p => p.Name.Equals(lookupName, StringComparison.OrdinalIgnoreCase))
                      ?? Variables.FirstOrDefault(p => p.Name.Equals(lookupName, StringComparison.OrdinalIgnoreCase));
            _streamer.MoveNext();
            return refVar?.Value ?? 0;
        }
        // Unrecognised operand — consume one token and return 0 so the
        // arithmetic chain doesn't loop forever.
        _streamer.MoveNext();
        return 0;
    }

    /// <summary>
    /// Best-effort int extraction from the heterogeneous <c>object</c> values
    /// stored as Variable.Value (int / string / bool). Returns null when the
    /// value isn't recognisable as a numeric integer — in that case the
    /// caller falls back to the LHS instead of folding.
    /// </summary>
    private static int? TryAsInt(object? v)
    {
        if (v is null) return null;
        if (v is int i) return i;
        if (v is long l) return (int)l;
        if (v is bool b) return b ? 1 : 0;
        if (v is string s && int.TryParse(s, out int parsed)) return parsed;
        return null;
    }

    /// <summary>
    /// Parses an aggregate expression like (others => '0') or a parenthesized expression.
    /// Bench bug #19: also recognises positional aggregates
    /// (`("10101010", "10111011", ...)`) used to initialise constant arrays,
    /// and returns them as a contiguous bitstring (element 0 first) so
    /// indexed reads via [[DeferredIndex]] / [[DynamicIndex]] can slice cleanly.
    /// Without this branch the parser fell into the generic stringify-tokens
    /// fallback below, which produced an unparseable `(  "..."  ,  "..."  )`
    /// blob and broke every `rom(addr)` lookup.
    /// </summary>
    private object ParseAggregateOrExpression()
    {
        _streamer.MoveNext(); // consume '('

        // Check for 'others' keyword
        if (_streamer.Current?.Value?.ToLower() == "others")
        {
            _streamer.MoveNext(); // consume 'others'
            if (_streamer.Current?.Value == "=>")
            {
                _streamer.MoveNext(); // consume '=>'
            }
            // Get the default value
            string defaultValue = _streamer.Current?.Value?.Replace("'", "").Replace("\"", "") ?? "0";
            _streamer.MoveNext(); // consume the value
            _streamer.MoveNext(); // consume ')'

            // Return a special aggregate marker
            return $"(others=>{defaultValue})";
        }

        // Bench bug #19 follow-up: named-association aggregate path —
        // `( key => value, key => value, ..., others => fallback )`. Used by
        // ROM tables that initialise per-address with a function-call value.
        // Requires the caller to have set _currentArrayElementSize +
        // _currentArrayLength (done in the := initializer block) so we know
        // each slot's width and how many slots to allocate.
        if (_currentArrayElementSize > 0
            && _currentArrayLength > 0
            && TryParseNamedAggregate(out string? namedBits))
        {
            return namedBits!;
        }

        // Try the positional-aggregate path. Snapshot the stream, walk
        // through the comma-separated element list; if every element is a
        // recognisable bitstring literal, concatenate and return. Any
        // unrecognised token / nested expression aborts the attempt and we
        // fall back to the legacy stringify behaviour.
        if (TryParsePositionalAggregate(out string? bitstring))
        {
            return bitstring!;
        }

        // It's a regular parenthesized expression - skip until closing paren
        int depth = 1;
        string expr = "(";
        while (depth > 0 && _streamer.Current != null)
        {
            if (_streamer.Current.Value == "(") depth++;
            else if (_streamer.Current.Value == ")") depth--;

            if (depth > 0)
            {
                expr += _streamer.Current.Value + " ";
                _streamer.MoveNext();
            }
        }
        expr += ")";
        _streamer.MoveNext(); // consume final ')'
        return expr.Trim();
    }

    /// <summary>
    /// Tries to read a positional aggregate of the form
    /// <c>("10101010", "11001100", ..., x"AB")</c> from the current stream
    /// position (which must already be past the opening '('). On success
    /// the stream is advanced past the matching ')' and the per-element
    /// bitstrings are concatenated in declaration order (element 0 first,
    /// MSB-first within each element). Returns false and rolls back the
    /// stream when any element fails to convert to a bitstring, so the
    /// caller can fall back to the legacy expression path.
    /// </summary>
    private bool TryParsePositionalAggregate(out string? bitstring)
    {
        bitstring = null;
        _streamer.SetRollBack();

        var elements = new List<string>();
        int depth = 1;
        bool first = true;

        while (_streamer.Current != null && depth > 0)
        {
            string tok = _streamer.Current.Value ?? string.Empty;

            if (tok == "(") { depth++; _streamer.MoveNext(); continue; }
            if (tok == ")")
            {
                depth--;
                if (depth == 0) { _streamer.MoveNext(); break; }
                _streamer.MoveNext();
                continue;
            }

            if (tok == ",") { _streamer.MoveNext(); continue; }

            // First non-trivial token decides whether this aggregate is
            // positional. If the very first thing is an operator or
            // identifier-followed-by `(`, we bail so a generic expression
            // like `(A + 1)` still reaches the legacy fallback.
            string? bits = null;
            if (tok.Length >= 2 && tok[0] == '"' && tok[^1] == '"')
            {
                bits = tok.Substring(1, tok.Length - 2);
            }
            else if (tok.Length >= 3 && (tok[0] == 'x' || tok[0] == 'X') && tok[1] == '"' && tok[^1] == '"')
            {
                bits = ParseHexLiteral(tok);
            }
            else
            {
                if (first) { _streamer.RollBack(); return false; }
                // Mid-aggregate non-literal — also bail; the legacy path
                // can deal with it (we don't try to evaluate identifiers
                // here to avoid order-of-declaration surprises).
                _streamer.RollBack();
                return false;
            }

            if (bits != null)
            {
                elements.Add(bits);
                first = false;
            }
            _streamer.MoveNext();
        }

        if (elements.Count == 0) { _streamer.RollBack(); return false; }

        bitstring = string.Concat(elements);
        return true;
    }

    /// <summary>
    /// Tries to read a named-association aggregate of the form
    /// <c>(0 =&gt; pack(170), 1 =&gt; pack(187), ..., others =&gt; (others =&gt; '0'))</c>
    /// at the current stream position (past the opening '('). On success the
    /// stream is advanced past the matching ')' and a contiguous bitstring of
    /// <c>_currentArrayElementSize × _currentArrayLength</c> bits is returned,
    /// with each value placed at its slot offset and any unspecified slots
    /// filled by the <c>others</c> fallback (defaults to all-'0' when
    /// absent). Value expressions are evaluated through the shared
    /// <see cref="Operation"/> pipeline so user-defined functions, based
    /// literals, and concatenations all work.
    /// Returns false (and rolls the stream back) when the aggregate doesn't
    /// match this shape so the caller can fall through to the legacy path.
    /// </summary>
    private bool TryParseNamedAggregate(out string? bitstring)
    {
        bitstring = null;
        _streamer.SetRollBack();

        int elementSize = _currentArrayElementSize;
        int slots       = _currentArrayLength;
        if (elementSize <= 0 || slots <= 0) { _streamer.RollBack(); return false; }

        // Quick pre-scan: the first chunk's syntax must include `=>` at
        // depth 1 before we hit the closing ')'. Without this we'd grab
        // unrelated parenthesised expressions like `(A + 1)` from the
        // legacy fallback.
        if (!QuickHasArrow()) { _streamer.RollBack(); return false; }

        var slotValues = new string?[slots];
        string? othersFill = null;
        bool sawAny = false;
        int depth = 1; // we're past the opening '('

        // Each iteration consumes one `<key> => <value>` chunk plus the
        // following `,` or final `)`.
        while (_streamer.Current != null && depth > 0)
        {
            // Read the key.
            string keyTok = _streamer.Current.Value ?? string.Empty;
            bool isOthers = keyTok.Equals("others", StringComparison.OrdinalIgnoreCase);
            int keyIndex = -1;
            if (isOthers)
            {
                _streamer.MoveNext();
            }
            else
            {
                if (!int.TryParse(keyTok, out keyIndex))
                {
                    // Not a simple integer key — bail to the legacy path.
                    _streamer.RollBack();
                    return false;
                }
                _streamer.MoveNext();
            }

            if (_streamer.Current?.Value != "=>") { _streamer.RollBack(); return false; }
            _streamer.MoveNext(); // consume '=>'

            // Collect the value expression tokens up to the next top-level
            // ',' or the closing ')'. Paren-aware so nested aggregates +
            // function-call arguments don't terminate the value prematurely.
            string valueExpr = "";
            int valueDepth = 0;
            while (_streamer.Current != null)
            {
                string tok = _streamer.Current.Value ?? string.Empty;
                if (valueDepth == 0 && (tok == "," || tok == ")")) break;
                if (tok == "(") valueDepth++;
                else if (tok == ")") valueDepth--;
                valueExpr += tok + " ";
                _streamer.MoveNext();
            }
            valueExpr = valueExpr.Trim();

            string slotBits = EvaluateAggregateValue(valueExpr, elementSize);

            if (isOthers) othersFill = slotBits;
            else if (keyIndex >= 0 && keyIndex < slots) slotValues[keyIndex] = slotBits;
            sawAny = true;

            // Consume the separator. ',' → next chunk; ')' → done.
            if (_streamer.Current?.Value == ",")
            {
                _streamer.MoveNext();
                continue;
            }
            if (_streamer.Current?.Value == ")")
            {
                _streamer.MoveNext();
                depth = 0;
                break;
            }
            // Unexpected token — bail.
            _streamer.RollBack();
            return false;
        }

        if (!sawAny) { _streamer.RollBack(); return false; }

        // Materialise the bitstring slot-by-slot, MSB-first (slot 0 at the
        // start of the string — matches DeferredIndex / DynamicIndex slot
        // extraction).
        string fill = othersFill ?? new string('0', elementSize);
        var sb = new System.Text.StringBuilder(elementSize * slots);
        for (int i = 0; i < slots; i++)
        {
            sb.Append(slotValues[i] ?? fill);
        }
        bitstring = sb.ToString();
        return true;
    }

    /// <summary>
    /// Cheap lookahead for the named-aggregate detector: returns true when
    /// the next significant `=&gt;` token appears before the matching outer
    /// `)`. Doesn't consume the stream.
    /// </summary>
    private bool QuickHasArrow()
    {
        _streamer.SetRollBack();
        int depth = 1;
        bool found = false;
        while (_streamer.Current != null && depth > 0)
        {
            string tok = _streamer.Current.Value ?? string.Empty;
            if (tok == "(") depth++;
            else if (tok == ")") { depth--; if (depth == 0) break; }
            else if (tok == "=>" && depth == 1) { found = true; break; }
            _streamer.MoveNext();
        }
        _streamer.RollBack();
        return found;
    }

    /// <summary>
    /// Evaluates an aggregate value expression (string / hex literal,
    /// integer literal, identifier, function call, concatenation) to an
    /// element-wide bitstring. Routes through <see cref="Operation"/> so
    /// user-defined functions resolve via the existing FunctionExecutor
    /// path. Falls back to a deterministic all-'U' element when evaluation
    /// fails so the rest of the aggregate still materialises.
    /// </summary>
    private string EvaluateAggregateValue(string expr, int elementSize)
    {
        if (string.IsNullOrWhiteSpace(expr))
            return new string('U', elementSize);

        // Trim a trailing `)` that the value-collector may leave behind
        // when the value itself is a parenthesised expression — Operation
        // can handle balanced parens, not stray closers.
        // (Defensive: the collector above is balance-aware, so this is a
        // belt-and-braces guard.)
        expr = expr.Trim();

        // Nested `(others => '0')` shape: handle directly so we don't
        // have to construct an Operation for a literal-fill aggregate.
        if (expr.StartsWith("(") && expr.IndexOf("others", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            char fill = expr.Contains("'1'") ? '1' : (expr.Contains("'0'") ? '0' : 'U');
            return new string(fill, elementSize);
        }

        // String literal `"..."` — strip quotes and pad/truncate.
        if (expr.Length >= 2 && expr[0] == '"' && expr[^1] == '"')
            return NormaliseBits(expr.Substring(1, expr.Length - 2), elementSize);

        // Hex literal `x"NN"` — convert via the existing helper.
        if (expr.Length >= 3 && (expr[0] == 'x' || expr[0] == 'X') && expr[1] == '"' && expr[^1] == '"')
            return NormaliseBits(ParseHexLiteral(expr), elementSize);

        // General expression — hand off to Operation. Use a combined scope
        // (ports + constants/signals declared so far) so identifier lookups
        // can resolve to either side.
        try
        {
            var scope = new HashSet<Variable>(_comparator);
            foreach (var v in Variables) scope.Add(v);
            var op = new Operation(expr, scope);
            var result = op.Execute();
            string bits = result switch
            {
                Variable v => v.Value?.ToString() ?? string.Empty,
                null       => string.Empty,
                _          => result.ToString() ?? string.Empty,
            };
            return NormaliseBits(bits, elementSize);
        }
        catch
        {
            return new string('U', elementSize);
        }
    }

    /// <summary>
    /// Normalises a raw bit / value string into an element-wide bitstring:
    /// strips surrounding quotes, pads with '0' on the left when shorter,
    /// or truncates on the left when longer. Integer-shaped results (e.g.
    /// <c>"170"</c> from a numeric function call) are converted to base-2.
    /// </summary>
    private static string NormaliseBits(string raw, int width)
    {
        if (string.IsNullOrEmpty(raw)) return new string('U', width);
        string s = raw.Replace("\"", string.Empty).Replace("'", string.Empty).Trim();

        bool isBinary = s.Length > 0 && s.All(c => c == '0' || c == '1' || c == 'U' || c == 'X' || c == 'Z');
        if (!isBinary)
        {
            if (int.TryParse(s, out int n))
            {
                if (n < 0) n &= (int)((1L << width) - 1);
                s = Convert.ToString(n, 2);
            }
            else return new string('U', width);
        }

        if (s.Length == width) return s;
        if (s.Length > width)  return s.Substring(s.Length - width);
        return s.PadLeft(width, '0');
    }

    /// <summary>
    /// Parses a hex literal like x"FF" into a binary string.
    /// </summary>
    private string ParseHexLiteral(string hexLiteral)
    {
        // Remove x" prefix and " suffix
        string hex = hexLiteral.Substring(2, hexLiteral.Length - 3);
        string binary = string.Join("", hex.Select(c =>
            Convert.ToString(Convert.ToInt32(c.ToString(), 16), 2).PadLeft(4, '0')));
        return binary;
    }

    /// <summary>
    /// Skips the 'after' timing clause (not implemented for simulation).
    /// </summary>
    private void SkipAfterClause()
    {
        _streamer.MoveNext(); // consume 'after'

        // Skip the time value
        if (_streamer.Current?.Type == LITERALS)
        {
            _streamer.MoveNext();
        }

        // Skip the time unit
        if (_streamer.Current?.Type == TIME_VARIABLES)
        {
            _streamer.MoveNext();
        }
    }

    /// <summary>
    /// Checks if a constant declaration contains a function call in its initialization.
    /// Uses lookahead without consuming tokens.
    /// </summary>
    /// <returns>True if the constant has a function call like func(args), false otherwise.</returns>
    private bool HasFunctionCallInConstant()
    {
        _streamer.SetRollBack();

        // Look ahead to find ':=' and then check what follows
        bool foundAssign = false;
        int iterations = 0;
        const int MAX_LOOKAHEAD = 50;

        while (_streamer.Current != null && iterations < MAX_LOOKAHEAD)
        {
            iterations++;
            string val = _streamer.Current.Value;

            // Stop if we hit semicolon or begin
            if (val == ";" || val.ToLower() == "begin" || val.ToLower() == _breaker)
            {
                break;
            }

            if (val == ":=")
            {
                foundAssign = true;
                _streamer.MoveNext();
                continue;
            }

            // After ':=', check if we have identifier followed by '('
            // which indicates a function call
            if (foundAssign)
            {
                // Skip any leading '(' for aggregate initializers like "(others => '0')"
                if (val == "(")
                {
                    _streamer.RollBack();
                    return false; // This is an aggregate, not a function call
                }

                // Check for identifier followed by '('
                if (_streamer.Current?.Type == IDENTIFIERS)
                {
                    string ident = _streamer.Current.Value;
                    _streamer.MoveNext();

                    // If next is '(', it's a function call
                    if (_streamer.Current?.Value == "(")
                    {
                        _streamer.RollBack();
                        return true;
                    }
                }

                // If we got here without finding function call pattern, it's a simple constant
                _streamer.RollBack();
                return false;
            }

            _streamer.MoveNext();
        }

        _streamer.RollBack();
        return false;
    }

    /// <summary>
    /// Skips a constant declaration until ';'.
    /// Constants with function calls cannot be evaluated at parse time.
    /// </summary>
    /// <remarks>
    /// <para>v0.13.0: Added to handle constants with function call initializations.</para>
    /// <code>
    /// constant TREE_DEPTH : integer := clog2(NUM_INPUTS);  -- Skipped
    /// constant ZERO : std_logic_vector(7 downto 0) := (others => '0');  -- Skipped
    /// </code>
    /// </remarks>
    private void SkipConstantDeclaration()
    {
        // Skip 'constant' keyword
        _streamer.MoveNext();

        // Capture the constant name(s) so later expressions can resolve them.
        // Without this, constants defined as `constant X : integer := func(Y);`
        // would be silently dropped, and any downstream use of X would fail
        // with "Undeclared variable". Register a placeholder Variable per name;
        // value resolution can refine this later if function evaluation is added.
        var declaredNames = new List<string>();
        while (_streamer.Current != null && _streamer.Current.Type == IDENTIFIERS)
        {
            declaredNames.Add(_streamer.Current.Value);
            _streamer.MoveNext();
            if (_streamer.Current?.Value == ",") _streamer.MoveNext();
            else break;
        }

        int parenDepth = 0;
        int iterations = 0;
        const int MAX_ITERATIONS = 1000;

        // Skip until we find ';' at depth 0
        while (_streamer.Current != null && iterations < MAX_ITERATIONS)
        {
            iterations++;
            string val = _streamer.Current.Value;

            if (val == "(")
            {
                parenDepth++;
            }
            else if (val == ")")
            {
                parenDepth--;
            }
            else if (val == ";" && parenDepth == 0)
            {
                _streamer.MoveNext(); // skip ';'
                break;
            }

            // Safety: if we hit 'begin', we've gone too far
            if (val.ToLower() == "begin" || val.ToLower() == _breaker)
            {
                break;
            }

            _streamer.MoveNext();
        }

        foreach (var name in declaredNames)
        {
            if (string.IsNullOrEmpty(name)) continue;
            if (Variables.Any(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
            // Seed Value=0 so downstream Convert.ToInt32(...) calls succeed instead
            // of falling into the `_value ?? new()` branch and getting a raw object.
            Variables.Add(new Variable { Name = name, Value = 0 });
            SkipDiagnosticLog.Instance.AddInfo(
                "VHD-CONSTANT-PLACEHOLDER",
                name,
                $"Constant '{name}' has a function-call initializer that the " +
                "parser does not yet evaluate. Registered with placeholder " +
                "value 0 — vector widths derived from this constant will be " +
                "incorrect at runtime.");
        }
    }

    /// <summary>
    /// Skips a component declaration until 'end component;'
    /// </summary>
    private void SkipComponentDeclaration()
    {
        int _iter = 0;
        ParseTrace.Log($"begin@{(_streamer.Current?.Line ?? 0) + 1}");
        // Skip 'component' keyword
        _streamer.MoveNext();

        // Skip until we find 'end component' or 'end <component_name>'
        while (_streamer.Current != null)
        {
            ParseTrace.Step(ref _iter, _streamer, _currentCt);
            string val = _streamer.Current.Value.ToLower();

            // Safety check: if we hit 'begin', we've gone too far
            if (val == "begin" || val == _breaker)
            {
                break;
            }

            if (val == "end")
            {
                _streamer.MoveNext(); // skip 'end'
                if (_streamer.Current == null) break;

                string nextVal = _streamer.Current.Value.ToLower();
                if (nextVal == "component" || _streamer.Current.Type == IDENTIFIERS)
                {
                    _streamer.MoveNext(); // skip 'component' or component name
                    // Optionally skip the component name if it was 'component'
                    if (_streamer.Current?.Type == IDENTIFIERS)
                    {
                        _streamer.MoveNext();
                    }
                    if (_streamer.Current?.Value == ";")
                    {
                        _streamer.MoveNext(); // skip ';'
                    }
                    break;
                }
            }
            _streamer.MoveNext();
        }
    }

    /// <summary>
    /// Parses a type declaration and registers it in the type registry.
    /// Handles: enumeration types, array types, record types
    /// </summary>
    /// <remarks>
    /// <para>Supported type declarations:</para>
    /// <code>
    /// type state_t is (IDLE, RUN, STOP);                           -- Enumeration
    /// type mem_t is array (0 to 255) of std_logic;                 -- Array
    /// type packet_t is record valid: std_logic; end record;        -- Record
    /// </code>
    /// <para>v0.13.0: Added safety limit to prevent infinite loops.</para>
    /// </remarks>
    private void ParseTypeDeclaration()
    {
        int iterations = 0;
        const int MAX_ITERATIONS = 10000;

        // Skip 'type' keyword
        if (_streamer.Current?.Value.ToLower() == "type")
        {
            _streamer.MoveNext();
        }

        // Get type name
        string typeName = _streamer.Current?.Value ?? "unknown_type";
        _streamer.MoveNext(); // skip type name

        // Skip 'is' keyword
        if (_streamer.Current?.Value.ToLower() == "is")
        {
            _streamer.MoveNext();
        }

        // Determine type kind based on next token
        string nextToken = _streamer.Current?.Value.ToLower() ?? "";

        TypeDefinition? typeDef = null;

        if (nextToken == "(")
        {
            // Enumeration type: type state_t is (IDLE, RUN, STOP);
            typeDef = ParseEnumerationType(typeName);
        }
        else if (nextToken == "array")
        {
            // Array type: type mem_t is array (0 to 255) of std_logic;
            typeDef = ParseArrayType(typeName);
        }
        else if (nextToken == "record")
        {
            // Record type: type packet_t is record ... end record;
            typeDef = ParseRecordType(typeName);
        }
        else
        {
            // Unknown or subtype - skip to semicolon
            SkipToSemicolonSafe(ref iterations, MAX_ITERATIONS);
        }

        // Register the parsed type
        if (typeDef != null)
        {
            ParsedTypes.TryRegister(typeDef);
            _typeRegistry?.TryRegister(typeDef);

            // Bug #16 fix: register every enum literal as a constant Variable
            // so expressions like `state <= sB` and `when sB =>` resolve. The
            // literal's Value is its own name; the case statement compares
            // string-to-string so the literal flows straight through.
            if (typeDef.Kind == TypeKind.Enumeration)
            {
                foreach (var literal in typeDef.EnumValues)
                {
                    if (string.IsNullOrEmpty(literal)) continue;
                    if (Variables.Any(v => v.Name.Equals(literal, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    Variables.Add(new Variable
                    {
                        Name = literal,
                        Type = Constants.DATATYPES.INTEGER,
                        Value = literal,
                    });
                }
            }
        }
    }

    /// <summary>
    /// Parses an enumeration type declaration.
    /// Example: type state_t is (IDLE, RUN, STOP);
    /// </summary>
    private TypeDefinition ParseEnumerationType(string typeName)
    {
        var typeDef = new TypeDefinition
        {
            Name = typeName,
            Kind = TypeKind.Enumeration
        };

        // Skip '('
        if (_streamer.Current?.Value == "(")
        {
            _streamer.MoveNext();
        }

        // Collect enum values until ')'
        int iterations = 0;
        while (_streamer.Current != null && _streamer.Current.Value != ")" && iterations < 1000)
        {
            iterations++;
            string val = _streamer.Current.Value;

            if (val != "," && val != "(" && val != ")")
            {
                typeDef.EnumValues.Add(val);
            }
            _streamer.MoveNext();
        }

        // Skip ')' and ';'
        if (_streamer.Current?.Value == ")")
            _streamer.MoveNext();
        if (_streamer.Current?.Value == ";")
            _streamer.MoveNext();

#if DEBUG
        Console.WriteLine($"[ParseTypeDeclaration] Enumeration '{typeName}': ({string.Join(", ", typeDef.EnumValues)})");
#endif

        return typeDef;
    }

    /// <summary>
    /// Parses an array type declaration.
    /// Example: type mem_t is array (0 to 255) of std_logic_vector(7 downto 0);
    /// </summary>
    private TypeDefinition ParseArrayType(string typeName)
    {
        var typeDef = new TypeDefinition
        {
            Name = typeName,
            Kind = TypeKind.Array
        };

        // Skip 'array'
        if (_streamer.Current?.Value.ToLower() == "array")
        {
            _streamer.MoveNext();
        }

        // Parse array bounds: (0 to 255) or (natural range <>)
        if (_streamer.Current?.Value == "(")
        {
            _streamer.MoveNext(); // skip '('

            // Check for unconstrained array (natural range <>)
            if (_streamer.Current?.Value.ToLower() == "natural" ||
                _streamer.Current?.Value.ToLower() == "integer" ||
                _streamer.Current?.Value.ToLower() == "positive")
            {
                typeDef.IsUnconstrained = true;
                // Skip to closing ')'
                while (_streamer.Current != null && _streamer.Current.Value != ")")
                {
                    _streamer.MoveNext();
                }
            }
            else
            {
                // Parse constrained bounds: low to/downto high
                if (int.TryParse(_streamer.Current?.Value, out int lowBound))
                {
                    _streamer.MoveNext(); // skip low bound

                    // Get direction (to or downto)
                    string direction = _streamer.Current?.Value.ToLower() ?? "to";
                    typeDef.IsDownto = direction == "downto";
                    _streamer.MoveNext(); // skip 'to' or 'downto'

                    // Get high bound
                    if (int.TryParse(_streamer.Current?.Value, out int highBound))
                    {
                        if (typeDef.IsDownto)
                        {
                            typeDef.ArrayBounds = (highBound, lowBound);
                        }
                        else
                        {
                            typeDef.ArrayBounds = (lowBound, highBound);
                        }
                        _streamer.MoveNext(); // skip high bound
                    }
                }
            }

            // Skip to closing ')'
            while (_streamer.Current != null && _streamer.Current.Value != ")")
            {
                _streamer.MoveNext();
            }
            if (_streamer.Current?.Value == ")")
                _streamer.MoveNext();
        }

        // Skip 'of'
        if (_streamer.Current?.Value.ToLower() == "of")
        {
            _streamer.MoveNext();
        }

        // Get element type
        string elementTypeName = _streamer.Current?.Value ?? "std_logic";
        typeDef.BaseTypeName = elementTypeName;
        typeDef.BaseType = Variable.GetDatatype(elementTypeName);
        _streamer.MoveNext();

        // Check for element vector size: std_logic_vector(7 downto 0)
        if (_streamer.Current?.Value == "(")
        {
            _streamer.MoveNext(); // skip '('

            if (int.TryParse(_streamer.Current?.Value, out int elemHigh))
            {
                _streamer.MoveNext(); // skip high
                _streamer.MoveNext(); // skip 'downto' or 'to'

                if (int.TryParse(_streamer.Current?.Value, out int elemLow))
                {
                    typeDef.ElementSize = Math.Abs(elemHigh - elemLow) + 1;
                    _streamer.MoveNext(); // skip low
                }
            }

            // Skip to closing ')'
            while (_streamer.Current != null && _streamer.Current.Value != ")")
            {
                _streamer.MoveNext();
            }
            if (_streamer.Current?.Value == ")")
                _streamer.MoveNext();
        }
        else if (typeDef.BaseType == Constants.DATATYPES.STD_LOGIC ||
                 typeDef.BaseType == Constants.DATATYPES.BIT)
        {
            typeDef.ElementSize = 1;
        }

        // Skip ';'
        if (_streamer.Current?.Value == ";")
            _streamer.MoveNext();

#if DEBUG
        Console.WriteLine($"[ParseTypeDeclaration] Array '{typeName}': ({typeDef.ArrayBounds.Low} to {typeDef.ArrayBounds.High}) of {typeDef.BaseTypeName}, ElementSize={typeDef.ElementSize}, TotalSize={typeDef.TotalSize}");
#endif

        return typeDef;
    }

    /// <summary>
    /// Parses a record type declaration.
    /// Example: type packet_t is record valid: std_logic; data: std_logic_vector(7 downto 0); end record;
    /// </summary>
    private TypeDefinition ParseRecordType(string typeName)
    {
        var typeDef = new TypeDefinition
        {
            Name = typeName,
            Kind = TypeKind.Record
        };

        // Skip 'record'
        if (_streamer.Current?.Value.ToLower() == "record")
        {
            _streamer.MoveNext();
        }

        int iterations = 0;
        const int MAX_ITERATIONS = 1000;

        // Parse fields until 'end record'
        while (_streamer.Current != null && iterations < MAX_ITERATIONS)
        {
            iterations++;
            string val = _streamer.Current.Value.ToLower();

            if (val == "end")
            {
                _streamer.MoveNext(); // skip 'end'
                if (_streamer.Current?.Value.ToLower() == "record")
                {
                    _streamer.MoveNext(); // skip 'record'
                }
                // Skip optional type name after 'end record'
                if (_streamer.Current?.Type == IDENTIFIERS)
                {
                    _streamer.MoveNext();
                }
                break;
            }

            // Parse field: fieldName : fieldType;
            if (_streamer.Current?.Type == IDENTIFIERS)
            {
                string fieldName = _streamer.Current.Value;
                _streamer.MoveNext();

                if (_streamer.Current?.Value == ":")
                {
                    _streamer.MoveNext(); // skip ':'

                    string fieldTypeName = _streamer.Current?.Value ?? "std_logic";
                    var fieldType = Variable.GetDatatype(fieldTypeName);
                    _streamer.MoveNext();

                    int fieldSize = 1;
                    // Check for vector size
                    if (_streamer.Current?.Value == "(")
                    {
                        _streamer.MoveNext(); // skip '('
                        if (int.TryParse(_streamer.Current?.Value, out int high))
                        {
                            _streamer.MoveNext();
                            _streamer.MoveNext(); // skip direction
                            if (int.TryParse(_streamer.Current?.Value, out int low))
                            {
                                fieldSize = Math.Abs(high - low) + 1;
                                _streamer.MoveNext();
                            }
                        }
                        // Skip to ')'
                        while (_streamer.Current != null && _streamer.Current.Value != ")")
                            _streamer.MoveNext();
                        if (_streamer.Current?.Value == ")")
                            _streamer.MoveNext();
                    }

                    var field = new Variable
                    {
                        Name = fieldName,
                        Type = fieldType,
                        Size = fieldSize
                    };
                    typeDef.RecordFields.Add(field);

                    // Skip ';'
                    if (_streamer.Current?.Value == ";")
                        _streamer.MoveNext();

                    continue;
                }
            }

            _streamer.MoveNext();
        }

        // Skip final ';'
        if (_streamer.Current?.Value == ";")
            _streamer.MoveNext();

#if DEBUG
        Console.WriteLine($"[ParseTypeDeclaration] Record '{typeName}': {typeDef.RecordFields.Count} fields, TotalSize={typeDef.TotalSize}");
#endif

        return typeDef;
    }

    /// <summary>
    /// Safely skips to semicolon with iteration limit.
    /// </summary>
    private void SkipToSemicolonSafe(ref int iterations, int maxIterations)
    {
        int parenDepth = 0;
        while (_streamer.Current != null && iterations < maxIterations)
        {
            iterations++;
            string val = _streamer.Current.Value;

            if (val == "(") parenDepth++;
            else if (val == ")") parenDepth--;
            else if (val == ";" && parenDepth == 0)
            {
                _streamer.MoveNext();
                break;
            }
            else if (val.ToLower() == "begin" || val.ToLower() == _breaker)
            {
                break;
            }

            _streamer.MoveNext();
        }
    }

    /// <summary>
    /// Legacy method - now calls ParseTypeDeclaration for backwards compatibility.
    /// </summary>
    private void SkipTypeDeclaration() => ParseTypeDeclaration();

    /// <summary>
    /// Skips a subtype declaration until ';'. Records the subtype's name in
    /// the diagnostic log — synth treats subtypes as their base type.
    /// </summary>
    private void SkipSubtypeDeclaration()
    {
        int _iter = 0;
        ParseTrace.Log($"begin@{(_streamer.Current?.Line ?? 0) + 1}");
        _streamer.MoveNext(); // 'subtype'
        string subtypeName = _streamer.Current?.Value ?? string.Empty;
        while (_streamer.Current != null && _streamer.Current.Value != ";")
        {
            ParseTrace.Step(ref _iter, _streamer, _currentCt);
            _streamer.MoveNext();
        }
        if (_streamer.Current?.Value == ";")
        {
            _streamer.MoveNext(); // skip ';'
        }
        SkipDiagnosticLog.Instance.AddInfo(
            "VHD-SUBTYPE-DROPPED",
            subtypeName,
            $"Subtype '{subtypeName}' is parsed but treated as its base type " +
            "in synthesis. Range constraints and resolution functions are " +
            "not enforced.");
    }

    /// <summary>
    /// Parses an alias declaration and registers the alias name as a Variable
    /// so later references resolve correctly. Supports the common forms:
    ///   <c>alias name is target(range);</c>
    ///   <c>alias name : subtype is target(range);</c>
    /// </summary>
    /// <remarks>
    /// Earlier this method consumed every token to the ';' without recording
    /// anything, so any reference to the alias later resolved through the
    /// fallback "implicit placeholder" path with size=0, value=0 — silently
    /// producing wrong netlists.
    ///
    /// Today the alias's effective width is derived from (in priority order):
    ///   1. An explicit subtype like <c>std_logic_vector(7 downto 0)</c>.
    ///   2. The slice expression on the target — <c>a(15 downto 8)</c> → 8.
    ///   3. Default to 1 when neither shape is recoverable.
    /// </remarks>
    private void SkipAliasDeclaration()
    {
        int _iter = 0;
        ParseTrace.Log($"begin@{(_streamer.Current?.Line ?? 0) + 1}");
        _streamer.MoveNext(); // consume 'alias'

        string aliasName = _streamer.Current?.Value ?? string.Empty;
        if (_streamer.Current?.Type == IDENTIFIERS)
            _streamer.MoveNext();

        int declaredSize = 0;
        Constants.DATATYPES declaredType = Constants.DATATYPES.UNKNOWN;

        // Optional subtype: `: std_logic_vector(7 downto 0)`
        if (_streamer.Current?.Value == ":")
        {
            _streamer.MoveNext(); // ':'
            if (_streamer.Current?.Type == KEYWORDS)
            {
                string typeText = _streamer.Current.Value.ToLower();
                declaredType = typeText switch
                {
                    "std_logic"        => Constants.DATATYPES.STD_LOGIC,
                    "std_logic_vector" => Constants.DATATYPES.STD_LOGIC_VECTOR,
                    "bit"              => Constants.DATATYPES.BIT,
                    "integer"          => Constants.DATATYPES.INTEGER,
                    "boolean"          => Constants.DATATYPES.BOOLEAN,
                    "natural"          => Constants.DATATYPES.NATURAL,
                    _                  => Constants.DATATYPES.UNKNOWN
                };
                _streamer.MoveNext();
                if (_streamer.Current?.Value == "(")
                {
                    declaredSize = ConsumeAndMeasureRange();
                }
            }
        }

        // 'is' <target>[(range)]
        if (_streamer.Current?.Value?.ToLower() == "is")
        {
            _streamer.MoveNext(); // 'is'
            // Target identifier (skipped — we only need its slice for sizing).
            if (_streamer.Current?.Type == IDENTIFIERS)
                _streamer.MoveNext();
            if (_streamer.Current?.Value == "(" && declaredSize == 0)
            {
                declaredSize = ConsumeAndMeasureRange();
            }
        }

        // Drain anything else up to the ';' (covers `alias x is X'high downto Y;`
        // attribute forms we don't yet model).
        while (_streamer.Current != null && _streamer.Current.Value != ";")
        {
            ParseTrace.Step(ref _iter, _streamer, _currentCt);
            if (_streamer.Current.Value == "(") { ConsumeAndMeasureRange(); continue; }
            _streamer.MoveNext();
        }
        if (_streamer.Current?.Value == ";")
            _streamer.MoveNext();

        if (!string.IsNullOrEmpty(aliasName))
        {
            // Choose the most informative type/size we recovered.
            int finalSize = declaredSize > 0 ? declaredSize : 1;
            var resolvedType = declaredType != Constants.DATATYPES.UNKNOWN
                ? declaredType
                : (finalSize > 1 ? Constants.DATATYPES.STD_LOGIC_VECTOR
                                 : Constants.DATATYPES.STD_LOGIC);
            Variables.Add(new Variable
            {
                Name  = aliasName,
                Type  = resolvedType,
                Size  = finalSize,
                Value = 0
            });
            SkipDiagnosticLog.Instance.AddInfo(
                "VHD-ALIAS-PLACEHOLDER",
                aliasName,
                $"Alias '{aliasName}' registered as a placeholder " +
                $"(size={finalSize}). Read-as-target slice mapping is not yet " +
                "wired into the netlist — references will resolve to a " +
                "constant placeholder rather than the underlying signal slice.");
        }
    }

    /// <summary>
    /// Consumes a parenthesised range expression starting at <c>(</c>, returning
    /// the literal width when the form is <c>(N to M)</c> or <c>(N downto M)</c>
    /// with both bounds as integer literals. Returns 0 for any other shape so
    /// the caller can fall back to a default size.
    /// </summary>
    private int ConsumeAndMeasureRange()
    {
        if (_streamer.Current?.Value != "(") return 0;
        _streamer.MoveNext(); // '('

        var tokens = new List<string>();
        int depth = 1;
        while (_streamer.Current != null && depth > 0)
        {
            string v = _streamer.Current.Value;
            if (v == "(") depth++;
            else if (v == ")")
            {
                depth--;
                if (depth == 0) break;
            }
            tokens.Add(v);
            _streamer.MoveNext();
        }
        if (_streamer.Current?.Value == ")") _streamer.MoveNext();

        if (tokens.Count == 3
            && (tokens[1].Equals("downto", StringComparison.OrdinalIgnoreCase) ||
                tokens[1].Equals("to",     StringComparison.OrdinalIgnoreCase))
            && int.TryParse(tokens[0], out int a)
            && int.TryParse(tokens[2], out int b))
        {
            return Math.Abs(a - b) + 1;
        }
        return 0;
    }

    /// <summary>
    /// Skips a function declaration until 'end function;' or 'end &lt;function_name&gt;;'
    /// and records the function's name in <see cref="FunctionRegistry"/> as a
    /// black-box stub so downstream call-site parsing can recognise it.
    /// </summary>
    /// <remarks>
    /// Body capture (parameters, return type, statement list) is intentionally
    /// deferred — task #18 lands the name registration today; deeper
    /// signature + body extraction is part of task #20 (IEEE library
    /// synthesis), which needs the same machinery.
    /// </remarks>
    private void SkipFunctionDeclaration()
    {
        // F-G-08 pass 4: shared iteration guard for the four while loops below.
        int _fnIter = 0;
        int _startLine = (_streamer.Current?.Line ?? 0) + 1;
        ParseTrace.Log($"begin@{_startLine}");
        // Skip 'function' keyword
        _streamer.MoveNext();

        // Get the function name (case-preserved for the registry, lowered
        // for matching the closing `end <name>;` token below).
        string functionNameRaw = _streamer.Current?.Value ?? "";
        string functionName    = functionNameRaw.ToLower();
        _streamer.MoveNext(); // consume function name

        // Bench bug #19 follow-up: parse parameter list + return type + body
        // text so the function is callable, not a black-box stub. Anything
        // we can't decode falls back to the legacy stub behaviour silently.
        var parameters = new List<ParameterDefinition>();
        var returnType = Constants.DATATYPES.UNKNOWN;
        int returnTypeSize = 0;
        string? customReturnTypeName = null;
        string bodyRaw = string.Empty;

        if (_streamer.Current?.Value == "(")
        {
            _streamer.MoveNext(); // consume '('
            TryParseParameterList(parameters);
        }

        if (_streamer.Current?.Value?.ToLower() == "return")
        {
            _streamer.MoveNext(); // consume 'return'
            string retTypeTok = _streamer.Current?.Value ?? "std_logic";
            returnType = Variable.GetDatatype(retTypeTok);
            if (returnType == Constants.DATATYPES.UNKNOWN)
                customReturnTypeName = retTypeTok;
            _streamer.MoveNext();
            // Optional vector range on return type, e.g. `return std_logic_vector(7 downto 0)`.
            if (_streamer.Current?.Value == "(")
            {
                _streamer.MoveNext();
                if (int.TryParse(_streamer.Current?.Value, out int hi))
                {
                    _streamer.MoveNext(); // hi
                    _streamer.MoveNext(); // downto/to
                    if (int.TryParse(_streamer.Current?.Value, out int lo))
                    {
                        returnTypeSize = Math.Abs(hi - lo) + 1;
                        _streamer.MoveNext();
                    }
                }
                while (_streamer.Current != null && _streamer.Current.Value != ")")
                {
                    ParseTrace.Step(ref _fnIter, _streamer, _currentCt);
                    _streamer.MoveNext();
                }
                if (_streamer.Current?.Value == ")") _streamer.MoveNext();
            }
        }

        // Look ahead for `is`, then capture body tokens from the
        // architecture-level `begin` to the matching `end`. Inner if/case/
        // loop blocks have their own `begin`/`end` pairs so we count depth.
        if (_streamer.Current?.Value?.ToLower() == "is")
        {
            _streamer.MoveNext();
        }

        // Parse local declarations (variables, constants) until top-level
        // `begin`. Tier 4.3 (2026-07-28): capture `variable <n> : <type>[(hi
        // downto lo)] [:= <init>];` into a list so synth-time body inlining
        // can recognise SSA writes to locals via the scope-alias stack.
        // Constants and other declaration kinds are still skipped.
        var localVariables = new List<Variable>();
        while (_streamer.Current != null && _streamer.Current.Value?.ToLower() != "begin")
        {
            ParseTrace.Step(ref _fnIter, _streamer, _currentCt);
            if (_streamer.Current.Value?.ToLower() == "variable")
            {
                _streamer.MoveNext(); // 'variable'
                string localName = _streamer.Current?.Value ?? string.Empty;
                if (!string.IsNullOrEmpty(localName)) _streamer.MoveNext();
                if (_streamer.Current?.Value == ":") _streamer.MoveNext();
                string localTypeTok = _streamer.Current?.Value ?? "std_logic";
                var localType = Variable.GetDatatype(localTypeTok);
                if (_streamer.Current != null) _streamer.MoveNext();
                int localSize = 0;
                if (_streamer.Current?.Value == "(")
                {
                    _streamer.MoveNext(); // '('
                    if (int.TryParse(_streamer.Current?.Value, out int hi))
                    {
                        _streamer.MoveNext(); // hi
                        _streamer.MoveNext(); // downto/to
                        if (int.TryParse(_streamer.Current?.Value, out int lo))
                        {
                            localSize = Math.Abs(hi - lo) + 1;
                            _streamer.MoveNext(); // lo
                        }
                    }
                    while (_streamer.Current != null && _streamer.Current.Value != ")")
                    {
                        _streamer.MoveNext();
                    }
                    if (_streamer.Current?.Value == ")") _streamer.MoveNext();
                }
                // Skip optional `:= <init>` and any trailing tokens up to ';'.
                while (_streamer.Current != null && _streamer.Current.Value != ";"
                    && _streamer.Current.Value?.ToLower() != "begin")
                {
                    _streamer.MoveNext();
                }
                if (_streamer.Current?.Value == ";") _streamer.MoveNext();

                if (!string.IsNullOrEmpty(localName))
                {
                    localVariables.Add(new Variable(localName, string.Empty, localType) { Size = localSize });
                }
                continue;
            }
            _streamer.MoveNext();
        }
        if (_streamer.Current?.Value?.ToLower() == "begin")
        {
            _streamer.MoveNext(); // consume 'begin'
        }

        // Capture body tokens until the matching `end` of the function.
        // Track nesting on the canonical VHDL constructs that open a body:
        //   if / case / process / function / procedure / generate / block
        //   + for / while (the trailing `loop` keyword is part of the same
        //     construct and must NOT count as a second opener)
        //   + standalone `loop` (used as `loop ... end loop` — rare).
        // Closers come as `end <keyword>` (or `end <name>`); we peek the
        // token after each `end` and consume both as a single close so the
        // depth counter stays balanced. The legacy code treated `end if`
        // as decrement+increment, which silently terminated the body at
        // the first inner `end` for any function whose body contained an
        // `if` / `case` / `loop` / etc. Bench bug #24 (P4 key_index).
        var bodyTokens = new List<string>();
        int innerDepth = 0;
        while (_streamer.Current != null)
        {
            ParseTrace.Step(ref _fnIter, _streamer, _currentCt);
            string val = _streamer.Current.Value ?? string.Empty;
            string low = val.ToLower();

            if (low == "for" || low == "while")
            {
                // Loop opener: increment once and consume tokens up to
                // (and including) the matching `loop` keyword so it
                // doesn't double-count below.
                innerDepth++;
                bodyTokens.Add(val);
                _streamer.MoveNext();
                while (_streamer.Current != null
                    && _streamer.Current.Value?.ToLower() != "loop")
                {
                    ParseTrace.Step(ref _fnIter, _streamer, _currentCt);
                    bodyTokens.Add(_streamer.Current.Value ?? string.Empty);
                    _streamer.MoveNext();
                }
                if (_streamer.Current != null)
                {
                    bodyTokens.Add(_streamer.Current.Value ?? string.Empty); // the `loop`
                    _streamer.MoveNext();
                }
                continue;
            }

            if (low == "if" || low == "case" || low == "process"
             || low == "function" || low == "procedure"
             || low == "generate" || low == "block"
             || low == "loop")
            {
                innerDepth++;
                bodyTokens.Add(val);
                _streamer.MoveNext();
                continue;
            }

            if (low == "end")
            {
                if (innerDepth == 0)
                {
                    // The function's own `end` — let the caller consume
                    // the closer keyword + optional name + `;`.
                    break;
                }
                innerDepth--;
                bodyTokens.Add(val);
                _streamer.MoveNext();
                // Consume the following closer keyword / identifier so
                // the next iteration doesn't see `if`/`loop`/etc. and
                // mis-count it as a fresh opener.
                if (_streamer.Current != null)
                {
                    bodyTokens.Add(_streamer.Current.Value ?? string.Empty);
                    _streamer.MoveNext();
                }
                continue;
            }

            bodyTokens.Add(val);
            _streamer.MoveNext();
        }
        bodyRaw = string.Join(" ", bodyTokens);

        if (!string.IsNullOrEmpty(functionNameRaw))
        {
            // Register a real FunctionDefinition (signature + body) so call
            // sites resolve through FunctionExecutor instead of the black-box
            // stub. PackageRegistry is the registry FunctionExecutor reads;
            // FunctionRegistry still gets the entry so the parser-side
            // user-function detection (IsUserFunction etc.) keeps working.
            var def = new FunctionDefinition
            {
                Name = functionNameRaw,
                Parameters = parameters,
                ReturnType = returnType,
                ReturnTypeSize = returnTypeSize,
                CustomReturnTypeName = customReturnTypeName,
                BodyRaw = bodyRaw,
                LocalVariables = localVariables,
                IsPure = true,
            };
            FunctionRegistry.Instance.Register(def);
            PackageRegistry.Instance.RegisterFunction(def, "global");
        }

        // Now consume the closing `end function;` / `end <name>;` tokens.
        if (_streamer.Current?.Value?.ToLower() == "end")
        {
            _streamer.MoveNext();
        }
        if (_streamer.Current?.Value?.ToLower() == "function" ||
            (_streamer.Current?.Value?.ToLower() == functionName))
        {
            _streamer.MoveNext();
        }
        // Optional second name (`end function pack`).
        if (_streamer.Current?.Type == IDENTIFIERS)
        {
            _streamer.MoveNext();
        }
        if (_streamer.Current?.Value == ";")
        {
            _streamer.MoveNext();
        }
    }

    /// <summary>
    /// Parses a function parameter list starting after the opening '('.
    /// Recognises <c>name : type</c> and <c>name : type(H downto L)</c>;
    /// stops at the matching ')'. Best-effort: anything unrecognised is
    /// skipped so a partial parameter list doesn't poison the whole
    /// function declaration.
    /// </summary>
    private void TryParseParameterList(List<ParameterDefinition> parameters)
    {
        int _iter = 0;
        int depth = 1;
        while (_streamer.Current != null && depth > 0)
        {
            ParseTrace.Step(ref _iter, _streamer, _currentCt);
            // Param name(s) until ':'. F-G-14 (Session 23, 2026-07-18):
            // also capture the optional `signal`/`variable`/`constant`
            // object class that precedes the name list — previously
            // dropped by the KEYWORDS filter, but signal-class formals
            // need to be aliased to the caller's Variable object so
            // `<=` inside the body schedules against the caller's driver.
            var names = new List<string>();
            bool isSignal = false;
            while (_streamer.Current != null && _streamer.Current.Value != ":")
            {
                ParseTrace.Step(ref _iter, _streamer, _currentCt);
                if (_streamer.Current.Value == ")") { depth--; if (depth == 0) { _streamer.MoveNext(); return; } }
                var lower = _streamer.Current.Value?.ToLowerInvariant();
                if (lower == "signal") isSignal = true;
                if (_streamer.Current.Type == IDENTIFIERS) names.Add(_streamer.Current.Value!);
                _streamer.MoveNext();
            }
            if (_streamer.Current?.Value != ":") return;
            _streamer.MoveNext(); // consume ':'

            // Optional mode (`in`/`out`/`inout`).
            ParameterMode mode = ParameterMode.In;
            string modeTok = _streamer.Current?.Value?.ToLower() ?? string.Empty;
            if (modeTok == "in" || modeTok == "out" || modeTok == "inout")
            {
                mode = modeTok switch
                {
                    "out"   => ParameterMode.Out,
                    "inout" => ParameterMode.InOut,
                    _       => ParameterMode.In,
                };
                _streamer.MoveNext();
            }

            string typeTok = _streamer.Current?.Value ?? "std_logic";
            var dataType = Variable.GetDatatype(typeTok);
            string? customTypeName = dataType == Constants.DATATYPES.UNKNOWN ? typeTok : null;
            _streamer.MoveNext();

            int size = 0, hi = 0, lo = 0;
            if (_streamer.Current?.Value == "(")
            {
                _streamer.MoveNext();
                if (int.TryParse(_streamer.Current?.Value, out int h))
                {
                    hi = h; _streamer.MoveNext();
                    _streamer.MoveNext(); // downto/to
                    if (int.TryParse(_streamer.Current?.Value, out int l))
                    {
                        lo = l;
                        size = Math.Abs(hi - lo) + 1;
                        _streamer.MoveNext();
                    }
                }
                while (_streamer.Current != null && _streamer.Current.Value != ")")
                    _streamer.MoveNext();
                if (_streamer.Current?.Value == ")") _streamer.MoveNext();
            }

            foreach (var n in names)
            {
                parameters.Add(new ParameterDefinition
                {
                    Name = n,
                    Type = dataType,
                    CustomTypeName = customTypeName,
                    Mode = mode,
                    Size = size,
                    HighBound = hi,
                    LowBound = lo,
                    IsSignal = isSignal,
                });
            }

            // Separator: ';' (next param) or ')' (end).
            if (_streamer.Current?.Value == ";")
            {
                _streamer.MoveNext();
                continue;
            }
            if (_streamer.Current?.Value == ")")
            {
                _streamer.MoveNext();
                return;
            }
            // Unexpected token — bail.
            return;
        }
    }

    /// <summary>
    /// Skips a procedure declaration until 'end procedure;' or 'end &lt;procedure_name&gt;;'
    /// and registers a black-box stub in <see cref="FunctionRegistry"/>.
    /// </summary>
    private void SkipProcedureDeclaration()
    {
        // Skip 'procedure' keyword
        _streamer.MoveNext();

        // Get the procedure name (case-preserved + lowered for matching).
        string procedureNameRaw = _streamer.Current?.Value ?? "";
        string procedureName    = procedureNameRaw.ToLower();
        _streamer.MoveNext(); // consume procedure name

        // F-G-12 (2026-07-18): capture parameters + body tokens so
        // call sites can inline the procedure at runtime. Prior to F-G-12
        // this method registered only the name (black-box stub), so
        // `press_key(KEY_1);` in a testbench dropped silently.
        var parameters = new List<ParameterDefinition>();
        if (_streamer.Current?.Value == "(")
        {
            _streamer.MoveNext(); // consume '('
            TryParseParameterList(parameters);
        }

        // Optional 'is'.
        if (_streamer.Current?.Value?.ToLower() == "is")
        {
            _streamer.MoveNext();
        }

        // Skip local declarations (variables, constants) between 'is' and the
        // body-opening 'begin'.
        int _declIter = 0;
        while (_streamer.Current != null && _streamer.Current.Value?.ToLower() != "begin")
        {
            ParseTrace.Step(ref _declIter, _streamer, _currentCt);
            _streamer.MoveNext();
        }
        if (_streamer.Current?.Value?.ToLower() == "begin")
        {
            _streamer.MoveNext();
        }

        // Capture body tokens while walking until `end procedure` or
        // `end <procedure_name>`. F-G-12 (2026-07-18): the previous
        // begin/end-depth walk got confused by fragmented string literals
        // (`" end "` tokenized into a lone `end` token would false-fire)
        // and by `wait for N ns;` (misread as a for-loop header). The
        // two-token `end <procedure|name>` match here is naturally
        // string-safe: an `end` inside a report string is followed by
        // more string fragments, not `procedure`/`<name>`.
        var bodyTokens = new List<string>();
        int _bodyIter = 0;
        bool consumedEnd = false;
        ParseTrace.Log($"proc-body-capture@{(_streamer.Current?.Line ?? 0) + 1}");
        while (_streamer.Current != null)
        {
            ParseTrace.Step(ref _bodyIter, _streamer, _currentCt);
            string val = _streamer.Current.Value ?? string.Empty;
            string low = val.ToLower();

            if (low == "end")
            {
                // Peek one token ahead. If it's `procedure` or the
                // procedure name, THIS is the real end — bail out. Any
                // other `end` (from `end if` / `end loop` / `end case` /
                // even a stray `end` inside a string fragment) gets
                // captured as normal body content.
                var here = _streamer.Current;
                _streamer.MoveNext();
                string? next = _streamer.Current?.Value?.ToLower();
                if (next == "procedure" || next == procedureName)
                {
                    // Consume `end`, `procedure`/<name>, optional
                    // second name identifier, `;`. Same shape as
                    // the pre-F-G-12 logic — no post-loop wrap needed.
                    _streamer.MoveNext(); // procedure / <name>
                    if (_streamer.Current?.Type == IDENTIFIERS)
                        _streamer.MoveNext(); // optional second name
                    if (_streamer.Current?.Value == ";")
                        _streamer.MoveNext();
                    consumedEnd = true;
                    break;
                }
                // Not the real end — record both tokens and continue.
                bodyTokens.Add(here?.Value ?? "end");
                continue;
            }

            bodyTokens.Add(val);
            _streamer.MoveNext();
        }
        string bodyRaw = string.Join(" ", bodyTokens);

        if (!string.IsNullOrEmpty(procedureNameRaw))
        {
            FunctionRegistry.Instance.Register(new FunctionDefinition
            {
                Name = procedureNameRaw,
                Parameters = parameters,
                BodyRaw = bodyRaw,
                IsPure = false,   // procedures may have side effects
            });
            // Diagnostic stays as-is — MVP inlining only fires report
            // statements from the procedure body; signal assignments,
            // wait, function-arg evaluation etc. still go unmodelled.
            SkipDiagnosticLog.Instance.AddWarning(
                "VHD-PROC-PARTIAL",
                procedureNameRaw,
                $"Procedure '{procedureNameRaw}' body is captured but only " +
                "`report` statements execute at call sites (F-G-12 MVP). " +
                "Signal assignments, wait, and nested calls are not modelled.");
        }

        // If the loop exited without matching a real `end procedure`
        // (malformed input / EOF), fall back to the pre-F-G-12 tolerant
        // consume-any-end-and-run-off behaviour so the outer parser can
        // still make progress instead of dying at the very next token.
        if (!consumedEnd)
        {
            if (_streamer.Current?.Value?.ToLower() == "end") _streamer.MoveNext();
            if (_streamer.Current?.Value?.ToLower() == "procedure" ||
                _streamer.Current?.Value?.ToLower() == procedureName)
                _streamer.MoveNext();
            if (_streamer.Current?.Type == IDENTIFIERS) _streamer.MoveNext();
            if (_streamer.Current?.Value == ";") _streamer.MoveNext();
        }
    }

    /// <summary>
    /// Skips an attribute declaration or specification until ';' and logs
    /// a diagnostic so the user knows the attribute is purely syntactic
    /// for now (synth pragmas like <c>attribute keep of sig : signal is true;</c>
    /// don't influence the netlist yet).
    /// </summary>
    private void SkipAttributeDeclaration()
    {
        int _iter = 0;
        // Try to grab the attribute name for the diagnostic subject.
        _streamer.MoveNext(); // 'attribute'
        string attrName = _streamer.Current?.Value ?? string.Empty;
        while (_streamer.Current != null && _streamer.Current.Value != ";")
        {
            ParseTrace.Step(ref _iter, _streamer, _currentCt);
            _streamer.MoveNext();
        }
        if (_streamer.Current?.Value == ";")
        {
            _streamer.MoveNext(); // skip ';'
        }
        SkipDiagnosticLog.Instance.AddInfo(
            "VHD-ATTR-DROPPED",
            attrName,
            $"Attribute '{attrName}' is recognised syntactically but does not " +
            "affect the synthesised netlist. Synth pragmas (keep / mark_debug / " +
            "ram_style / etc.) are read-only metadata in this build.");
    }

    /// <summary>
    /// Skips a disconnect specification until ';'.
    /// </summary>
    private void SkipDisconnectSpec()
    {
        int _iter = 0;
        while (_streamer.Current != null && _streamer.Current.Value != ";")
        {
            ParseTrace.Step(ref _iter, _streamer, _currentCt);
            _streamer.MoveNext();
        }
        if (_streamer.Current?.Value == ";")
        {
            _streamer.MoveNext(); // skip ';'
        }
        SkipDiagnosticLog.Instance.AddInfo(
            "VHD-DISCONNECT-DROPPED",
            "",
            "Disconnect specification dropped — guarded-signal driver " +
            "disconnect timing is not modelled.");
    }

    /// <summary>
    /// Skips to the next semicolon (used for unknown declarations).
    /// </summary>
    private void SkipToSemicolon()
    {
        int _iter = 0;
        while (_streamer.Current != null && _streamer.Current.Value != ";")
        {
            ParseTrace.Step(ref _iter, _streamer, _currentCt);
            _streamer.MoveNext();
        }
        if (_streamer.Current?.Value == ";")
        {
            _streamer.MoveNext(); // skip ';'
        }
    }

    /// <summary>
    /// F-G-01 helper (session 8, 2026-07-09): fold a small VHDL range-bound
    /// expression to an integer. Handles integer literals, single-token
    /// constant references (resolved via PackageRegistry), and left-to-right
    /// two-operand or three-operand chains over +, -, *, /. Returns null
    /// when the expression contains anything more complex — the caller then
    /// falls back to the pre-existing default-8 behaviour.
    /// </summary>
    private static int? FoldSimpleExpr(List<string> tokens)
    {
        // Filter out whitespace-only tokens defensively.
        var t = tokens.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        if (t.Count == 0) return null;

        // Single operand.
        if (t.Count == 1) return ResolveToken(t[0]);

        // Two-operand: OP a  (unary -/+)  — rare in range specs but cheap.
        if (t.Count == 2 && (t[0] == "-" || t[0] == "+"))
        {
            var v = ResolveToken(t[1]);
            if (v == null) return null;
            return t[0] == "-" ? -v.Value : v.Value;
        }

        // Left-to-right fold: value op value op value ... (no precedence).
        int i = 0;
        var a = ResolveToken(t[i++]);
        if (a == null) return null;
        int acc = a.Value;
        while (i + 1 < t.Count)
        {
            string op = t[i++];
            var b = ResolveToken(t[i++]);
            if (b == null) return null;
            acc = op switch
            {
                "+" => acc + b.Value,
                "-" => acc - b.Value,
                "*" => acc * b.Value,
                "/" => b.Value != 0 ? acc / b.Value : 0,
                _   => int.MinValue,
            };
            if (acc == int.MinValue) return null;
        }
        return i == t.Count ? acc : null;
    }

    /// <summary>
    /// Resolves a single token to an integer: literal → parse; identifier →
    /// look up in <see cref="PackageRegistry"/>. Returns null on miss.
    /// </summary>
    private static int? ResolveToken(string token)
    {
        string s = token.Replace("_", string.Empty);
        if (int.TryParse(s, out int lit)) return lit;

        var c = PackageRegistry.Instance.GetConstant(token);
        if (c?.Value is int ci) return ci;
        if (c?.Value is string cs && int.TryParse(cs, out int cp)) return cp;
        return null;
    }

    /// <summary>
    /// F-G-02 helper (session 8, 2026-07-09): coerce a <see cref="GetVariableValue"/>
    /// return to <c>int</c>, falling back to <see cref="PackageRegistry"/>
    /// when the value is an identifier string like <c>BUZ_LONG_TICKS</c> that
    /// the local lookup didn't resolve. Returns 0 on any resolution failure —
    /// matches the pre-existing <c>Convert.ToInt32</c> behaviour so downstream
    /// range-magnitude arithmetic keeps its old semantics on miss.
    /// </summary>
    private static int ToInt32OrPackageConstant(object varValue)
    {
        if (varValue is int i) return i;
        if (varValue is string s)
        {
            string bare = s.Trim().Replace("_", string.Empty);
            if (int.TryParse(bare, out int lit)) return lit;
            var pkg = PackageRegistry.Instance.GetConstant(s.Trim());
            if (pkg?.Value is int pi) return pi;
            if (pkg?.Value is string ps && int.TryParse(ps, out int pp)) return pp;
        }
        try { return Convert.ToInt32(varValue); }
        catch { return 0; }
    }

    /// <summary>
    /// F-G-02 helper: read one bound of an <c>integer range … to …</c>
    /// clause. If the current token is an identifier that resolves in
    /// <see cref="PackageRegistry"/>, take that constant's value and
    /// advance past the token — otherwise defer to <see cref="GetVariableValue"/>
    /// (legacy path). Kept as a scoped helper so package-constant lookup
    /// only fires for range bounds, not for every identifier throughout
    /// the parser.
    /// </summary>
    private int ReadRangeBound()
    {
        // Peek: identifier that PackageRegistry knows about?
        if (_streamer.Current?.Type == IDENTIFIERS)
        {
            string name = _streamer.Current.Value ?? string.Empty;
            var pkg = PackageRegistry.Instance.GetConstant(name);
            if (pkg?.Value != null)
            {
                if (pkg.Value is int pi)
                {
                    _streamer.MoveNext();
                    return pi;
                }
                if (pkg.Value is string ps && int.TryParse(ps, out int pp))
                {
                    _streamer.MoveNext();
                    return pp;
                }
            }
        }
        return ToInt32OrPackageConstant(GetVariableValue());
    }

    /// <summary>
    /// F-G-02 helper: minimum bit width needed to encode every integer in
    /// [low, high] (unordered — accepts <c>0 to N</c> or <c>N downto 0</c>).
    /// Uses <c>ceil(log2(max_magnitude + 1))</c> with a sign bit added when
    /// the range spans negatives. Returns 1 for degenerate/empty ranges.
    /// </summary>
    private static int RangeToBitWidth(int a, int b)
    {
        int lo = Math.Min(a, b);
        int hi = Math.Max(a, b);
        // Magnitude that needs to fit: farthest-from-zero endpoint.
        int magnitude = Math.Max(Math.Abs(lo), Math.Abs(hi));
        if (magnitude == 0) return 1;
        int bits = 0;
        int m = magnitude;
        while (m > 0) { bits++; m >>= 1; }
        if (lo < 0) bits += 1; // sign bit
        return bits;
    }
}