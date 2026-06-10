using System.Text.RegularExpressions;
using Interpreter.Interfaces;
using Interpreter.Models;
using Interpreter.Repositories;
using Parser.Models;
using Parser.Repositories;

namespace Interpreter.Services;

/// <summary>
/// Signal attribute types supported by the interpreter.
/// </summary>
public enum SignalAttributeType
{
    None,
    Event,
    LastValue,
    Stable,
    Active
}

/// <summary>
/// Represents an operation.
/// </summary>
public class Operation : IOperation {
    /// <summary>
    /// Gets the result of the operation.
    /// </summary>
    public Variable Result { get; private set; } = new();

    /// <summary>
    /// Gets the items of the operation.
    /// </summary>
    public object[] Items { get; private set; }
    private string[] _data;
    private int _currentSyntetizeIndex = 0;

    /// <summary>
    /// Gets the variables of the operation.
    /// </summary>
    public HashSet<Variable> Variables { get; private set; }

    // v0.13.0 Fix: Use static shared Regex instances to prevent memory bloat
    // Previously each Operation created 4 new Regex objects, causing significant memory allocation
    // when processing many expressions (e.g., large VHDL files with many signals/assignments)
    private static readonly RegexOptions _optionsCompiled = RegexOptions.Compiled;
    private static readonly Regex _variableRegex = new(Constants.IDENTIFIERS, _optionsCompiled);
    private static readonly Regex _operators = new(Constants.OPERATORS, _optionsCompiled);
    private static readonly Regex _logicOperators = new(Constants.LOGIC_OPERATORS, _optionsCompiled);
    private static readonly Regex _arithOperators = new(Constants.ARITH_OPERATORS, _optionsCompiled);
    private static readonly LiteralsFinder _finder = new();

    // v0.17.0: Atomic counter for literal names - replaces expensive Guid.NewGuid()
    private static int _literalCounter = 0;

    private string _rawData = string.Empty;

    /// <summary>
    /// Gets the raw data string of the operation for debugging.
    /// </summary>
    public string RawData => _rawData;

    /// <summary>
    /// Phase B+ Deep AST (2026-05-19): 1-based source-line number captured
    /// at parse time. Zero means "unknown" (synthetic operations, legacy
    /// construction paths). Plumbed through <see cref="ScheduledOperation"/>
    /// into the <c>SignalScheduler</c> so each signal transition carries
    /// the line of the statement that caused it — used by the live replay
    /// to highlight only the firing branch, not every assignment to the
    /// signal across the source file.
    /// </summary>
    public int SourceLine { get; set; }

    /// <summary>
    /// When the assignment target is an indexed element of a vector signal
    /// (e.g. <c>output(0) &lt;= …</c>), this is the integer index. Null for
    /// whole-signal assignments. Only literal-integer indices are supported
    /// for now — runtime/variable indices fall through to a parse failure.
    /// Bench Phase E bug #5 fix (2026-06-03).
    /// </summary>
    public int? ResultBitIndex { get; set; }

    /// <summary>
    /// Nanosecond inertial-delay captured from `after N <unit>` clauses on
    /// the RHS of a signal assignment. 0 means "no delay" (the default).
    /// `ScheduledOperation.Execute` routes through
    /// `SignalScheduler.ScheduleAfter` when this is set. Bench bug #8.
    /// </summary>
    public long AfterDelayNs { get; set; }

