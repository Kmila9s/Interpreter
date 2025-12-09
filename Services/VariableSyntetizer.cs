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
            Constants.PortType portType = _isPort || _isGeneric ? _streamer.Current?.Value.ToLower() == "in" ?
                Constants.PortType.IN : Constants.PortType.OUT
                    : varType.ToLower() == "constant" ?
                Constants.PortType.IN : Constants.PortType.OUT;
            if (_isPort)
                _streamer.Expect(KEYWORDS); // IN/OUT/INOUT Type
            var dataType = Variable.GetDatatype(_streamer.Current?.Value ?? string.Empty);
            string dataTypeStr = _streamer.Current?.Value?.ToLower() ?? string.Empty;

            // Check if it's a custom type in the registry
            TypeDefinition? customTypeDef = null;
            if (dataType == Constants.DATATYPES.UNKNOWN)
            {
                // Try to resolve from type registry
                customTypeDef = _typeRegistry?.Get(dataTypeStr) ?? ParsedTypes.Get(dataTypeStr);
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
                SkipToSemicolon();
                continue;
            }
            // For ports with unknown type, try to skip gracefully
            if (dataType == Constants.DATATYPES.UNKNOWN && customTypeDef == null && (_isPort || _isGeneric))
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

                    // Check if this is a complex expression (contains operators or nested parens)
                    bool isComplexExpression = false;
                    if (_isPort || _isGeneric)
                    {
                        _streamer.SetRollBack();
                        // Peek ahead to see if expression contains operators or nested parens
                        int checkDepth = 0;
                        while (_streamer.Current != null && _streamer.Current.Value != ")")
                        {
                            if (_streamer.Current.Value == "(")
                            {
                                isComplexExpression = true;
                                break;
                            }
                            string[] operators = { "+", "-", "*", "**", "/" };
                            if (operators.Contains(_streamer.Current.Value))
                            {
                                isComplexExpression = true;
                                break;
                            }
                            _streamer.MoveNext();
                        }
                        _streamer.RollBack();
                    }

                    if (isComplexExpression)
                    {
#if DEBUG
                        Console.WriteLine($"[VariableSyntetizer] Complex vector expression detected, using default size");
#endif
                        // Skip complex expression and use default size
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
                        VectorSize = 8; // Default size for complex expressions
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
                }
                // For records, use STD_LOGIC_VECTOR as storage type
                else if (customTypeDef.Kind == TypeKind.Record)
                {
                    dataType = Constants.DATATYPES.STD_LOGIC_VECTOR;
                }
                _streamer.Expect(KEYWORDS); // consume type name
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

                var newVar = new Variable()
                {
                    Name = variable.Name,
                    Type = dataType,
                    IsPort = _isPort,
                    PortType = portType,
                    Size = VectorSize,  // Set Size BEFORE Value to avoid Size setter issues
                    Value = !_isPort ? initValue : null!,
                };

                // Store custom type name reference for debugging/analysis
                if (customTypeDef != null)
                {
                    // We could add a CustomTypeName property to Variable if needed
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
                throw ParserException.UndeclaredVariable(
                    _streamer.Current?.Value ?? "unknown",
                    _streamer.Current);
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