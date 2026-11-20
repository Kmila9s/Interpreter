using System.Text.RegularExpressions;
using Interpreter.Interfaces;
using Interpreter.Models;
using Interpreter.Repositories;
using Parser.Models;
using Parser.Repositories;
using Parser.Services;

namespace Interpreter.Services;

/// <summary>
/// Signal attribute types supported by the interpreter.
/// </summary>
public enum SignalAttributeType
{
    /// <summary>No signal attribute.</summary>
    None,
    /// <summary>'event attribute: true when the signal changed in the current delta cycle.</summary>
    Event,
    /// <summary>'last_value attribute: the signal's value before its most recent transition.</summary>
    LastValue,
    /// <summary>'stable attribute: true when the signal has not changed.</summary>
    Stable,
    /// <summary>'active attribute: true when a transaction occurred on the signal.</summary>
    Active
}

/// <summary>
/// Represents an operation — the runtime evaluator for a single VHDL expression
/// or assignment. Tokenizes → synthesises via <see cref="Syntetize"/> into an
/// <see cref="Items"/> array of operands + operators, then <see cref="Execute()"/>
/// walks Items with a Shunting-Yard-style stack.
/// </summary>
/// <remarks>
/// Items dispatch (post 2026-07-20):
/// <list type="bullet">
/// <item><see cref="Variable"/> — plain signal / port / constant / literal.</item>
/// <item><see cref="Models.DeferredFunction"/> — IEEE stdlib primitive
/// (rising_edge, to_unsigned, resize, …) evaluated at execute time.</item>
/// <item><see cref="Models.DeferredUserFunction"/> — user-declared VHDL
/// function whose body is registered. Args re-parse against the live scope
/// on every <see cref="Execute()"/>. Introduced 2026-07-20 to close the
/// user-fn reactivity gap (rgb_rainbow-style LFSR-through-function froze
/// at first-eval pre-fix).</item>
/// <item><see cref="Models.DeferredIndex"/> — indexed signal read
/// (bench Phase F #5).</item>
/// <item><see cref="Models.DynamicIndex"/> — runtime-variable index.</item>
/// <item><see cref="Operator"/> — arith / logic / relational operator.</item>
/// </list>
/// <see cref="ScheduledOperation.Execute"/> has a twin dispatch loop that
/// must stay in sync when new item kinds land.
/// </remarks>
public class Operation : IOperation, IConcurrentStatement {
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

    /// <summary>A validation error about this operation's target, located at its line and name.</summary>
    private Parser.Models.ValidationException ResultError(string message) =>
        new(message, SourceLine, 0, Result?.Name);

    /// <summary>
    /// The assignment's target (<see cref="Result"/> plus the index or range
    /// written, if any); null when this operation is a condition, not an
    /// assignment to a declared object.
    /// </summary>
    public AssignmentTarget? Target { get; private set; }

    /// <summary>
    /// The delay mechanism and waveform of a signal assignment
    /// (<c>y &lt;= transport a after 2 ns, b after 5 ns</c>); null for variable
    /// assignments and conditions. This operation evaluates the first element's
    /// value; <see cref="WaveformValues"/> evaluates the others and the delays.
    /// </summary>
    public Waveform? Waveform { get; private set; }

    /// <summary>The value expressions of the waveform elements after the first.</summary>
    private List<Operation>? _laterElements;

    /// <summary>
    /// The waveform to schedule, given the first element's value
    /// <paramref name="first"/>: every element with its delay evaluated now.
    /// </summary>
    public IReadOnlyList<WaveformElement> WaveformValues(object first)
    {
        if (Waveform is not { } w) return [new WaveformElement(first, 0)];
        var result = new List<WaveformElement>(w.Elements.Count)
        {
            new(first, Models.Waveform.DelayNs(w.Elements[0].After, Variables, SourceLine)),
        };
        for (int i = 1; i < w.Elements.Count; i++)
            result.Add(new(_laterElements![i - 1].Evaluate(), Models.Waveform.DelayNs(w.Elements[i].After, Variables, SourceLine)));
        return result;
    }

    /// <summary>The pulse-rejection limit in ns (negative = the first delay).</summary>
    public long RejectNs => Waveform?.Reject is { } r ? Models.Waveform.DelayNs(r, Variables, SourceLine) : -1;

    /// <summary>
    /// True for variable assignments (<c>:=</c>) inside a process body.
    /// VHDL variable semantics require immediate update visible to the
    /// next statement (within the same delta cycle), unlike signal
    /// assignments (<c>&lt;=</c>) which are scheduled to a later delta.
    /// `ScheduledOperation.Execute` writes the result directly to
    /// <c>Result.Value</c> when this is true rather than queuing
    /// through `SignalScheduler.Schedule`. Bug #14 follow-up.
    /// </summary>
    public bool IsVariableAssignment { get; private set; }

