using Interpreter.Interfaces;
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
    private HashSet<Variable> _comparator;
    private readonly string _breaker = "begin";
    private bool _isPort, _isGeneric, _isSignal;
    private TokenStream _streamer;

    /// <summary>
    /// Initializes a new instance of the <see cref="VariableSyntetizer"/> class.
    /// </summary>
    /// <param name="Streamer">The token stream.</param>
    /// <param name="Comparator">The set of variables to compare against.</param>
    /// <param name="IsPort">Whether the variable is a port.</param>
    /// <param name="IsGeneric">Whether the variable is a generic.</param>
    /// <param name="Breaker">The token that breaks the synthesis.</param>
    /// <param name="IsSignal">Whether the variable is a signal.</param>
    public VariableSyntetizer(TokenStream Streamer, HashSet<Variable> Comparator, bool IsPort = false, bool IsGeneric = false, string Breaker = "begin", bool IsSignal = false)
    {
        _streamer = Streamer;
        _isPort = IsPort;
        _isSignal = IsSignal;
        _isGeneric = IsGeneric;
        _comparator = Comparator;
        _breaker = _isPort || _isGeneric ? ")" : Breaker.ToLower();
    }

    /// <summary>
    /// Executes the variable synthesizer asynchronously.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task ExecuteAsync() => throw new NotImplementedException();

    /// <summary>
    /// Synthesizes the variables from a token stream.
    /// </summary>
    /// <param name="strings">The token stream.</param>
    public void Syntetize(string[] strings) => ExecuteAsync();

    /// <summary>
    /// Synthesizes the variables from a token stream.
    /// </summary>
    public void Syntetize()
    {
        while (_streamer.Current != null && _breaker != _streamer.Current?.Value.ToLower())
        {
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
                    throw new ParserException(Constants.ParseExceptions.DUPLICATE_DECLARATION, (int)_streamer.Current?.Line + 1, (int)_streamer.Current?.Line + 1);
                }
                // Note: Don't check if varName is in _comparator here - we're DECLARING the variable,
                // not using it. Forward reference checks belong in initializer parsing.
                variables.Add(new() { Name = varName });
            }
            _streamer.Expect(OPERATORS); // :
            Constants.PortType portType = _isPort || _isGeneric ? _streamer.Current?.Value.ToLower() == "in" ?
                Constants.PortType.IN : Constants.PortType.OUT
                    : varType.ToLower() == "constant" ?
                Constants.PortType.IN : Constants.PortType.OUT;
            if (_isPort)
                _streamer.Expect(KEYWORDS); // IN/OUT/INOUT Type
            var dataType = Variable.GetDatatype(_streamer.Current?.Value ?? string.Empty);
            string dataTypeStr = _streamer.Current?.Value?.ToLower() ?? string.Empty;

            // If datatype is UNKNOWN, skip this declaration (custom type)
            if (dataType == Constants.DATATYPES.UNKNOWN && !_isPort && !_isGeneric)
            {
                SkipToSemicolon();
                continue;
            }
            // For ports with unknown type, try to skip gracefully
            if (dataType == Constants.DATATYPES.UNKNOWN && (_isPort || _isGeneric))
            {
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
            _streamer.Expect(KEYWORDS);
            int VectorSize = 0;

            // Handle vector types with parentheses (std_logic_vector, bit_vector, signed, unsigned, etc.)
            if (dataType == Constants.DATATYPES.STD_LOGIC_VECTOR ||
                dataTypeStr == "bit_vector" || dataTypeStr == "signed" || dataTypeStr == "unsigned" ||
                dataTypeStr == "std_ulogic_vector" || dataTypeStr == "boolean_vector" || dataTypeStr == "integer_vector")
            {
                if (_streamer.Current?.Value == "(")
                {
                    // Parse vector range (7 downto 0) or (0 to 7)
                    // For ports/generics, skip evaluation of complex expressions (v0.13.0 fix)
                    // This prevents errors when expressions use generics or functions like clog2()
                    // Example: std_logic_vector((NUM_INPUTS * DATA_WIDTH) - 1 downto 0)
                    if (_isPort || _isGeneric)
                    {
                        // Skip the entire range specification without evaluating
                        _streamer.Expect(PARENTHESIS); // consume '('
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
                        VectorSize = 8; // Default size for ports/generics with complex expressions
                    }
                    else
                    {
                        _streamer.Expect(PARENTHESIS);
                        object varValue = GetVariableValue();
                        int initial = Convert.ToInt32(varValue);
                        string vecType = _streamer.Current?.Value ?? string.Empty;
                        _streamer.Expect(KEYWORDS);
                        varValue = GetVariableValue();
                        int end = Convert.ToInt32(varValue);
                        _streamer.Expect(PARENTHESIS);
                        VectorSize = vecType.ToLower() == "downto" ? initial - end + 1 : end - initial + 1;
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
                    object varValue = GetVariableValue();
                    int initial = Convert.ToInt32(varValue);
                    _streamer.Expect(KEYWORDS); // consume "to"
                    varValue = GetVariableValue();
                    int end = Convert.ToInt32(varValue);
                    VectorSize = Math.Abs(end - initial);
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

            object value = new();
            // Handle initialization with :=
            if (!_isPort && _streamer.Current?.Value == ":=")
            {
                _streamer.Expect(OPERATORS); // consume ":="
                value = ParseInitializationValue();
            }
            // Handle "after" clause (skip it for now - temporal simulation not implemented)
            if (_streamer.Current?.Value?.ToLower() == "after")
            {
                SkipAfterClause();
            }
            variables.ForEach(variable =>
            {
                Variables.Add(new()
                {
                    Name = variable.Name,
                    Type = dataType,
                    IsPort = _isPort,
                    PortType = portType,
                    Value = !_isPort ? value : null!,
                    Size = VectorSize,
                });
            });
            if ((_isPort || _isGeneric) && _streamer.Current?.Value != ";") break;
            _streamer.Expect(OPERATORS); // ;
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

            varValue = _comparator.FirstOrDefault(p => p.Name == _streamer.Current?.Value!)?.Value!;
            if (varValue == null)
            {
                throw new Exception($"Variable {_streamer.Current.Value} not found {_streamer.Current.Line + 1} : {_streamer.Current.Col + 1}");
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
    /// Parses an aggregate expression like (others => '0') or a parenthesized expression.
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
        else
        {
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
    /// Skips a component declaration until 'end component;'
    /// </summary>
    private void SkipComponentDeclaration()
    {
        // Skip 'component' keyword
        _streamer.MoveNext();

        // Skip until we find 'end component' or 'end <component_name>'
        while (_streamer.Current != null)
        {
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
    /// Skips a type declaration until ';'
    /// </summary>
    private void SkipTypeDeclaration()
    {
        // Type declarations can be complex (records, arrays, etc.)
        // For simplicity, skip until we see 'is' followed by not 'record'/'array', or 'end record'
        int depth = 0;
        while (_streamer.Current != null)
        {
            string val = _streamer.Current.Value.ToLower();
            if (val == "record" || val == "array")
            {
                depth++;
            }
            else if (val == "end")
            {
                _streamer.MoveNext();
                if (_streamer.Current?.Value.ToLower() == "record")
                {
                    depth--;
                    _streamer.MoveNext(); // skip 'record'
                    // Skip optional type name
                    if (_streamer.Current?.Type == IDENTIFIERS)
                    {
                        _streamer.MoveNext();
                    }
                }
                if (depth == 0)
                {
                    // Skip semicolon
                    if (_streamer.Current?.Value == ";")
                    {
                        _streamer.MoveNext();
                    }
                    break;
                }
                continue;
            }
            else if (val == ";" && depth == 0)
            {
                _streamer.MoveNext(); // skip ';'
                break;
            }
            _streamer.MoveNext();
        }
    }

    /// <summary>
    /// Skips a subtype declaration until ';'
    /// </summary>
    private void SkipSubtypeDeclaration()
    {
        while (_streamer.Current != null && _streamer.Current.Value != ";")
        {
            _streamer.MoveNext();
        }
        if (_streamer.Current?.Value == ";")
        {
            _streamer.MoveNext(); // skip ';'
        }
    }

    /// <summary>
    /// Skips an alias declaration until ';'
    /// </summary>
    private void SkipAliasDeclaration()
    {
        while (_streamer.Current != null && _streamer.Current.Value != ";")
        {
            _streamer.MoveNext();
        }
        if (_streamer.Current?.Value == ";")
        {
            _streamer.MoveNext(); // skip ';'
        }
    }

    /// <summary>
    /// Skips a function declaration until 'end function;' or 'end &lt;function_name&gt;;'
    /// </summary>
    private void SkipFunctionDeclaration()
    {
        // Skip 'function' keyword
        _streamer.MoveNext();

        // Get the function name for matching 'end <function_name>'
        string functionName = _streamer.Current?.Value?.ToLower() ?? "";
#if DEBUG
        Console.WriteLine($"[SkipFunction] Skipping function: {functionName}");
#endif

        // Simply look for 'end function' or 'end <function_name>'
        while (_streamer.Current != null)
        {
            string val = _streamer.Current.Value.ToLower();

            if (val == "end")
            {
                _streamer.MoveNext(); // skip 'end'
                if (_streamer.Current == null) break;

                string nextVal = _streamer.Current.Value.ToLower();
#if DEBUG
                Console.WriteLine($"[SkipFunction] Found 'end' followed by: {nextVal}");
#endif

                // Check if this is the function end
                if (nextVal == "function" || nextVal == functionName)
                {
#if DEBUG
                    Console.WriteLine($"[SkipFunction] Matched function end! Current: '{_streamer.Current?.Value}'");
#endif
                    _streamer.MoveNext(); // skip 'function' or function name
#if DEBUG
                    Console.WriteLine($"[SkipFunction] After skipping func name: '{_streamer.Current?.Value}'");
#endif
                    // Skip optional second function name after 'end function'
                    if (_streamer.Current?.Type == IDENTIFIERS)
                    {
#if DEBUG
                        Console.WriteLine($"[SkipFunction] Skipping identifier: '{_streamer.Current?.Value}'");
#endif
                        _streamer.MoveNext();
                    }
#if DEBUG
                    Console.WriteLine($"[SkipFunction] Before checking semicolon: '{_streamer.Current?.Value}'");
#endif
                    if (_streamer.Current?.Value == ";")
                    {
#if DEBUG
                        Console.WriteLine($"[SkipFunction] Skipping semicolon");
#endif
                        _streamer.MoveNext(); // skip ';'
                    }
#if DEBUG
                    Console.WriteLine($"[SkipFunction] Final current token: '{_streamer.Current?.Value}' at line {_streamer.Current?.Line}");
#endif
                    return;
                }
                // Other 'end' statements - just continue (don't move next again, we already moved)
                continue;
            }
            _streamer.MoveNext();
        }
    }

    /// <summary>
    /// Skips a procedure declaration until 'end procedure;' or 'end &lt;procedure_name&gt;;'
    /// </summary>
    private void SkipProcedureDeclaration()
    {
        // Skip 'procedure' keyword
        _streamer.MoveNext();

        // Get the procedure name for matching 'end <procedure_name>'
        string procedureName = _streamer.Current?.Value?.ToLower() ?? "";

        // Simply look for 'end procedure' or 'end <procedure_name>'
        while (_streamer.Current != null)
        {
            string val = _streamer.Current.Value.ToLower();

            if (val == "end")
            {
                _streamer.MoveNext(); // skip 'end'
                if (_streamer.Current == null) break;

                string nextVal = _streamer.Current.Value.ToLower();

                // Check if this is the procedure end
                if (nextVal == "procedure" || nextVal == procedureName)
                {
                    _streamer.MoveNext(); // skip 'procedure' or procedure name
                    // Skip optional second procedure name after 'end procedure'
                    if (_streamer.Current?.Type == IDENTIFIERS)
                        _streamer.MoveNext();
                    if (_streamer.Current?.Value == ";")
                        _streamer.MoveNext(); // skip ';'
                    return;
                }
            }
            _streamer.MoveNext();
        }
    }

    /// <summary>
    /// Skips an attribute declaration or specification until ';'
    /// </summary>
    private void SkipAttributeDeclaration()
    {
        while (_streamer.Current != null && _streamer.Current.Value != ";")
        {
            _streamer.MoveNext();
        }
        if (_streamer.Current?.Value == ";")
        {
            _streamer.MoveNext(); // skip ';'
        }
    }

    /// <summary>
    /// Skips a disconnect specification until ';'
    /// </summary>
    private void SkipDisconnectSpec()
    {
        while (_streamer.Current != null && _streamer.Current.Value != ";")
        {
            _streamer.MoveNext();
        }
        if (_streamer.Current?.Value == ";")
        {
            _streamer.MoveNext(); // skip ';'
        }
    }

    /// <summary>
    /// Skips to the next semicolon (used for unknown declarations).
    /// </summary>
    private void SkipToSemicolon()
    {
        while (_streamer.Current != null && _streamer.Current.Value != ";")
        {
            _streamer.MoveNext();
        }
        if (_streamer.Current?.Value == ";")
        {
            _streamer.MoveNext(); // skip ';'
        }
    }
}