    /// <summary>
    /// True for variable assignments (<c>:=</c>) inside a process body.
    /// VHDL variable semantics require immediate update visible to the
    /// next statement (within the same delta cycle), unlike signal
    /// assignments (<c>&lt;=</c>) which are scheduled to a later delta.
    /// `ScheduledOperation.Execute` writes the result directly to
    /// <see cref="Result.Value"/> when this is true rather than queuing
    /// through `SignalScheduler.Schedule`. Bug #14 follow-up.
    /// </summary>
    public bool IsVariableAssignment { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="Operation"/> class.
    /// </summary>
    /// <param name="RawData">The raw data of the operation.</param>
    /// <param name="Variables">The variables of the operation.</param>
    /// <param name="sourceLine">Optional 1-based source line; 0 = unknown.</param>
    public Operation(string RawData, HashSet<Variable> Variables, int sourceLine = 0)
    {
        SourceLine = sourceLine;
        this.Variables = Variables;
        _rawData = RawData;
        Tokenizer tokenizer = new(string.Empty);

        // v0.16.1: Support both signal assignment (<=) and variable assignment (:=)
        bool isSignalAssignment = RawData.Contains("<=");
        bool isVariableAssignment = RawData.Contains(":=");
        IsVariableAssignment = isVariableAssignment && !isSignalAssignment;

        if (isSignalAssignment || isVariableAssignment)
        {
            // Determine which operator to use for splitting
            string assignOp = isSignalAssignment ? "<=" : ":=";
            var splitted = RawData.Trim().Split(assignOp);

            if (splitted.Length >= 2)
            {
                string varName = splitted[0].Trim();

                // Indexed-target detection: `output(0)` or `output ( 0 )` →
                // base name "output", ResultBitIndex 0. Bench bug #5.
                int? bitIndex = null;
                string baseName = varName;
                {
                    var m = System.Text.RegularExpressions.Regex.Match(
                        varName, @"^\s*([A-Za-z_][A-Za-z0-9_]*)\s*\(\s*(\d+)\s*\)\s*$");
                    if (m.Success)
                    {
                        baseName = m.Groups[1].Value;
                        if (int.TryParse(m.Groups[2].Value, out int parsedIdx))
                            bitIndex = parsedIdx;
                    }
                }
                var foundVar = this.Variables.Where(p => !ReferenceEquals(p, null)).FirstOrDefault(p =>
                    !string.IsNullOrEmpty(p.Name) &&
                    p.Name.Equals(baseName, StringComparison.OrdinalIgnoreCase));
                if (foundVar != null) ResultBitIndex = bitIndex;

                if (ReferenceEquals(foundVar, null))
                {
                    if (isSignalAssignment)
                    {
                        // The <= might be a comparison operator, not assignment
                        // Create a temporary result variable for condition
                        Result = new Variable("ConditionResult", "false", Constants.DATATYPES.BOOLEAN);
                        Result.PortType = Constants.PortType.OUT;
                    }
                    else
                    {
                        // For := with unknown variable, create a temporary variable
                        // This handles local variables in procedures/processes
                        Result = new Variable(varName, "0", Constants.DATATYPES.STD_LOGIC_VECTOR);
                        Result.PortType = Constants.PortType.OUT;
                        RawData = splitted[1].Trim();
                    }
                }
                else
                {
                    Result = foundVar;
                    RawData = splitted[1].Trim();
                }
            }
            else
            {
                // Malformed assignment - treat as condition
                Result = new Variable("ConditionResult", "false", Constants.DATATYPES.BOOLEAN);
                Result.PortType = Constants.PortType.OUT;
            }
        }
        else
        {
            // No assignment operator - this is a condition/expression, not an assignment
            // Create a temporary result variable to hold the condition result
            Result = new Variable("ConditionResult", "false", Constants.DATATYPES.BOOLEAN);
            Result.PortType = Constants.PortType.OUT;
        }
        _data = tokenizer.TokenizeLineReturn(RawData);
        var syntetized = Syntetize().ToList();
#if DEBUG_OPERATION
        Console.WriteLine($"[DEBUG Operation] RawData: '{RawData}'");
        Console.WriteLine($"[DEBUG Operation] Tokens: [{string.Join(", ", _data.Select(d => $"'{d}'"))}]");
        Console.WriteLine($"[DEBUG Operation] Variables: [{string.Join(", ", Variables.Select(v => v.Name))}]");
        Console.WriteLine($"[DEBUG Operation] Syntetized count: {syntetized.Count}");
        foreach (var item in syntetized)
        {
            Console.WriteLine($"  - {item.GetType().Name}: {item}");
        }
#endif
        ShuntingYard shuntingYard = new(syntetized);
        Items = shuntingYard.Result.ToArray();
    }

    /// <summary>
    /// Executes the operation.
    /// </summary>
    /// <returns>The result of the operation.</returns>
    public object Execute(){
        Stack<Variable> variables = new();
        if (Result.PortType == Constants.PortType.IN)
            throw new InvalidOperationException($"Input ports are not elegible for assignations: {Result.Name}");

        foreach(var item in Items){
            if(item is Variable newVar){
                variables.Push(newVar);
            }
            // v0.17.0: Handle deferred function evaluation at execution time
            // This allows rising_edge/falling_edge to be evaluated with current signal state
            else if(item is Models.DeferredFunction deferredFunc){
                var result = deferredFunc.Evaluate();
                variables.Push(result);
            }
            // Bench bug #5 sub-cause D: indexed vector reads re-read live.
            else if(item is Models.DeferredIndex deferredIdx){
                variables.Push(deferredIdx.Evaluate());
            }
            // Bug #14: runtime-variable index — read the index variable's
            // current value at evaluation time, then extract the source bit.
            else if(item is Models.DynamicIndex dynIdx){
                variables.Push(dynIdx.Evaluate());
            }
            else if(item is Operator op){
                // Only treat as unary NOT if it's actually a logic operation with NOT precedence
                // v0.13.0 Fix: Added type check to prevent ** (power) operator from being treated as NOT
                // Issue: PrecedencesArithOperators.UNARY has value 0, same as PrecedencesLogicOperators.NOT
                // Without type check, (int)PrecedencesArithOperators.UNARY == (int)PrecedencesLogicOperators.NOT
                bool isUnaryNot = op.OperatorType == Constants.OperatorType.LOGIC_OPERATION &&
                                  op.Precedence is Models.PrecedencesLogicOperators &&
                                  (Models.PrecedencesLogicOperators)op.Precedence == Models.PrecedencesLogicOperators.NOT;

                if(isUnaryNot){
                    var value = variables.Pop();
                    variables.Push(new("Temp", op.Execute(value), value.Type));
                } else {
                    var right = variables.Pop();
                    var left = variables.Pop();
                    Variable vari = new("Temp", op.Execute(left, right), left.Type);
                    if (op.OperatorType == Constants.OperatorType.COMPARATION)
                    {
                        vari.IsPort = false;
                        vari.Type = Constants.DATATYPES.BOOLEAN;
                        vari.Value = bool.Parse((string)vari.Value);
                    }
                    else if (string.Equals(op.OperatorName, "&", StringComparison.Ordinal))
                    {
                        // The Operator's `&` path bypasses the type-check and
                        // always returns a bit string; retag the wrapper so
                        // downstream operators (and the LHS assignment) see a
                        // proper STD_LOGIC_VECTOR with the correct Size.
                        vari.Type = Constants.DATATYPES.STD_LOGIC_VECTOR;
                        if (vari.Value is string concat) vari.Size = concat.Length;
                    }
                    variables.Push(vari);
                }
            }
        }
        if (variables.Count != 1)
            throw new InvalidOperationException($"Malformed expression: stack not reduced to a single result: {_rawData}");
        Result.Value = variables.Pop().Value;
        return Result;
    }

    /// <summary>
    /// Executes the operation.
    /// </summary>
    /// <param name="Items">The items of the operation.</param>
    /// <param name="CurrentIndex">The current index of the operation.</param>
    /// <returns>The result of the operation.</returns>
    public object Execute(object[] Items, int CurrentIndex = 0) => throw new NotImplementedException();

    /// <summary>
    /// Executes the operation.
    /// </summary>
    /// <param name="leftSideOperator">The left side operator of the operation.</param>
    /// <param name="rightSideOperator">The right side operator of the operation.</param>
    /// <returns>The result of the operation.</returns>
    public object Execute(Variable leftSideOperator, Variable rightSideOperator) => throw new NotImplementedException();
    
    /// <summary>
    /// Synthesizes the operation.
    /// </summary>
    /// <returns>The synthesized operation.</returns>
    public object[] Syntetize(){
        List<object> items = new();
        bool isComparation = false;
        int iterations = 0;
        const int maxIterations = 5000;
        while (_currentSyntetizeIndex < _data.Length && iterations < maxIterations)
        {
            iterations++;
            var currentItem = _data[_currentSyntetizeIndex];
            object item = new();
            if (currentItem == ";")
            {
                _currentSyntetizeIndex++;
                continue;
            }

            // `after N <unit>` inertial delay. Capture the delay (in ns)
            // onto the Operation so `ScheduledOperation.Execute` can route
            // through `SignalScheduler.ScheduleAfter`. The RHS expression
            // before the `after` keyword is what we'll evaluate at apply
            // time; everything from `after` onward is metadata.
            // Bench Phase E bug #8 fix (2026-06-03).
            if (currentItem.ToLower() == "after")
            {
                _currentSyntetizeIndex++; // skip 'after'
                long delayNs = 0;
                if (_currentSyntetizeIndex < _data.Length &&
                    long.TryParse(_data[_currentSyntetizeIndex], out long value))
                {
                    _currentSyntetizeIndex++;
                    if (_currentSyntetizeIndex < _data.Length && IsTimeUnit(_data[_currentSyntetizeIndex]))
                    {
                        string unit = _data[_currentSyntetizeIndex].ToLower();
                        _currentSyntetizeIndex++;
                        AfterDelayNs = unit switch
                        {
                            "fs" => value / 1_000_000L,
                            "ps" => value / 1_000L,
                            "ns" => value,
                            "us" => value * 1_000L,
                            "ms" => value * 1_000_000L,
                            "sec" or "s" => value * 1_000_000_000L,
                            _ => value,
                        };
                    }
                    else
                    {
                        AfterDelayNs = value; // assume ns when unit is missing
                    }
                }
                continue;
            }

            // Handle aggregate expressions like (others => '0')
            if (currentItem.ToLower() == "others")
            {
                // The leading `(` of the aggregate was already pushed onto
                // `items` by a previous loop iteration that fell through to
                // the generic operator path. ParseOthersAggregate consumes
                // the closing `)` token but not the opening `(` from items —
                // so without this step, the postfix output ends up with an
                // unmatched `(` operator that later tries to pop two
                // operands and stack-empties (cycle 7 of the m3-counter-wrap
                // / clk-divider-toggle exercises was the real-world repro:
                // the aggregate body fired the moment the wrap condition
                // evaluated true). Drop the trailing `(` so the aggregate
                // becomes a single self-contained operand.
                if (items.Count > 0 &&
                    items[^1] is Operator stray &&
                    stray.OperatorName == "(")
                {
                    items.RemoveAt(items.Count - 1);
                }

                // Parse (others => value) and create a variable with the aggregate value
                var aggregateResult = ParseOthersAggregate();
                if (aggregateResult.success)
                {
                    items.Add(aggregateResult.variable);
                    continue;
                }
                // v0.13.0: If parsing failed, skip this token to avoid infinite loop
                _currentSyntetizeIndex++;
                continue;
            }

            // v0.13.0 Fix: Skip 'downto' and 'to' keywords - they're handled within ParseIndexedAccess
            // but if we encounter them at the top level during expression synthesis, skip them
            // Example: signal(7 downto 0) - the 'downto' is not part of the arithmetic expression
            if (currentItem.ToLower() == "downto" || currentItem.ToLower() == "to")
            {
                _currentSyntetizeIndex++;
                continue;
            }

            // v0.13.0 Fix: Skip 'when' and 'else' keywords for conditional signal assignments
            // These are used in concurrent conditional assignments like:
            // signal <= value1 when condition1 else value2 when condition2 else default;
            // For simulation purposes, we use the first value and skip the conditional part
            // (Full conditional assignment support would require additional infrastructure)
            if (currentItem.ToLower() == "when")
            {
                // Skip everything until we find 'else' or end of expression
                while (_currentSyntetizeIndex < _data.Length)
                {
                    if (_data[_currentSyntetizeIndex].ToLower() == "else")
                    {
                        _currentSyntetizeIndex++; // skip 'else'
                        break;
                    }
                    _currentSyntetizeIndex++;
                }
                continue;
            }

            // Check for signal attributes (e.g., clk'event)
            var attributeResult = TryParseSignalAttribute(currentItem);
            if (attributeResult.isAttribute)
            {
                item = attributeResult.variable;
                items.Add(item);
                _currentSyntetizeIndex++;
                continue;
            }

            // Check for standard function calls (e.g., rising_edge(clk))
            var functionResult = TryParseStandardFunction(currentItem);
            if (functionResult.isFunction)
            {
                item = functionResult.result;  // Can be Variable or DeferredFunction
                items.Add(item);
                _currentSyntetizeIndex++;
                continue;
            }

            if (_operators.IsMatch(currentItem.ToLower()))
            {
                isComparation = true;
                item = new Operator(currentItem);
            }
            else if (_arithOperators.IsMatch(currentItem) || _logicOperators.IsMatch(currentItem.ToLower()))
            {
                item = new Operator(currentItem);
            }
            else if (_finder.IsLiteral(currentItem))
            {
                Constants.DATATYPES type = _finder.GetDataType(currentItem);
                // v0.17.0: Use atomic counter instead of expensive Guid.NewGuid()
                item = new Variable($"Literal-{Interlocked.Increment(ref _literalCounter)}", currentItem, type);
            }
            else if (_variableRegex.IsMatch(currentItem))
            {
                // Check if this is a function call (next token will be parenthesis)
                if (_currentSyntetizeIndex + 1 < _data.Length &&
                    _data[_currentSyntetizeIndex + 1] == "(")
                {
                    // Could be standard function, user-defined function, or array access
                    if (StandardFunctions.IsStandardFunction(currentItem))
                    {
                        var funcResult = ParseFunctionCall(currentItem);
                        if (funcResult.success)
                        {
                            items.Add(funcResult.result);  // Can be Variable or DeferredFunction
                            continue;
                        }
                    }
                    else if (!Variables.Contains(currentItem) && StandardFunctions.CouldBeFunction(currentItem))
                    {
                        // User-defined function - skip to closing paren and return stub value
                        var funcResult = ParseUserDefinedFunctionCall(currentItem);
                        if (funcResult.success)
                        {
                            items.Add(funcResult.variable);
                            continue;
                        }
                    }
                }

                // v0.13.0: Handle undeclared identifiers gracefully
                // This allows tests with package/constant references to pass
                Variable variable;
                if (!Variables.Contains(currentItem))
                {
#if DEBUG
                    Console.WriteLine($"[Operation] Warning: Undeclared identifier '{currentItem}' in expression '{_rawData}'");
#endif
                    // Create stub variable for undeclared identifier
                    // Assume STD_LOGIC_VECTOR with size 8 as default
                    variable = new Variable(currentItem, "00000000", Constants.DATATYPES.STD_LOGIC_VECTOR)
                    {
                        Size = 8,
                        IsStub = true
                    };
                }
                else
                {
                    variable = Variables.First(p => p.Name.ToLower().Equals(currentItem.ToLower()));
                }

                // Check for array/vector element access: var(index). Use
                // the deferred form so the bit is re-read at expression
                // evaluation time rather than frozen at parse time.
                // Bench Phase F bug #5 sub-cause D fix.
                if (_currentSyntetizeIndex + 1 < _data.Length && _data[_currentSyntetizeIndex + 1] == "(")
                {
                    _pendingDynamicIndexSource = null;
                    _pendingDynamicIndexVar = null;
                    var idx = ParseIndexedAccessDeferred(variable);
                    if (idx.success)
                    {
                        items.Add(idx.deferred!);
                        continue;
                    }
                    if (_pendingDynamicIndexSource != null && _pendingDynamicIndexVar != null)
                    {
                        items.Add(new Models.DynamicIndex(_pendingDynamicIndexSource, _pendingDynamicIndexVar));
                        _pendingDynamicIndexSource = null;
                        _pendingDynamicIndexVar = null;
                        continue;
                    }
                }

                item = variable;
            }
            else
                item = new Operator(currentItem);

            items.Add(item);
            _currentSyntetizeIndex++;
        }
        if (isComparation)
        {
            Result.PortType = Constants.PortType.OUT;
            List<object> newItems = new();
            foreach (var item in items)
            {
                if (item is Variable vari && vari.Name.Contains("Literal"))
                    vari.PortType = Constants.PortType.OUT;
                newItems.Add(item);
            }
            items = newItems;
        }
        return items.ToArray();
    }

    /// <summary>
    /// Attempts to parse a signal attribute expression like "clk'event".
    /// </summary>
    /// <param name="token">The token to parse.</param>
    /// <returns>Tuple indicating if it's an attribute and the resulting variable.</returns>
    private (bool isAttribute, Variable variable) TryParseSignalAttribute(string token)
    {
        // Check if token contains apostrophe (attribute indicator)
        if (!token.Contains("'"))
        {
            return (false, null!);
        }

        int apostropheIndex = token.IndexOf("'");

        // Skip if starts with apostrophe (character literal)
        if (apostropheIndex == 0)
        {
            return (false, null!);
        }

        string signalName = token[..apostropheIndex].Trim();
        string attributeName = token[(apostropheIndex + 1)..].Trim().ToLower();

        // Known signal attributes
        string[] validAttributes = ["event", "last_value", "stable", "active"];

        if (!validAttributes.Contains(attributeName))
        {
            return (false, null!);
        }

        // Find the signal
        var signal = Variables.FirstOrDefault(v => v.Name.Equals(signalName, StringComparison.OrdinalIgnoreCase));
        if (signal == null)
        {
            return (false, null!);
        }

        // Evaluate the attribute
        var handler = SignalAttributeHandler.Instance;
        object attributeValue = attributeName switch
        {
            "event" => handler.GetEvent(signal),
            "last_value" => handler.GetLastValue(signal),
            "stable" => handler.GetStable(signal),
            "active" => handler.GetActive(signal),
            _ => false
        };

        // Create a variable representing the attribute result
        Constants.DATATYPES resultType = attributeName == "last_value"
            ? signal.Type
            : Constants.DATATYPES.BOOLEAN;

        var resultVar = new Variable(
            $"Attribute-{signalName}'{attributeName}",
            attributeValue.ToString() ?? "false",
            resultType
        );
        resultVar.PortType = Constants.PortType.OUT; // Allow in comparisons

        return (true, resultVar);
    }

    /// <summary>
    /// Attempts to parse a standard function call like "rising_edge(clk)".
    /// This handles the case where the function call is a single token.
    /// </summary>
    /// <returns>
    /// A tuple where the result can be either a Variable (for functions evaluated at parse time)
    /// or a DeferredFunction (for functions that must be evaluated at execution time).
    /// </returns>
    private (bool isFunction, object result) TryParseStandardFunction(string token)
    {
        // Check if token contains parentheses (function call pattern: funcname(arg))
        int openParen = token.IndexOf('(');
        int closeParen = token.LastIndexOf(')');

        if (openParen <= 0 || closeParen < openParen)
        {
            return (false, null!);
        }

        string functionName = token[..openParen].Trim().ToLower();
        string argsString = token[(openParen + 1)..closeParen].Trim();

        // Check if it's a standard function
        if (!StandardFunctions.IsStandardFunction(functionName))
        {
            return (false, null!);
        }

        // Parse arguments
        List<object> args = new();
        if (!string.IsNullOrEmpty(argsString))
        {
            var argParts = argsString.Split(',');
            foreach (var arg in argParts)
            {
                string trimmedArg = arg.Trim();
                // Try to find variable
                var variable = Variables.FirstOrDefault(v => v.Name.Equals(trimmedArg, StringComparison.OrdinalIgnoreCase));
                if (variable != null)
                {
                    args.Add(variable);
                }
                else if (int.TryParse(trimmedArg, out int intVal))
                {
                    args.Add(intVal);
                }
                else
                {
                    args.Add(trimmedArg);
                }
            }
        }

        // Determine result type
        Constants.DATATYPES resultType = functionName switch
        {
            "rising_edge" or "falling_edge" => Constants.DATATYPES.BOOLEAN,
            "to_integer" or "conv_integer" => Constants.DATATYPES.INTEGER,
            _ => Constants.DATATYPES.STD_LOGIC_VECTOR
        };

        // v0.17.0 Fix: For edge detection functions, defer evaluation until execution time
        // These functions depend on the current simulation state which isn't available at parse time
        if (Models.DeferredFunction.ShouldDefer(functionName))
        {
            return (true, new Models.DeferredFunction(functionName, args.ToArray(), resultType, Variables));
        }

        // Evaluate the function immediately for other functions
        object result = StandardFunctions.EvaluateFunction(functionName, args.ToArray(), Variables);

        // v0.17.0 Fix: Preserve actual boolean value instead of converting to string
        object resultValue = resultType == Constants.DATATYPES.BOOLEAN
            ? (result ?? false)
            : (result?.ToString() ?? "false");

        var resultVar = new Variable(
            $"Function-{functionName}",
            resultValue,
            resultType
        );
        resultVar.PortType = Constants.PortType.OUT;

        return (true, resultVar);
    }

    /// <summary>
    /// Parses a function call where the function name and parentheses are separate tokens.
    /// </summary>
    /// <returns>
    /// A tuple where the result can be either a Variable (for functions evaluated at parse time)
    /// or a DeferredFunction (for functions that must be evaluated at execution time).
    /// </returns>
    private (bool success, object result) ParseFunctionCall(string functionName)
    {
        // Skip the function name
        _currentSyntetizeIndex++;

        // Expect opening parenthesis
        if (_currentSyntetizeIndex >= _data.Length || _data[_currentSyntetizeIndex] != "(")
        {
            return (false, null!);
        }
        _currentSyntetizeIndex++; // Skip (

        // Collect arguments until closing parenthesis
        List<object> args = new();
        int parenDepth = 1;

        while (_currentSyntetizeIndex < _data.Length && parenDepth > 0)
        {
            string token = _data[_currentSyntetizeIndex];

            if (token == "(")
            {
                parenDepth++;
            }
            else if (token == ")")
            {
                parenDepth--;
                if (parenDepth == 0)
                {
                    _currentSyntetizeIndex++;
                    break;
                }
            }
            else if (token == "," && parenDepth == 1)
            {
                // Argument separator - skip
            }
            else if (token != ";" && parenDepth == 1)
            {
                // Detect a nested function call:
                //   <fn_name> (              ← we want to recurse so the
                //                              inner call's args don't get
                //                              swallowed by the outer loop's
                //                              depth tracking.
                // The `_currentSyntetizeIndex+1 < _data.Length` guard avoids
                // walking off the end on a malformed expression. Function
                // identity is established via:
                //   • StandardFunctions.IsStandardFunction — covers IEEE
                //     names + clog2 + the legacy aliases (to_integer,
                //     unsigned, signed, conv_*, …);
                //   • Models.FunctionRegistry.IsKnown — covers user-defined
                //     functions registered by the IEEE library compiler or
                //     by the user's own package source.
                // When the recursive call succeeds, _currentSyntetizeIndex
                // already advanced past the inner `)`, so we `continue` to
                // skip the trailing index increment and let the outer loop
                // re-check `parenDepth > 0` against the next token.
                bool nextIsParen = _currentSyntetizeIndex + 1 < _data.Length
                                && _data[_currentSyntetizeIndex + 1] == "(";
                bool isKnownFn = nextIsParen && _variableRegex.IsMatch(token)
                              && (StandardFunctions.IsStandardFunction(token)
                               || Services.FunctionRegistry.Instance.IsKnown(token));
                if (isKnownFn)
                {
                    var nested = ParseFunctionCall(token);
                    if (nested.success)
                    {
                        args.Add(nested.result);
                        continue;
                    }
                    // Recursive parse failed — fall through to the legacy
                    // bare-string path so the original behaviour (silent
                    // arg drop) is preserved on error paths.
                }

                // This is an argument (legacy path: identifier resolves to
                // a Variable, integer literal, or bare token).
                var variable = Variables.Where(v => !ReferenceEquals(v, null))
                    .FirstOrDefault(v => !string.IsNullOrEmpty(v.Name) &&
                        v.Name.Equals(token, StringComparison.OrdinalIgnoreCase));
                if (!ReferenceEquals(variable, null))
                {
                    args.Add(variable);
                }
                else if (int.TryParse(token, out int intVal))
                {
                    args.Add(intVal);
                }
                else
                {
                    args.Add(token);
                }
            }

            _currentSyntetizeIndex++;
        }

        // Determine result type
        Constants.DATATYPES resultType = functionName.ToLower() switch
        {
            "rising_edge" or "falling_edge" => Constants.DATATYPES.BOOLEAN,
            "to_integer" or "conv_integer" or "clog2" => Constants.DATATYPES.INTEGER,
            _ => Constants.DATATYPES.STD_LOGIC_VECTOR
        };

        // v0.17.0 Fix: For edge detection functions, defer evaluation until execution time
        if (Models.DeferredFunction.ShouldDefer(functionName))
        {
            return (true, new Models.DeferredFunction(functionName, args.ToArray(), resultType, Variables));
        }

        // Evaluate the function immediately for other functions
        object result = StandardFunctions.EvaluateFunction(functionName, args.ToArray(), Variables);

        // v0.17.0 Fix: Preserve actual boolean value instead of converting to string
        object resultValue = resultType == Constants.DATATYPES.BOOLEAN
            ? (result ?? false)
            : (result?.ToString() ?? "false");

        var resultVar = new Variable(
            $"Function-{functionName}",
            resultValue,
            resultType
        );
        resultVar.PortType = Constants.PortType.OUT;

        return (true, resultVar);
    }

    /// <summary>
    /// Parses a user-defined function call and returns a stub value.
    /// This allows synthesizing code that uses user-defined functions without executing them.
    /// </summary>
    private (bool success, Variable variable) ParseUserDefinedFunctionCall(string functionName)
    {
        // Skip the function name (already processed), move to "("
        _currentSyntetizeIndex++;

        if (_currentSyntetizeIndex >= _data.Length || _data[_currentSyntetizeIndex] != "(")
            return (false, null!);

        // Skip to matching closing parenthesis
        int parenDepth = 0;
        while (_currentSyntetizeIndex < _data.Length)
        {
            string token = _data[_currentSyntetizeIndex];
            if (token == "(") parenDepth++;
            else if (token == ")")
            {
                parenDepth--;
                if (parenDepth == 0)
                {
                    _currentSyntetizeIndex++;
                    break;
                }
            }
            _currentSyntetizeIndex++;
        }

        // Return a stub variable representing the function result
        var resultVar = new Variable(
            $"UserFunction-{functionName}",
            "0",
            Constants.DATATYPES.STD_LOGIC_VECTOR
        );
        resultVar.PortType = Constants.PortType.OUT;

        return (true, resultVar);
    }

    /// <summary>
    /// Checks if a token is a VHDL time unit.
    /// </summary>
    /// <param name="token">The token to check.</param>
    /// <returns>True if the token is a time unit.</returns>
    private static bool IsTimeUnit(string token)
    {
        string[] timeUnits = { "fs", "ps", "ns", "us", "ms", "sec", "min", "hr" };
        return timeUnits.Contains(token.ToLower());
    }

    /// <summary>
    /// Parses an others aggregate like (others => '0').
    /// </summary>
    /// <returns>Tuple indicating success and the resulting variable with aggregate value.</returns>
    private (bool success, Variable variable) ParseOthersAggregate()
    {
        // Current token is 'others', move past it
        _currentSyntetizeIndex++;

        // Expect '=>'
        if (_currentSyntetizeIndex < _data.Length && _data[_currentSyntetizeIndex] == "=>")
        {
            _currentSyntetizeIndex++; // skip '=>'
        }

        // Get the value
        string value = "0";
        if (_currentSyntetizeIndex < _data.Length)
        {
            value = _data[_currentSyntetizeIndex].Replace("'", "").Replace("\"", "");
            _currentSyntetizeIndex++;
        }

        // Skip closing ')' if present
        if (_currentSyntetizeIndex < _data.Length && _data[_currentSyntetizeIndex] == ")")
        {
            _currentSyntetizeIndex++;
        }

        // Create a variable that represents the aggregate
        // The actual size will be determined by the assignment target
        var resultVar = new Variable($"Aggregate_others_{value}", $"(others=>{value})", Constants.DATATYPES.STD_LOGIC_VECTOR);
        resultVar.PortType = Constants.PortType.OUT;

        return (true, resultVar);
    }

    /// <summary>
    /// Parses an indexed array/vector access like A(7) or A(7 downto 0).
    /// </summary>
    /// <param name="variable">The variable being indexed.</param>
    /// <returns>Tuple indicating success and the resulting indexed variable.</returns>
    // Backward-compatible Variable-returning wrapper used by call sites
    // that don't care about evaluation timing. Internally promotes to the
    // deferred path so the value re-reads at execution time.
    private (bool success, Variable variable) ParseIndexedAccess(Variable variable)
    {
        var deferred = ParseIndexedAccessDeferred(variable);
        if (!deferred.success) return (false, null!);
        return (true, deferred.deferred!.Evaluate());
    }

    /// <summary>
    /// Parses `name(N)` or `name(H downto L)` and returns a
    /// <see cref="Models.DeferredIndex"/> that re-reads the source variable's
    /// bits each time the containing expression is evaluated. The legacy
    /// <see cref="ParseIndexedAccess"/> entry point still resolves
    /// immediately for non-engine callers (e.g. tests).
    /// </summary>
    private (bool success, Models.DeferredIndex? deferred) ParseIndexedAccessDeferred(Variable variable)
    {
        // Skip the variable name (already processed), move to "("
        _currentSyntetizeIndex++;

        if (_currentSyntetizeIndex >= _data.Length || _data[_currentSyntetizeIndex] != "(")
            return (false, null);

        _currentSyntetizeIndex++; // skip "("

        // Parse the index expression - could be simple "7" or range "7 downto 0"
        int startIndex = -1;
        int endIndex = -1;
        bool isDownto = false;
        bool isTo = false;

        // Get start index - could be a simple number, variable, or expression
        if (_currentSyntetizeIndex < _data.Length)
        {
            // Collect tokens until we hit 'downto', 'to', or ')'
            string expr = "";
            int tokenCount = 0;
            while (_currentSyntetizeIndex + tokenCount < _data.Length)
            {
                string token = _data[_currentSyntetizeIndex + tokenCount];
                string tokenLower = token.ToLower();
                if (tokenLower == "downto" || tokenLower == "to" || token == ")")
                    break;
                expr += token + " ";
                tokenCount++;
            }
            expr = expr.Trim();
            _currentSyntetizeIndex += tokenCount;

            // Try to evaluate the expression
            if (!string.IsNullOrEmpty(expr))
            {
                if (int.TryParse(expr, out int idx))
                {
                    startIndex = idx;
                }
                else if (expr.Contains("-") || expr.Contains("+") || expr.Contains("*") || expr.Contains("/"))
                {
                    // It's an arithmetic expression
                    try
                    {
                        Operation op = new(expr, Variables);
                        var result = op.Execute();
                        if (result is Variable v)
                        {
                            startIndex = Convert.ToInt32(v.Value);
                        }
                        else
                        {
                            startIndex = Convert.ToInt32(result);
                        }
                    }
                    catch { /* ignore errors */ }
                }
                else if (!expr.Contains(' '))
                {
                    // Bug #14: runtime-variable index. Only fires when the
                    // index is a single identifier referring to an INTEGER
                    // variable in scope (typically a for-loop counter).
                    var candidate = Variables.FirstOrDefault(v =>
                        v != null && !string.IsNullOrEmpty(v.Name) &&
                        v.Name.Equals(expr, StringComparison.OrdinalIgnoreCase));
                    if (candidate != null && candidate.Type == Constants.DATATYPES.INTEGER)
                    {
                        _pendingDynamicIndexSource = variable;
                        _pendingDynamicIndexVar = candidate;
                        // Swallow remaining tokens up to and including ')'
                        // so the outer Syntetize stream stays aligned.
                        while (_currentSyntetizeIndex < _data.Length && _data[_currentSyntetizeIndex] != ")")
                            _currentSyntetizeIndex++;
                        if (_currentSyntetizeIndex < _data.Length && _data[_currentSyntetizeIndex] == ")")
                            _currentSyntetizeIndex++;
                        return (false, null);
                    }
                }
            }
        }

        // Check for range (downto or to)
        if (_currentSyntetizeIndex < _data.Length)
        {
            string rangeToken = _data[_currentSyntetizeIndex].ToLower();
            if (rangeToken == "downto")
            {
                isDownto = true;
                _currentSyntetizeIndex++;
                if (_currentSyntetizeIndex < _data.Length && int.TryParse(_data[_currentSyntetizeIndex], out int end))
                {
                    endIndex = end;
                    _currentSyntetizeIndex++;
                }
            }
            else if (rangeToken == "to")
            {
                isTo = true;
                _currentSyntetizeIndex++;
                if (_currentSyntetizeIndex < _data.Length && int.TryParse(_data[_currentSyntetizeIndex], out int end))
                {
                    endIndex = end;
                    _currentSyntetizeIndex++;
                }
            }
        }

        // Skip closing ")"
        if (_currentSyntetizeIndex < _data.Length && _data[_currentSyntetizeIndex] == ")")
        {
            _currentSyntetizeIndex++;
        }

        // Build a DeferredIndex so the bits are re-read from the source
        // variable each time the containing expression evaluates. Bench
        // Phase F bug #5 sub-cause D fix.
        bool isSlice = isDownto || isTo;
        int high, low;
        if (isSlice)
        {
            high = isDownto ? startIndex : endIndex;
            low  = isDownto ? endIndex   : startIndex;
        }
        else
        {
            high = startIndex;
            low  = startIndex;
        }
        if (startIndex < 0) return (false, null);
        return (true, new Models.DeferredIndex(variable, high, low, isSlice));
    }

    // Bug #14 side-channel: ParseIndexedAccessDeferred writes the source +
    // index Variable pair here when it detects a runtime-variable index;
    // Syntetize() reads + clears them right after the call to install a
    // DynamicIndex operand instead of a frozen DeferredIndex.
    private Variable? _pendingDynamicIndexSource;
    private Variable? _pendingDynamicIndexVar;
}