    /// <summary>The aggregate target of <c>(a, b) &lt;= value</c> (G55); null for any other assignment.</summary>
    public AggregateTarget? AggregateTarget { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="Operation"/> class.
    /// </summary>
    /// <param name="RawData">The raw data of the operation.</param>
    /// <param name="Variables">The variables of the operation.</param>
    /// <param name="sourceLine">Optional 1-based source line; 0 = unknown.</param>
    public Operation(string RawData, HashSet<Variable> Variables, int sourceLine = 0, bool isAssignmentStatement = false)
    {
        var _profileStopwatch = ParseProfile.Enabled ? System.Diagnostics.Stopwatch.StartNew() : null;
        SourceLine = sourceLine;
        this.Variables = Variables;
        _rawData = RawData;
        Tokenizer tokenizer = new();

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
                AggregateTarget = AggregateTarget.TryParse(varName, this.Variables, SourceLine);
                Target = AggregateTarget is null ? AssignmentTarget.Resolve(varName, this.Variables) : null;
                if (Target?.Path.FieldError is { } fieldError)
                    throw new Parser.Models.ParserException(fieldError, SourceLine, 0, varName);
                var foundVar = Target?.Base ?? AggregateTarget?.Elements[0].Base;

                if (foundVar is null)
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
                    if (isSignalAssignment)
                    {
                        Waveform = Models.Waveform.Parse(RawData);
                        RawData = Waveform.FirstValue;
                        if (Waveform.Elements.Count > 1)
                            _laterElements = [.. Waveform.Elements.Skip(1).Select(e => new Operation(e.Value, this.Variables, SourceLine))];
                    }
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
        // v1.18.0 (P4/C8): a signal assignment whose target is an input port is
        // illegal — GHDL: `port "X" can't be assigned`. Gated on
        // isAssignmentStatement (set only by the concurrent/process assignment
        // statement parsers) because the ctor also splits a comparison like
        // `if din <= x` on `<=`, and `din` may legitimately be an input port read.
        if (isAssignmentStatement)
        {
            foreach (var written in WrittenObjects)
                if (written is { IsPort: true, PortType: Constants.PortType.IN })
                    throw ResultError($"cannot assign to input port \"{written.Name}\"");
        }

        _data = DropQualifierMarks(tokenizer.TokenizeLineReturn(RawData));
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
        ShuntingYard shuntingYard = new(WithUnarySigns(syntetized));
        Items = shuntingYard.Result.ToArray();

        // v1.19.0 (P4/C4+C5): type/width validation for a single-atom assignment
        // (gated like C8 so comparisons are never mistaken for assignments).
        if (isAssignmentStatement)
        {
            if (AggregateTarget is null)
            {
                ValidatePartialTarget();
                ValidateAssignmentTypeWidth();
            }
            else ValidateAggregateTargetWidth();
        }

        if (_profileStopwatch != null)
        {
            _profileStopwatch.Stop();
            ParseProfile.RecordOperationCtor(_profileStopwatch.ElapsedTicks);
        }
    }

    /// <summary>
    /// VHDL sign operators: a <c>-</c>/<c>+</c> that starts an expression (first item, or right after
    /// <c>(</c>, <c>,</c> or another operator) applies to the whole following term, so it reads
    /// as <c>0 - term</c> / <c>0 + term</c> (<c>a &lt;= -7;</c>, <c>x := -n * 2;</c>). A logical
    /// operator in that position is a VHDL-2008 unary reduction (<c>and v</c>).
    /// </summary>
    private static List<object> WithUnarySigns(List<object> items)
    {
        var result = new List<object>(items.Count + 1);
        foreach (var item in items)
        {
            bool prefix = result.Count == 0 || result[^1] is Operator { OperatorName: not ")" };
            if (prefix && item is Operator { OperatorName: "-" or "+" })
                result.Add(new Variable($"Literal-{Interlocked.Increment(ref _literalCounter)}", "0", Constants.DATATYPES.INTEGER) { IsConstant = true });
            else if (prefix && item is Operator op && StdLogic.Binary(op.OperatorName) is not null && !op.OperatorName.StartsWith('?'))
            {
                result.Add(new Operator(op.OperatorName).AsReduction());
                continue;
            }
            result.Add(item);
        }
        return result;
    }

    /// <summary>
    /// Evaluates the expression (assignment right-hand side or condition)
    /// against its operands' current values and returns the result without
    /// writing anything. Shared by <see cref="Execute"/> and
    /// <see cref="ScheduledOperation.Run"/>.
    /// </summary>
    public object Evaluate()
    {
        try { return EvaluateItems(); }
        catch (SimulationRuntimeException e) { throw e.At(SourceLine); }
    }

    private object EvaluateItems()
    {
        Stack<Variable> variables = new();

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
            // Tier 4.1 (2026-07-28): DeferredAttribute — `sig'event`,
            // `'last_value`, `'stable[(T)]` re-evaluate against the live
            // SignalAttributeHandler state each execute, mirroring the
            // DeferredFunction path. Fixes the frozen-at-synth-time bug that
            // made raw `clk'event and clk='1'` inside a wait-until unusable.
            else if(item is Models.DeferredAttribute deferredAttr){
                variables.Push(deferredAttr.Evaluate());
            }
            // 2026-07-20: user-defined function whose body is registered.
            // Evaluate re-parses each arg expression against the live scope
            // and re-runs FunctionExecutor.ExecuteFunction. Without this
            // branch, `q <= step_r(state);` inside a clocked process would
            // freeze after the first fire (the pre-P22-fn eager-execute
            // path baked the result into a Variable that never refreshed).
            else if(item is Models.DeferredUserFunction deferredUser){
                var result = deferredUser.Evaluate();
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
            else if(item is Models.IDeferredOperand deferred){
                variables.Push(deferred.Evaluate());
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
                        // The comparator op already returns a primitive type
                        // here: sometimes a string ("True"/"False" — from the
                        // legacy ToString-based comparator) and sometimes a
                        // raw `bool` (newer dispatch paths). Coerce safely
                        // either way; the InvalidCast at this line was
                        // crashing P2A's `if reset = '1'` evaluation when
                        // the comparator returned a bool directly.
                        vari.Value = vari.Value switch
                        {
                            bool b   => b,
                            string s => bool.TryParse(s, out var pb) && pb,
                            _        => false
                        };
                    }
                    else if (string.Equals(op.OperatorName, "&", StringComparison.Ordinal))
                    {
                        // The Operator's `&` path bypasses the type-check and
                        // always returns a bit string; retag the wrapper so
                        // downstream operators (and the LHS assignment) see a
                        // proper STD_LOGIC_VECTOR with the correct Size.
                        bool text = left.Type is Constants.DATATYPES.STRING or Constants.DATATYPES.CHARACTER
                            || right.Type is Constants.DATATYPES.STRING or Constants.DATATYPES.CHARACTER;
                        vari.Type = text ? Constants.DATATYPES.STRING : Constants.DATATYPES.STD_LOGIC_VECTOR;
                        if (vari.Value is string concat) vari.Size = concat.Length;
                    }
                    variables.Push(vari);
                }
            }
        }
        if (variables.Count != 1)
            throw new InvalidOperationException(
                $"Malformed expression: stack not reduced to a single result ({variables.Count} items left). RHS=`{RawData}` Target=`{Result?.Name}`");
        return variables.Pop().Value;
    }

    /// <summary>The target must be writable: VHDL forbids assigning an input port.</summary>
    internal void EnsureWritable()
    {
        if (Result is { IsPort: true, PortType: Constants.PortType.IN })
            throw new InvalidOperationException($"Input ports are not eligible for assignments: {Result.Name}");
    }

    /// <summary>
    /// Evaluates and writes the result immediately (sequential executor and
    /// condition evaluation). A part target writes only its part.
    /// </summary>
    public object Execute()
    {
        EnsureWritable();
        var value = Evaluate();
        if (AggregateTarget is { } aggregate) aggregate.Assign(null, value, SourceLine, immediate: true);
        else if (Target is { IsPartial: true } part) part.Assign(null, value, SourceLine, immediate: true);
        else Result.Value = value;
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

            // Record / array aggregates: `(lo => a, hi => b)`, `(a, b)`,
            // `(0 => a, others => '0')`, at operand position.
            if (currentItem == "(" && (items.Count == 0 || items[^1] is Operator { OperatorName: not ")" })
                && TryParseAggregate(items) is { } aggregate)
            {
                items.Add(aggregate);
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

            // Check for signal attributes (e.g., clk'event). Tier 4.1: dynamic
            // attributes now come back as DeferredAttribute so Execute() re-
            // evaluates against live handler state; static ones stay Variable.
            var attributeResult = TryParseSignalAttribute(currentItem);
            if (attributeResult.isAttribute)
            {
                items.Add(attributeResult.result);
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
                item = new Variable($"Literal-{Interlocked.Increment(ref _literalCounter)}", currentItem, type) { IsConstant = true };
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
                        // User-defined function. 2026-07-20 — wrapper (DeferredUserFunction
                        // when body is registered, or legacy stub Variable otherwise) is
                        // returned as `object`; Execute() branches on the type at fire time.
                        var funcResult = ParseUserDefinedFunctionCall(currentItem);
                        if (funcResult.success)
                        {
                            items.Add(funcResult.item);
                            continue;
                        }
                    }
                }

                // v1.17.0 (P3): resolve an undeclared identifier against the
                // package-constant registry before giving up. A name that is a
                // real constant materialises as a typed Variable (this also fixes
                // the latent "package constant evaluates to 0" bug, where such a
                // name used to fall through to the 8-bit stub). A name that is
                // neither a signal/port/generic/alias nor a package constant is a
                // genuine undeclared-identifier error — GHDL: `no declaration for
                // "X"`. A token still followed by `(` is a function/array edge
                // that slipped past the branches above (e.g. a standard-function
                // parse that failed); keep the historical stub there rather than
                // mis-reporting it as undeclared.
                Variable variable;
                if (!Variables.Contains(currentItem))
                {
                    // NB: `GetConstant(...) is { }` — a true null test. Variable
                    // overloads `operator !=`/`==`, so a plain `!= null` would
                    // bind to those overloads and report a null reference as
                    // non-null; the pattern bypasses them.
                    if (PackageRegistry.Instance.GetConstant(currentItem) is { } pkgConst)
                    {
                        variable = MaterializeConstant(pkgConst);
                    }
                    else if (_currentSyntetizeIndex + 1 < _data.Length &&
                             _data[_currentSyntetizeIndex + 1] == "(")
                    {
                        variable = new Variable(currentItem, "00000000", Constants.DATATYPES.STD_LOGIC_VECTOR)
                        {
                            Size = 8,
                            IsStub = true
                        };
                    }
                    else if (IsPredefinedName(currentItem) ||
                             PackageRegistry.Instance.IsDeclaredName(currentItem))
                    {
                        // Either a VHDL predefined name always in scope (e.g. `now`)
                        // or a name that IS declared somewhere in the source but that
                        // Kmila's scope-builder did not evaluate into the live Variable
                        // set (e.g. a local constant whose initializer references a
                        // generic). Not an undeclared-identifier error — keep the
                        // historical stub so synthesis proceeds as before.
                        variable = new Variable(currentItem, "00000000", Constants.DATATYPES.STD_LOGIC_VECTOR)
                        {
                            Size = 8,
                            IsStub = true
                        };
                    }
                    else
                    {
                        throw new Parser.Models.ParserException(
                            $"no declaration for \"{currentItem}\"", SourceLine, 0, currentItem);
                    }
                }
                else
                {
                    variable = Variables.First(p => p.Name.ToLower().Equals(currentItem.ToLower()));
                }

                // Record field paths through this object (`m(i).lo`, `p.a.b`).
                if ((variable.Layout != null || variable.ElementShape?.Layout != null)
                    && TryParseFieldPath() is { } fieldRead)
                {
                    items.Add(fieldRead);
                    continue;
                }

                // Check for array/vector element access: var(index). Use
                // the deferred form so the bit is re-read at expression
                // evaluation time rather than frozen at parse time.
                // Bench Phase F bug #5 sub-cause D fix.
                if (_currentSyntetizeIndex + 1 < _data.Length && _data[_currentSyntetizeIndex + 1] == "(")
                {
                    _pendingDynamicIndexSource = null;
                    _pendingDynamicIndexVar = null;
                    _pendingDynamicIndexExpression = null;
                    var idx = ParseIndexedAccessDeferred(variable);
                    if (idx.success)
                    {
                        items.Add(idx.deferred!);
                        continue;
                    }
                    if (_pendingDynamicIndexSource is not null && _pendingDynamicIndexVar is not null)
                    {
                        items.Add(new Models.DynamicIndex(_pendingDynamicIndexSource, _pendingDynamicIndexVar));
                        _pendingDynamicIndexSource = null;
                        _pendingDynamicIndexVar = null;
                        continue;
                    }
                    if (_pendingDynamicIndexSource is not null && _pendingDynamicIndexExpression is not null)
                    {
                        items.Add(new Models.DynamicIndex(_pendingDynamicIndexSource,
                                                          _pendingDynamicIndexExpression,
                                                          Variables));
                        _pendingDynamicIndexSource = null;
                        _pendingDynamicIndexExpression = null;
                        continue;
                    }
                }

                item = variable;
            }
            else if (currentItem.Contains('.') && TryParseFieldPath() is { } fieldRead)
            {
                // `p.lo`, `q.a.hi`, `r.d(2)` — one selected-name token.
                items.Add(fieldRead);
                continue;
            }
            else if (currentItem.Split('.') is [.., var package, var name]
                     && PackageRegistry.Instance.GetConstant(package, name) is { } selected)
            {
                // `work.pkg.c` / `pkg.c` — a package constant by selected name.
                item = MaterializeConstant(selected);
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
    /// Tier 4.1 (2026-07-28): dynamic attributes (`event`, `last_value`,
    /// `stable`, `active`) return a <see cref="Models.DeferredAttribute"/>
    /// so Execute() re-evaluates against live SignalAttributeHandler state;
    /// static integer-class attributes (`length`/`high`/`low`/`left`/`right`)
    /// still inline-evaluate to a plain Variable since their value is fixed
    /// at declaration time.
    /// </summary>
    /// <param name="token">The token to parse.</param>
    /// <returns>Tuple of (isAttribute, result) where result is either a Variable (static attrs) or DeferredAttribute (dynamic).</returns>
    private (bool isAttribute, object result) TryParseSignalAttribute(string token)
    {
        if (!token.Contains("'")) return (false, null!);
        int apostropheIndex = token.IndexOf("'");
        if (apostropheIndex == 0) return (false, null!);

        string signalName = token[..apostropheIndex].Trim();
        string attributeName = token[(apostropheIndex + 1)..].Trim().ToLower();

        // Known signal attributes. Bench bug #23 (P4): 'length is a
        // value-class array attribute (returns the vector / array length
        // as an integer). Without it, `to_unsigned(N, slot_cnt'length)`
        // tokens corrupted the surrounding function-call arg list and the
        // parser ran past `end architecture;` consuming tokens until EOF.
        string[] validAttributes = ["event", "last_value", "stable", "active", "length", "high", "low", "left", "right", "ascending"];
        if (!validAttributes.Contains(attributeName)) return (false, null!);

        var signal = Variables.FirstOrDefault(v => v.Name.Equals(signalName, StringComparison.OrdinalIgnoreCase));
        if (signal is null) return (false, null!);

        // Dynamic attributes: wrap in DeferredAttribute so Execute() re-evaluates.
        if (attributeName is "event" or "last_value" or "stable" or "active")
        {
            Constants.DATATYPES resultType = attributeName == "last_value"
                ? signal.Type
                : Constants.DATATYPES.BOOLEAN;

            // Peek ahead for `'stable(T)` argument. VECTORS_ATTRIBUTES tokens
            // glue as `sig'stable`; the (T) part arrives as separate tokens.
            long? timeArg = null;
            if (attributeName == "stable")
                timeArg = TryConsumeStableTimeArg();

            return (true, new Models.DeferredAttribute(signal, attributeName, resultType, timeArg));
        }

        // Range-aware static attributes. Derive 'left/'right/'high/'low/'ascending
        // from the declared range when present; otherwise fall back to the
        // conventional (Size-1 downto 0) form (identical to the legacy values).
        int effLeft  = signal.HasDeclaredRange ? signal.LeftBound!.Value  : Math.Max(0, signal.Size - 1);
        int effRight = signal.HasDeclaredRange ? signal.RightBound!.Value : 0;
        bool effDownto = !signal.HasDeclaredRange || signal.IsDownto;

        // 'ascending is a BOOLEAN attribute — type the result accordingly.
        if (attributeName == "ascending")
        {
            var ascVar = new Variable(
                $"Attribute-{signalName}'ascending",
                effDownto ? "false" : "true",
                Constants.DATATYPES.BOOLEAN);
            ascVar.PortType = Constants.PortType.OUT;
            return (true, ascVar);
        }

        object attributeValue = attributeName switch
        {
            "length" => signal.Size,
            "high"   => Math.Max(effLeft, effRight),
            "low"    => Math.Min(effLeft, effRight),
            "left"   => effLeft,
            "right"  => effRight,
            _        => 0
        };
        var resultVar = new Variable(
            $"Attribute-{signalName}'{attributeName}",
            attributeValue.ToString() ?? "0",
            Constants.DATATYPES.INTEGER);
        resultVar.PortType = Constants.PortType.OUT;
        return (true, resultVar);
    }

    /// <summary>
    /// Tier 4.1: peek at tokens following a `'stable` attribute and consume
    /// `(T)` if present, returning T as raw ns. The scale conversion into
    /// the tick*1000+delta units used by <see cref="SignalHistory"/> and
    /// <see cref="SignalAttributeHandler.OnDeltaCommit"/> happens inside
    /// <c>SignalAttributeHandler.GetStable(sig, ns)</c> using the engine's
    /// currently-registered <c>TickResolutionNs</c> (Tier 4.1 close-out —
    /// non-1 ns bench resolutions were previously off by that factor).
    /// </summary>
    private long? TryConsumeStableTimeArg()
    {
        // Tokens after `sig'stable`: might be `(`, `10`, `ns`, `)` or none.
        int nextIdx = _currentSyntetizeIndex + 1;
        if (nextIdx >= _data.Length || _data[nextIdx] != "(") return null;

        int close = MatchingClose(nextIdx);
        if (close < 0) return null;

        // Body between paren: expect `<number>` `<unit>` (2 tokens) or a
        // single integer token. Anything else — abort and don't consume.
        int bodyLen = close - nextIdx - 1;
        long ns;
        if (bodyLen == 2 &&
            long.TryParse(_data[nextIdx + 1], out var qty))
        {
            string unit = _data[nextIdx + 2].ToLower();
            ns = unit switch
            {
                "fs"  => qty / 1_000_000,
                "ps"  => qty / 1_000,
                "ns"  => qty,
                "us"  => qty * 1_000,
                "ms"  => qty * 1_000_000,
                "sec" => qty * 1_000_000_000,
                _     => qty
            };
        }
        else if (bodyLen == 1 && long.TryParse(_data[nextIdx + 1], out var raw))
        {
            ns = raw;  // bare integer — treated as ns
        }
        else
        {
            return null;
        }

        // Advance past the (T) — the outer loop already does one ++ for the
        // attribute token itself, so we need to skip (bodyLen + 2) more.
        _currentSyntetizeIndex += bodyLen + 2;

        // Return raw ns; scale to tick*1000+delta happens inside
        // SignalAttributeHandler.GetStable using the engine's tick resolution.
        return ns;
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
                if (variable is not null)
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

        // Collect arguments until closing parenthesis.
        //
        // Historical shape: each token at depth 1 was pushed to `args`
        // individually; commas were the ONLY arg separator (and were
        // no-op'd rather than triggering a flush). That collapsed a
        // sub-expression like `unsigned(a) + unsigned(b)` into three
        // args `[a-val, "+", b-val]` — the outer cast primitive then
        // saw a malformed arg count and returned empty. `argBuffer`
        // buffers tokens *within one comma-separated arg*, and
        // `FoldArgExpression` reduces the buffered items (which may
        // contain Operators) to a single Variable before pushing to
        // `args`. Single-token args behave identically to the legacy
        // path — the fix is invisible unless the arg is a sub-expr.
        List<object> args = new();
        List<object> argBuffer = new();
        int parenDepth = 1;

        void FlushArg()
        {
            if (argBuffer.Count == 0) return;
            args.Add(FoldArgExpression(argBuffer));
            argBuffer.Clear();
        }

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
                    FlushArg();
                    _currentSyntetizeIndex++;
                    break;
                }
            }
            else if (token == "," && parenDepth == 1)
            {
                FlushArg();
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
                        argBuffer.Add(nested.result);
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

                // Record field argument: `to_unsigned(c.lim, 4)`, `f(m(i).lo)`.
                if ((token.Contains('.') || variable?.Layout != null || variable?.ElementShape?.Layout != null)
                    && TryParseFieldPath() is { } fieldArg)
                {
                    argBuffer.Add(new Models.DeferredExpression([fieldArg]));
                    continue;
                }

                // Bench bug #20 (P2A LOAD pipeline): a slice / index access
                // on a Variable arg — `mbr_reg(7 downto 0)`, `mem(idx)` —
                // would otherwise lose the slice info and the called
                // function would see the whole vector. When the arg looks
                // like `<var> ( <int> [downto|to <int>] )`, build a
                // DeferredIndex so the slice is re-evaluated at call time.
                if (!ReferenceEquals(variable, null) && nextIsParen)
                {
                    var idxArg = TryConsumeSliceArg(variable);
                    if (idxArg != null)
                    {
                        argBuffer.Add(idxArg);
                        continue; // index already past the inner ')'
                    }
                }

                if (!ReferenceEquals(variable, null))
                {
                    argBuffer.Add(variable);
                }
                else if (int.TryParse(token, out int intVal))
                {
                    argBuffer.Add(intVal);
                }
                else if (_arithOperators.IsMatch(token) || _logicOperators.IsMatch(token.ToLower()))
                {
                    // Binary / unary operator inside a function-call argument.
                    // Recording it as an Operator (not a bare string) lets
                    // FoldArgExpression collapse the sub-expression into a
                    // single Variable when the arg is flushed. Was the
                    // silent-empty-arg trap that broke `std_logic_vector(
                    // unsigned(a) + unsigned(b))`-style one-liners.
                    argBuffer.Add(new Operator(token));
                }
                else
                {
                    argBuffer.Add(token);
                }
            }

            _currentSyntetizeIndex++;
        }
        FlushArg();

        // Determine result type
        Constants.DATATYPES resultType = functionName.ToLower() switch
        {
            "rising_edge" or "falling_edge" or "is_x" or "std_match" => Constants.DATATYPES.BOOLEAN,
            "to_integer" or "conv_integer" or "clog2" => Constants.DATATYPES.INTEGER,
            "to_x01" or "to_x01z" or "to_ux01" when args.FirstOrDefault() is Variable { Type: Constants.DATATYPES.STD_LOGIC } =>
                Constants.DATATYPES.STD_LOGIC,
            "to_stdulogic" => Constants.DATATYPES.STD_LOGIC,
            "to_bit" => Constants.DATATYPES.BIT,
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

    // Reduce a buffered function-call argument (mix of Variables,
    // DeferredFunctions, DeferredIndex, ints, and Operators) into a single
    // value suitable for passing to a StandardFunctions primitive.
    //
    // Called by ParseFunctionCall on every comma / closing paren so a
    // sub-expression like `unsigned(a) + unsigned(b)` inside
    // `std_logic_vector(...)` reduces to one arg rather than three
    // pseudo-args.
    //
    // When the buffer holds a real expression (>1 item), we wrap the item
    // list in a DeferredExpression instead of evaluating at parse time.
    // This is essential: IEEE cast/arith primitives run through
    // DeferredFunction — evaluating the sub-expression at parse time
    // would capture the pre-elaboration values of any Variables it
    // reads (typically all 0s), and the outer DeferredFunction would
    // keep evaluating that frozen constant on every tick regardless of
    // how the source ports change. The DeferredExpression re-folds on
    // each Evaluate call so live values flow through.
    private static object FoldArgExpression(List<object> items)
    {
        if (items.Count == 0) return "";
        if (items.Count == 1) return items[0];
        // Preserve the buffered list under a defensive copy so subsequent
        // ParseFunctionCall reuse of the shared buffer doesn't mutate us.
        return new Models.DeferredExpression(new List<object>(items));
    }

    // Exposed to Models.DeferredExpression so it can resolve its buffered
    // items using the same rules as the parse-time fold. Kept as a
    // static helper because DeferredExpression is a Model / lightweight
    // wrapper and shouldn't drag in Operation instance state.
    /// <summary>
    /// Resolves a buffered expression item to a <see cref="Variable"/> using the
    /// same rules as the parse-time fold. Exposed for <c>Models.DeferredExpression</c>.
    /// </summary>
    /// <param name="item">The buffered item to resolve.</param>
    /// <returns>The resolved <see cref="Variable"/>.</returns>
    public static Variable ToArgVariablePublic(object item) => ToArgVariable(item);

    private static Variable ToArgVariable(object item)
    {
        Variable v = item switch
        {
            Variable vv => vv,
            Models.DeferredFunction df => df.Evaluate(),
            Models.DeferredIndex di => di.Evaluate(),
            Models.DynamicIndex dyn => dyn.Evaluate(),
            Models.IDeferredOperand deferred => deferred.Evaluate(),
            Models.DeferredExpression de => de.Evaluate(),
            int i => new Variable("__ArgInt", i, Constants.DATATYPES.INTEGER),
            _ => new Variable("__ArgRaw", item?.ToString() ?? "", Constants.DATATYPES.STD_LOGIC_VECTOR),
        };
        // Function-call return Variables (Name="Function-<fn>") come out of
        // ParseFunctionCall / StandardFunctions without a Size set. When
        // they're then used as an arith operand inside a folded expression,
        // SolveArithOperation reads Size to pick the modular-arith width —
        // a missing Size collapses width to 1 and turns 3+5 into 0. Infer
        // Size from the string length of the current Value so downstream
        // arith uses the right width. Only touches vector-typed values so
        // we don't clobber INTEGER / BOOLEAN sizes elsewhere.
        if (v.Type == Constants.DATATYPES.STD_LOGIC_VECTOR && v.Size <= 0)
        {
            var s = v.Value?.ToString();
            if (!string.IsNullOrEmpty(s)) v.Size = s.Length;
        }
        return v;
    }

    /// <summary>
    /// Parse a user-defined function call and return either a
    /// <see cref="Models.DeferredUserFunction"/> wrapper (when the function
    /// body is registered — Evaluate each fire) or a static stub
    /// <see cref="Variable"/> (unknown / IEEE-stub — pre-existing legacy path).
    /// </summary>
    /// <remarks>
    /// 2026-07-20 — pre-fix, this method evaluated argument expressions
    /// eagerly and baked the function result into a <see cref="Variable"/>
    /// pushed onto the operation's <c>Items</c>. On re-fire of the
    /// enclosing <see cref="Operation"/> the frozen variable just
    /// re-pushed its stale value — an LFSR routed through a user
    /// <c>function</c> would transition once then freeze. Now the wrapper
    /// carries the argument <b>expressions</b> and re-evaluates them
    /// against the live scope on every <see cref="Models.DeferredUserFunction.Evaluate"/>
    /// call. Returned as <c>object</c> so the caller pushes it as-is onto
    /// <c>Items</c> — <see cref="Execute()"/> switches on the type and
    /// invokes <c>Evaluate</c> per fire.
    /// </remarks>
    /// <inheritdoc/>
    public IEnumerable<string> ReadTexts => [TargetSelectors(_rawData), RawRhs];

    /// <summary>
    /// The selectors of an assignment's target (<c>(addr)</c> of <c>mem(addr) &lt;= d</c>):
    /// read by the statement, unlike the target's name. Empty for an aggregate target.
    /// </summary>
    internal static string TargetSelectors(string statement)
    {
        int opIdx = statement.IndexOf("<=", StringComparison.Ordinal);
        if (opIdx < 0) opIdx = statement.IndexOf(":=", StringComparison.Ordinal);
        if (opIdx < 0) return string.Empty;
        string lhs = statement[..opIdx].Trim();
        if (lhs.StartsWith('(')) return string.Empty;
        int end = 0;
        while (end < lhs.Length && (char.IsLetterOrDigit(lhs[end]) || lhs[end] == '_')) end++;
        return lhs[end..];
    }

    /// <summary>The assignment's right-hand side as written (empty when there is no <c>&lt;=</c>/<c>:=</c>).</summary>
    private string RawRhs
    {
        get
        {
            int opIdx = _rawData.IndexOf("<=", StringComparison.Ordinal);
            if (opIdx < 0) opIdx = _rawData.IndexOf(":=", StringComparison.Ordinal);
            return opIdx >= 0 ? _rawData[(opIdx + 2)..].Trim() : string.Empty;
        }
    }

    /// <summary>
    /// A qualified expression <c>T'(e)</c> has the value of <c>(e)</c>: the
    /// type mark only fixes the type GHDL resolves the expression to, so the
    /// mark is dropped and the parenthesised operand kept.
    /// </summary>
    private static string[] DropQualifierMarks(string[] tokens)
    {
        if (!tokens.Any(Parser.Models.Token.IsQualifierMarkText)) return tokens;
        var kept = new List<string>(tokens.Length);
        for (int i = 0; i < tokens.Length; i++)
            if (!(Parser.Models.Token.IsQualifierMarkText(tokens[i]) && i + 1 < tokens.Length && tokens[i + 1] == "("))
                kept.Add(tokens[i]);
        return [.. kept];
    }

    /// <summary>
    /// The objects this assignment writes: the target's object, every element of an
    /// aggregate target, or nothing for a condition.
    /// </summary>
    public IEnumerable<Variable> WrittenObjects =>
        AggregateTarget is { } aggregate ? aggregate.Elements.Select(e => e.Base)
        : Result is null ? [] : [Result];

    /// <summary>
    /// G55: an aggregate target takes a value of exactly its total width when
    /// both widths are certain (GHDL rejects the mismatch at analysis).
    /// </summary>
    private void ValidateAggregateTargetWidth()
    {
        if (AggregateTarget?.StaticWidth(Variables) is int targetWidth
            && ExpressionWidthInference.TryComputeWidth(RawRhs, Variables) is int w && w != targetWidth)
            throw ResultError($"width mismatch: {targetWidth}-bit aggregate target assigned a {w}-bit value");
    }

    /// <summary>
    /// G17/G49 (v3.1.1): a part target with literal indices must lie inside
    /// its object, and a range target takes a value of exactly its width
    /// when that width is certain (as GHDL checks).
    /// </summary>
    private void ValidatePartialTarget()
    {
        if (Target is not { IsPartial: true, StaticIndices: { } idx } || Result.Size <= 0) return;
        if (VectorIndexing.Range(Result, idx.Left, idx.Right, Result.Size) == null)
            throw ResultError(idx.Left == idx.Right
                ? $"index {idx.Left} out of range of \"{Result.Name}\""
                : $"range {idx.Left}..{idx.Right} out of range of \"{Result.Name}\"");
        if (idx.Left == idx.Right || Result.ElementSize > 1) return;

        int targetWidth = VectorIndexing.RangeWidth(Result, idx.Left, idx.Right);
        if (ExpressionWidthInference.TryComputeWidth(RawRhs, Variables) is int w && w != targetWidth)
            throw ResultError($"width mismatch: {targetWidth}-bit slice of \"{Result.Name}\" assigned a {w}-bit value");
    }

    /// <summary>
    /// v1.17.0 (P3): build a usable <see cref="Variable"/> from a package
    /// constant resolved out of <see cref="PackageRegistry"/>. The scanner
    /// registration path stores a constant's <c>Value</c> but leaves
    /// <c>Type = UNKNOWN</c>; infer a concrete datatype/size from the value so
    /// downstream <see cref="Operator"/> evaluation treats it like a real
    /// signal — an integer literal becomes INTEGER, a bit pattern like "00001"
    /// becomes a STD_LOGIC_VECTOR sized to its length — instead of silently
    /// collapsing to the old 8-bit "00000000" stub. An already-typed constant
    /// is copied through unchanged.
    /// </summary>
    /// <summary>
    /// v1.19.0 (P4): reject a single-atom assignment whose RHS type or width
    /// cannot match the LHS target — C4 (std_logic vs std_logic_vector) and C5
    /// (vector width mismatch), matching GHDL. Deliberately conservative: it
    /// fires only when the RHS is ONE lone literal or whole-signal reference,
    /// because the type/width of a general RHS expression (concatenation,
    /// conversions, <c>others =&gt;</c>, slices, function results) is not known
    /// until evaluation, and those constructs are pervasive in valid designs.
    /// </summary>
    private void ValidateAssignmentTypeWidth()
    {
        // LHS must be a real, whole declared target (not a stub, not the BOOLEAN
        // ConditionResult of a comparison, not a part target — checked by
        // ValidatePartialTarget — and not undetermined).
        if (Result is null || Result.IsStub || Target is { IsPartial: true }) return;
        if (string.Equals(Result.Name, "ConditionResult", StringComparison.Ordinal)) return;
        var lhsType = Result.Type;
        if (lhsType == Constants.DATATYPES.UNKNOWN) return;

        static bool IsScalar(Constants.DATATYPES t) =>
            t == Constants.DATATYPES.STD_LOGIC || t == Constants.DATATYPES.BIT;
        static bool IsVector(Constants.DATATYPES t) =>
            t == Constants.DATATYPES.STD_LOGIC_VECTOR;

        bool lhsScalar = IsScalar(lhsType), lhsVector = IsVector(lhsType);
        // Only std_logic / std_logic_vector targets are checked; INTEGER/NATURAL/
        // BOOLEAN etc. coerce legitimately and are left alone.
        if (!(lhsScalar || lhsVector)) return;

        // The raw source RHS (after the assignment operator). The engine re-joins
        // tokens with spaces, e.g. `a ( 22 downto 0 )`. Shared by the single-atom
        // char-literal reclassification and the expression-width path below.
        string rawRhs = RawRhs;

        // ---- Single-atom RHS (one Variable; existing behaviour, unchanged) ----
        if (Items.Length == 1 && Items[0] is Variable rhs)
        {
            if (rhs.IsStub || rhs.Type == Constants.DATATYPES.UNKNOWN) return;
            if (rhs.HasPlaceholderType) return;
            string rhsVal = rhs.Value?.ToString() ?? string.Empty;
            if (rhsVal.StartsWith("(others", StringComparison.OrdinalIgnoreCase)) return; // aggregate → width = LHS by construction

            bool rhsScalar = IsScalar(rhs.Type), rhsVector = IsVector(rhs.Type);

            // A SINGLE-quoted character literal ('0','1','X','W','L','H','Z','U','-')
            // is ALWAYS a scalar std_logic value. Kmila's datatype finder mislabels
            // 'X'/'W'/'L'/'H' as a vector, which would false-reject a legal `y <= 'X'`.
            // A DOUBLE-quoted `"0"` is a 1-bit vector and is deliberately NOT matched.
            if (rhs.Name.StartsWith("Literal-", StringComparison.Ordinal)
                && rawRhs.Length == 3 && rawRhs[0] == '\'' && rawRhs[^1] == '\'')
            {
                rhsScalar = true;
                rhsVector = false;
            }

            if (!(rhsScalar || rhsVector)) return;

            // C4 — scalar vs vector type mismatch.
            if ((lhsScalar && rhsVector) || (lhsVector && rhsScalar))
            {
                string lhsClass = lhsScalar ? "std_logic" : "std_logic_vector";
                string rhsClass = rhsScalar ? "std_logic" : "std_logic_vector";
                throw ResultError($"type mismatch: cannot assign a {rhsClass} value to {lhsClass} signal \"{Result.Name}\"");
            }

            // C5 — width mismatch. Only on RELIABLE widths (HasDeclaredRange LHS,
            // and a pure-binary literal or declared-range signal RHS).
            if (lhsVector && rhsVector && Result.HasDeclaredRange && Result.Size > 0)
            {
                int lhsW = Result.Size;
                int? rhsW = null;
                if (rhs.Name.StartsWith("Literal-", StringComparison.Ordinal))
                {
                    string bits = rhsVal.Replace("\"", string.Empty).Replace("'", string.Empty);
                    if (bits.Length > 0 &&
                        bits.All(c => "01UXZWLH-".IndexOf(char.ToUpperInvariant(c)) >= 0))
                        rhsW = bits.Length;
                }
                else if (rhs.HasDeclaredRange && rhs.Size > 0)
                {
                    rhsW = rhs.Size;
                }

                // Fallback for a based literal (x"…"/o"…"/b"…") whose stored value
                // isn't a pure-binary string: its length IS certain. (v1.27.0)
                rhsW ??= ExpressionWidthInference.TryComputeWidth(rawRhs, Variables);

                if (rhsW is int w && lhsW != w)
                    throw ResultError($"width mismatch: {lhsW}-bit signal \"{Result.Name}\" assigned a {w}-bit value");
            }
            return;
        }

        // ---- Expression RHS (v1.26.0): conservative certain-width inference ----
        // Everything here is purely additive; it only fires when the width/type is
        // provably certain, so it never false-rejects an expression Kmila can't size.

        // C4 — a concatenation is always an array, so it is illegal into a scalar target.
        if (lhsScalar && ExpressionWidthInference.HasTopLevelConcat(rawRhs))
            throw ResultError($"type mismatch: cannot assign a std_logic_vector value to std_logic signal \"{Result.Name}\"");

        // C5 — compare a fully-certain RHS width against a RELIABLE LHS width.
        if (lhsVector && Result.HasDeclaredRange && Result.Size > 0)
        {
            int? exprW = ExpressionWidthInference.TryComputeWidth(rawRhs, Variables);
            if (exprW is int ew && ew != Result.Size)
                throw ResultError($"width mismatch: {Result.Size}-bit signal \"{Result.Name}\" assigned a {ew}-bit value");
        }
    }

    /// <summary>
    /// v1.17.0 (P3): VHDL predefined parameterless names that resolve to no
    /// user declaration yet are always in scope, so the undeclared-identifier
    /// check must not reject them. <c>now</c> (the current simulation time) is
    /// the one the sample corpus exercises.
    /// </summary>
    private static bool IsPredefinedName(string name) =>
        string.Equals(name, "now", StringComparison.OrdinalIgnoreCase);

    private static Variable MaterializeConstant(Variable c)
    {
        object value = c.Value;
        Constants.DATATYPES type = c.Type;
        int size = c.Size;

        if (type == Constants.DATATYPES.UNKNOWN)
        {
            if (value is int)
            {
                type = Constants.DATATYPES.INTEGER;
            }
            else if (value is string vs)
            {
                string bare = vs.Trim().Trim('"');
                if (int.TryParse(bare.Replace("_", string.Empty), out _))
                {
                    type = Constants.DATATYPES.INTEGER;
                }
                else if (bare.Length > 0 &&
                         bare.All(ch => "01UXZWLH-".IndexOf(char.ToUpperInvariant(ch)) >= 0))
                {
                    type = Constants.DATATYPES.STD_LOGIC_VECTOR;
                    size = bare.Length;
                    value = bare;
                }
                else
                {
                    // Unresolvable value (e.g. an enum-literal name) — fall back
                    // to the historical vector shape so synthesis still proceeds.
                    type = Constants.DATATYPES.STD_LOGIC_VECTOR;
                }
            }
        }

        var mv = new Variable(c.Name, value, type, size) { IsConstant = true };
        mv.CopyShapeFrom(c);
        return mv;
    }

    private (bool success, object item) ParseUserDefinedFunctionCall(string functionName)
    {
        // Skip the function name (already processed), move to "("
        _currentSyntetizeIndex++;

        if (_currentSyntetizeIndex >= _data.Length || _data[_currentSyntetizeIndex] != "(")
            return (false, null!);

        // Bench bug #19 follow-up: collect each top-level comma-separated
        // argument expression so we can hand them to FunctionExecutor.
        _currentSyntetizeIndex++; // consume '('
        var argExpressions = new List<string>();
        var currentArg = new System.Text.StringBuilder();
        int parenDepth = 1;
        while (_currentSyntetizeIndex < _data.Length && parenDepth > 0)
        {
            string token = _data[_currentSyntetizeIndex];
            if (token == "(") parenDepth++;
            else if (token == ")")
            {
                parenDepth--;
                if (parenDepth == 0) { _currentSyntetizeIndex++; break; }
            }
            if (parenDepth == 1 && token == ",")
            {
                if (currentArg.Length > 0)
                {
                    argExpressions.Add(currentArg.ToString().Trim());
                    currentArg.Clear();
                }
            }
            else
            {
                currentArg.Append(token).Append(' ');
            }
            _currentSyntetizeIndex++;
        }
        if (currentArg.Length > 0)
            argExpressions.Add(currentArg.ToString().Trim());

        // Real path: function body registered → build a DeferredUserFunction
        // wrapper that re-evaluates on every Operation.Execute. Falls back
        // to the legacy "0" stub Variable when the function is unknown or
        // has no body (keeps black-box / IEEE stubs working as before).
        try
        {
            var def = PackageRegistry.Instance.GetFunction(functionName);
            if (def != null && !string.IsNullOrEmpty(def.BodyRaw))
            {
                var deferred = new Models.DeferredUserFunction(
                    functionName,
                    argExpressions,
                    def.ReturnType,
                    def.ReturnTypeSize,
                    Variables);
                return (true, deferred);
            }
        }
        catch
        {
            // Fall through to stub on any registry error so the rest of
            // the expression still synthesises.
        }

        // v1.17.0 (P3): a call to a name unknown to every registry is an
        // undefined subprogram — GHDL: `no declaration for "X"`. Signature-only
        // package/IEEE functions (q_mul, resize, …) and constant arrays indexed
        // like a call (rom_data(addr)) are NOT undefined: they are known to a
        // registry, so they keep the legacy black-box stub below. Mirrors the
        // known-callable predicate already used by ParseFunctionCall.
        bool knownCallable = PackageRegistry.IsKnownName(functionName);
        if (!knownCallable)
        {
            throw new Parser.Models.ParserException(
                $"no declaration for \"{functionName}\"", SourceLine, 0, functionName);
        }

        // Return a stub variable representing an unresolved function result
        // (black-box / package-header / unknown-name path).
        var resultVar = new Variable(
            $"UserFunction-{functionName}",
            "0",
            Constants.DATATYPES.STD_LOGIC_VECTOR
        );
        resultVar.PortType = Constants.PortType.OUT;
        resultVar.HasPlaceholderType = true; // black-box result: the real type is unknown

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

        // Get the value. Handle three cases:
        //   1. Literal char/bitstring:           others => '0'
        //   2. Nested-aggregate (2D arrays):     others => ( others => '0' )
        //   3. Plain identifier (e.g. constant): others => MY_BLANK
        string value = "0";
        if (_currentSyntetizeIndex < _data.Length)
        {
            // Nested aggregate? Look for the `( others => ...)` shape.
            if (_data[_currentSyntetizeIndex] == "("
                && _currentSyntetizeIndex + 1 < _data.Length
                && string.Equals(_data[_currentSyntetizeIndex + 1], "others", StringComparison.OrdinalIgnoreCase))
            {
                _currentSyntetizeIndex++; // skip inner '('
                var inner = ParseOthersAggregate();
                if (inner.success)
                {
                    // Use the inner aggregate's value as the per-row payload.
                    // The outer aggregate's repeated rows each get the inner
                    // value verbatim.
                    value = inner.variable.Value?.ToString() ?? "0";
                }
            }
            else
            {
                value = _data[_currentSyntetizeIndex].Replace("'", "").Replace("\"", "");
                _currentSyntetizeIndex++;
            }
        }

        // Skip closing ')' if present
        if (_currentSyntetizeIndex < _data.Length && _data[_currentSyntetizeIndex] == ")")
        {
            _currentSyntetizeIndex++;
        }

        // Create a variable that represents the aggregate. We DELIBERATELY
        // do NOT pass Size to the constructor — the 4-arg ctor calls the
        // Size setter with `value=0`, which in turn triggers
        // `Variable.Size`'s `(others=>X)` expansion path (Variable.cs:135)
        // and collapses our marker to an empty string. Keeping Size at its
        // default (0) AND skipping the setter preserves the marker string
        // intact so `SignalScheduler.ExpandAggregateMarker` can expand it
        // against the LHS target's declared width at assignment time.
        var resultVar = new Variable
        {
            Name = $"Aggregate_others_{value}",
            Type = Constants.DATATYPES.STD_LOGIC_VECTOR,
            Value = $"(others=>{value})",
            PortType = Constants.PortType.OUT,
        };

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

    /// <summary>True for an operator or a constant (literals, declared constants, generics): an operand whose value never changes during the run.</summary>
    private static bool IsConstantOperand(object item) => item is Operator || item is Variable { IsConstant: true };

    /// <summary>
    /// Parses `name(N)` or `name(H downto L)` and returns a
    /// <see cref="Models.DeferredIndex"/> that re-reads the source variable's
    /// bits each time the containing expression is evaluated. The legacy
    /// <see cref="ParseIndexedAccess"/> entry point still resolves
    /// immediately for non-engine callers (e.g. tests).
    /// </summary>
    /// <summary>Index of the <c>)</c> token closing the <c>(</c> at <paramref name="open"/> in the token list, or -1.</summary>
    private int MatchingClose(int open)
    {
        int depth = 0;
        for (int i = open; i < _data.Length; i++)
        {
            if (_data[i] == "(") depth++;
            else if (_data[i] == ")" && --depth == 0) return i;
        }
        return -1;
    }

    /// <summary>
    /// At a name token, gathers it with its following <c>( … )</c> groups and
    /// <c>.field</c> tokens; when that forms a <see cref="Models.ValuePath"/>
    /// with a record field, consumes it and returns its <see cref="Models.FieldRead"/>.
    /// Null (nothing consumed) when there is no field selector.
    /// </summary>
    private Models.FieldRead? TryParseFieldPath()
    {
        var parts = new List<string> { _data[_currentSyntetizeIndex] };
        int next = _currentSyntetizeIndex + 1;
        while (next < _data.Length)
        {
            if (_data[next] == "(")
            {
                int close = MatchingClose(next);
                if (close < 0) break;
                parts.AddRange(_data[next..(close + 1)]);
                next = close + 1;
            }
            else if (Parser.Models.Token.IsFieldSelectorText(_data[next]))
                parts.Add(_data[next++]);
            else break;
        }

        string text = string.Join(" ", parts);
        if (Models.ValuePath.Parse(text, Variables) is not { HasField: true } path)
            return null;
        if (path.FieldError is { } error)
            throw new Parser.Models.ParserException(error, SourceLine, 0, text);
        _currentSyntetizeIndex = next;
        return new Models.FieldRead(path);
    }

    /// <summary>
    /// At a <c>(</c> in operand position: when the group is an aggregate, consumes
    /// it and returns it typed by its context — the operand it is compared or
    /// concatenated with, else the assignment target. A lone
    /// <c>(others =&gt; c)</c> for a non-record target keeps the fill-marker path.
    /// </summary>
    private Models.Aggregate? TryParseAggregate(List<object> items)
    {
        int open = _currentSyntetizeIndex, close = MatchingClose(open);
        if (close < 0) return null;
        string inner = string.Join(" ", _data[(open + 1)..close]);
        if (!Models.Aggregate.IsAggregate(inner)) return null;

        Variable? shape = items.Count >= 2 && items[^1] is Operator && items[^2] is Variable partner
                          && (partner.Layout != null || partner.ElementSize > 1)
            ? partner
            : AggregateTarget is not null ? null : Target?.Path.StaticShape ?? Result;
        bool othersOnly = _data[open + 1].Equals("others", StringComparison.OrdinalIgnoreCase)
                          && !inner.Contains(',');
        if (othersOnly && shape?.Layout == null) return null;

        _currentSyntetizeIndex = close + 1;
        return Models.Aggregate.Parse(inner, shape, Variables);
    }

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

        // Get start index - could be a simple number, variable, or expression.
        // Paren-aware: the index expression itself may contain nested parens
        // (e.g. `rom(to_integer(unsigned(addr)))`), so we track depth and
        // only break on `downto`/`to`/`)` at the outer (depth==0) level.
        if (_currentSyntetizeIndex < _data.Length)
        {
            string expr = "";
            int tokenCount = 0;
            int parenDepth = 0;
            while (_currentSyntetizeIndex + tokenCount < _data.Length)
            {
                string token = _data[_currentSyntetizeIndex + tokenCount];
                string tokenLower = token.ToLower();
                if (parenDepth == 0 && (tokenLower == "downto" || tokenLower == "to" || token == ")"))
                    break;
                if (token == "(") parenDepth++;
                else if (token == ")") parenDepth--;
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
                    // Arithmetic over constants only (`N-1`) folds to a fixed
                    // index; one over a signal or variable
                    // (`to_integer(unsigned(sel)) + 1`) stays a run-time index
                    // (below) instead of freezing the operands' parse-time values.
                    try
                    {
                        Operation op = new(expr, Variables);
                        if (op.Items.All(IsConstantOperand))
                            startIndex = Convert.ToInt32(op.Evaluate());
                    }
                    catch { /* ignore errors */ }
                }
                else if (!expr.Contains(' '))
                {
                    // Bug #14: runtime-variable index. Only fires when the
                    // index is a single identifier referring to an INTEGER
                    // variable in scope (typically a for-loop counter).
                    var candidate = Variables.FirstOrDefault(v =>
                        v is not null && !string.IsNullOrEmpty(v.Name) &&
                        v.Name.Equals(expr, StringComparison.OrdinalIgnoreCase));
                    if (candidate is not null && candidate.Type == Constants.DATATYPES.INTEGER)
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

                // Bench bug #19: still no concrete int but the expression is
                // non-trivial (function call, signal-dependent arithmetic).
                // Capture it as a runtime-deferred expression that will be
                // re-evaluated each delta cycle when the indexed access fires.
                if (startIndex < 0)
                {
                    _pendingDynamicIndexSource = variable;
                    _pendingDynamicIndexExpression = expr;
                    // Swallow the closing ')' so the outer stream stays aligned.
                    if (_currentSyntetizeIndex < _data.Length && _data[_currentSyntetizeIndex] == ")")
                        _currentSyntetizeIndex++;
                    return (false, null);
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

    /// <summary>
    /// Bench bug #20 (P2A LOAD): when ParseFunctionCall encounters an arg
    /// shaped like <c>&lt;var&gt; ( &lt;int&gt; [downto|to &lt;int&gt;] )</c>,
    /// produce a <see cref="Models.DeferredIndex"/> so the inner slice is
    /// re-read at call time rather than collapsing to the whole vector.
    /// Returns null when the next tokens don't fit the slice pattern; in
    /// that case the caller falls back to passing the Variable bare.
    /// </summary>
    private Models.DeferredIndex? TryConsumeSliceArg(Variable source)
    {
        int sliceStart = _currentSyntetizeIndex + 1; // points at '('
        if (sliceStart >= _data.Length || _data[sliceStart] != "(") return null;

        // Need at least `( <int> )`.
        if (sliceStart + 2 >= _data.Length) return null;
        if (!int.TryParse(_data[sliceStart + 1], out int hi)) return null;

        // Single-bit form: `var(N)`.
        if (_data[sliceStart + 2] == ")")
        {
            // Advance past the slice tokens.
            _currentSyntetizeIndex = sliceStart + 3;
            return new Models.DeferredIndex(source, hi, hi, isSlice: false);
        }

        // Range form: `var(H downto L)` or `var(L to H)`.
        if (sliceStart + 4 >= _data.Length) return null;
        string dir = _data[sliceStart + 2].ToLower();
        if (dir != "downto" && dir != "to") return null;
        if (!int.TryParse(_data[sliceStart + 3], out int lo)) return null;
        if (_data[sliceStart + 4] != ")") return null;

        int high = dir == "downto" ? hi : lo;
        int low  = dir == "downto" ? lo : hi;
        _currentSyntetizeIndex = sliceStart + 5;
        return new Models.DeferredIndex(source, high, low, isSlice: true);
    }

    // Bug #14 side-channel: ParseIndexedAccessDeferred writes the source +
    // index Variable pair here when it detects a runtime-variable index;
    // Syntetize() reads + clears them right after the call to install a
    // DynamicIndex operand instead of a frozen DeferredIndex.
    private Variable? _pendingDynamicIndexSource;
    private Variable? _pendingDynamicIndexVar;

    // Bench bug #19 side-channel: ParseIndexedAccessDeferred writes the raw
    // index expression text here when the index is a non-trivial expression
    // (function call, signal-dependent arithmetic) that the parser can't
    // reduce to a single integer Variable at parse time. The DynamicIndex
    // operand built from this re-evaluates the expression each delta cycle.
    private string? _pendingDynamicIndexExpression;
}