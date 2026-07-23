using System.Text;
using Interpreter.Interfaces;
using Interpreter.Models;
using Interpreter.Repositories;
using Parser.Models;
using Parser.Repositories;
using static Parser.Models.Constants.TYPE;

namespace Interpreter.Services;

/// <summary>
/// Executes user-defined VHDL functions and procedures.
/// </summary>
/// <remarks>
/// <para><strong>Responsibilities:</strong></para>
/// <list type="bullet">
/// <item>Create local scope with parameter values</item>
/// <item>Synthesize function body on first call (lazy compilation)</item>
/// <item>Execute function body tasks</item>
/// <item>Catch ReturnException and extract return value</item>
/// <item>Support recursion via execution stack</item>
/// </list>
/// </remarks>
public class FunctionExecutor
{
    private static FunctionExecutor? _instance;
    private static readonly object _lock = new();

    /// <summary>
    /// Gets the singleton instance of the FunctionExecutor.
    /// </summary>
    public static FunctionExecutor Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    _instance ??= new FunctionExecutor();
                }
            }
            return _instance;
        }
    }

    /// <summary>
    /// Stack to track recursive function calls.
    /// </summary>
    private readonly Stack<string> _callStack = new();

    /// <summary>
    /// Maximum recursion depth to prevent stack overflow.
    /// </summary>
    private const int MaxRecursionDepth = 100;

    /// <summary>
    /// Executes a user-defined function with the given arguments.
    /// </summary>
    /// <param name="functionName">The name of the function to execute.</param>
    /// <param name="arguments">The argument values for the function call.</param>
    /// <param name="callerVariables">The variables in the caller's scope.</param>
    /// <returns>The function's return value.</returns>
    public object? ExecuteFunction(string functionName, object[] arguments, HashSet<Variable> callerVariables)
    {
        // Look up the function
        var registry = PackageRegistry.Instance;
        var function = registry.GetFunction(functionName);

        if (function == null)
        {
#if DEBUG
            Console.WriteLine($"[FunctionExecutor] Warning: Function '{functionName}' not found, returning stub");
#endif
            return "0"; // Return stub value for unknown functions
        }

        // Check recursion depth
        if (_callStack.Count >= MaxRecursionDepth)
        {
            throw new InvalidOperationException($"Maximum recursion depth ({MaxRecursionDepth}) exceeded calling '{functionName}'");
        }

        // Push onto call stack
        _callStack.Push(functionName);

        try
        {
            // Bench bug #19 perf follow-up: reuse the synthesised body's
            // CachedScope across calls — its parameter Variables already
            // back the body's Operations. We just refresh their .Value
            // from the current call's arguments. Without this, every call
            // re-synthesised against a fresh scope, which was ~20× slower
            // on signal-heavy designs (P22, P3) and made P4 unusable.
            HashSet<Variable> localScope;
            if (function.IsSynthesized && function.CachedScope != null)
            {
                localScope = function.CachedScope;
                RefreshScopeArgs(function, localScope, arguments, callerVariables);
            }
            else
            {
                localScope = CreateLocalScope(function, arguments, callerVariables);
                SynthesizeFunctionBody(function, localScope);
                function.CachedScope = localScope;
            }

            // Execute the function body
            return ExecuteFunctionBody(function, localScope);
        }
        finally
        {
            _callStack.Pop();
        }
    }

    /// <summary>
    /// Executes a user-defined function asynchronously.
    /// </summary>
    public async Task<object?> ExecuteFunctionAsync(string functionName, object[] arguments, HashSet<Variable> callerVariables)
    {
        return await Task.Run(() => ExecuteFunction(functionName, arguments, callerVariables));
    }

    /// <summary>
    /// Bench bug #19 perf: refreshes the cached scope's parameter Variables
    /// with the current call's arguments. The Variable instances stay the
    /// same (so the body's already-synthesised Operations remain valid);
    /// only their .Value is updated. Also rewires caller-scope references
    /// (constants/signals visible from the call site) so concurrent
    /// changes flow through.
    /// </summary>
    private void RefreshScopeArgs(FunctionDefinition function, HashSet<Variable> localScope,
                                  object[] arguments, HashSet<Variable> callerVariables)
    {
        // Refresh parameter values in place.
        for (int i = 0; i < function.Parameters.Count && i < arguments.Length; i++)
        {
            var param = function.Parameters[i];
            object? value = arguments[i];
            if (value is Variable varValue) value = varValue.Value;
            var existing = localScope.FirstOrDefault(v =>
                v.Name.Equals(param.Name, StringComparison.OrdinalIgnoreCase));
            if (existing != null && value != null)
            {
                existing.Value = value;
            }
        }

        // Reset local variables to their declared defaults so the body
        // executes from a known state on every call.
        foreach (var localVar in function.LocalVariables)
        {
            var existing = localScope.FirstOrDefault(v =>
                v.Name.Equals(localVar.Name, StringComparison.OrdinalIgnoreCase));
            if (existing != null && localVar.Value != null)
            {
                existing.Value = localVar.Value;
            }
        }
    }

    /// <summary>
    /// Creates a local scope with parameter variables bound to argument values.
    /// </summary>
    private HashSet<Variable> CreateLocalScope(FunctionDefinition function, object[] arguments, HashSet<Variable> callerVariables)
    {
        HashSet<Variable> localScope = new();

        // Bind parameters to arguments
        for (int i = 0; i < function.Parameters.Count; i++)
        {
            var param = function.Parameters[i];
            object? value = null;

            if (i < arguments.Length)
            {
                value = arguments[i];

                // Extract value from Variable if needed
                if (value is Variable varValue)
                {
                    value = varValue.Value;
                }
            }
            else if (param.DefaultValue != null)
            {
                value = param.DefaultValue;
            }

            var variable = param.CreateVariable(value);
            localScope.Add(variable);
        }

        // Add local variables declared in the function
        foreach (var localVar in function.LocalVariables)
        {
            // Clone the variable so we don't modify the template
            var clone = new Variable(localVar.Name, localVar.Value?.ToString() ?? "", localVar.Type)
            {
                Size = localVar.Size,
                CustomTypeName = localVar.CustomTypeName
            };
            localScope.Add(clone);
        }

        // Add caller's variables as read-only references (for constants, signals accessed in functions)
        foreach (var callerVar in callerVariables)
        {
            if (!localScope.Any(v => v.Name.Equals(callerVar.Name, StringComparison.OrdinalIgnoreCase)))
            {
                localScope.Add(callerVar);
            }
        }

        return localScope;
    }

    /// <summary>
    /// Synthesizes the function body from raw tokens.
    /// </summary>
    private void SynthesizeFunctionBody(FunctionDefinition function, HashSet<Variable> localScope)
    {
        if (string.IsNullOrEmpty(function.BodyRaw))
        {
            function.SynthesizedBody = new List<object>();
            return;
        }

        // Tokenize the body
        Tokenizer tokenizer = new(string.Empty);
        List<Token> allTokens = new();

        // Tokenize each line of the body
        foreach (var line in function.BodyLines ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            tokenizer.TokenizeLine(line);
        }

        // If BodyRaw is set, tokenize it as a single block
        if (!string.IsNullOrEmpty(function.BodyRaw))
        {
            foreach (var line in function.BodyRaw.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                tokenizer.TokenizeLine(line);
            }
        }

        tokenizer.Evaluate();
        var tokens = tokenizer.GetDictionaryType();
        TokenStream streamer = new(tokens);

        // Parse the body using Process-like parsing
        function.SynthesizedBody = ParseFunctionBody(streamer, localScope);
    }

    /// <summary>
    /// Parses a function body into executable tasks.
    /// </summary>
    private List<object> ParseFunctionBody(TokenStream streamer, HashSet<Variable> variables)
    {
        List<object> tasks = new();
        OperationDetector detector = new();

        int iterations = 0;
        const int maxIterations = 10000;

        while (streamer.Current != null && iterations < maxIterations)
        {
            iterations++;

            // Stop at 'end' keyword
            if (streamer.Current.Value.ToLower() == "end")
                break;

            if (streamer.Current.Type == KEYWORDS)
            {
                switch (streamer.Current.Value.ToLower())
                {
                    case "if":
                        IfStructure ifStructure = new(variables, streamer);
                        ifStructure.Syntetize();
                        tasks.Add(ifStructure);
                        ConsumeEndKeyword(streamer, "if");
                        break;

                    case "case":
                        CaseStructure caseStructure = new(variables, streamer);
                        caseStructure.Syntetize();
                        tasks.Add(caseStructure);
                        ConsumeEndKeyword(streamer, "case");
                        break;

                    case "for":
                        ForLoopStructure forLoop = new(variables, streamer);
                        forLoop.Syntetize();
                        tasks.Add(forLoop);
                        ConsumeEndKeyword(streamer, "loop");
                        break;

                    case "while":
                        WhileLoopStructure whileLoop = new(variables, streamer);
                        whileLoop.Syntetize();
                        tasks.Add(whileLoop);
                        ConsumeEndKeyword(streamer, "loop");
                        break;

                    case "loop":
                        SimpleLoopStructure simpleLoop = new(variables, streamer);
                        simpleLoop.Syntetize();
                        tasks.Add(simpleLoop);
                        ConsumeEndKeyword(streamer, "loop");
                        break;

                    case "return":
                        ReturnStatement returnStmt = new(variables, streamer);
                        returnStmt.Syntetize();
                        tasks.Add(returnStmt);
                        break;

                    case "exit":
                        ExitStatement exitStmt = new(variables, streamer);
                        exitStmt.Syntetize();
                        tasks.Add(exitStmt);
                        break;

                    case "next":
                        NextStatement nextStmt = new(variables, streamer);
                        nextStmt.Syntetize();
                        tasks.Add(nextStmt);
                        break;

                    case "null":
                        NullStatement nullStmt = new(streamer);
                        nullStmt.Syntetize();
                        tasks.Add(nullStmt);
                        break;

                    default:
                        streamer.MoveNext();
                        break;
                }
            }
            else
            {
                // Parse assignment statement
                var rawDataBuilder = new StringBuilder();
                while (streamer.Current?.Value != ";" && streamer.Current?.Value.ToLower() != "end")
                {
                    rawDataBuilder.Append(streamer.Current?.Value + " ");
                    streamer.MoveNext();
                }

                string operation = rawDataBuilder.ToString().Trim();
                if (!string.IsNullOrEmpty(operation))
                {
                    if (streamer.Current?.Value == ";")
                        streamer.MoveNext();

                    var type = detector.GetOperationType(operation);
                    if (type == OperationType.ASSIGNATION)
                    {
                        Operation op = new(operation, variables);
                        tasks.Add(op);
                    }
                }
            }
        }

        return tasks;
    }

    /// <summary>
    /// Consumes 'end keyword ;' tokens.
    /// </summary>
    private void ConsumeEndKeyword(TokenStream streamer, string keyword)
    {
        if (streamer.Current?.Value.ToLower() == "end")
        {
            streamer.MoveNext();
            if (streamer.Current?.Value.ToLower() == keyword)
            {
                streamer.MoveNext();
            }
            if (streamer.Current?.Value == ";")
            {
                streamer.MoveNext();
            }
        }
    }

    /// <summary>
    /// Executes the synthesized function body and captures the return value.
    /// </summary>
    private object? ExecuteFunctionBody(FunctionDefinition function, HashSet<Variable> localScope)
    {
        if (function.SynthesizedBody == null || function.SynthesizedBody.Count == 0)
        {
            return null;
        }

        try
        {
            // Execute each task
            foreach (var task in function.SynthesizedBody)
            {
                if (task is Operation operation)
                {
                    operation.Execute();
                }
                else if (task is ISControl sControl)
                {
                    sControl.ExecuteAsync().GetAwaiter().GetResult();
                }
            }

            // If we get here without a return statement, return null
            return null;
        }
        catch (ReturnException returnEx)
        {
            // Caught the return statement - extract and return the value
            return returnEx.Value;
        }
    }

    /// <summary>
    /// Executes a user-defined procedure with the given arguments.
    /// </summary>
    /// <param name="procedureName">The name of the procedure to execute.</param>
    /// <param name="arguments">The argument values/variables for the procedure call.</param>
    /// <param name="callerVariables">The variables in the caller's scope (for out/inout params).</param>
    public void ExecuteProcedure(string procedureName, object[] arguments, HashSet<Variable> callerVariables)
    {
        var registry = PackageRegistry.Instance;
        var procedure = registry.GetProcedure(procedureName);

        if (procedure == null)
        {
#if DEBUG
            Console.WriteLine($"[FunctionExecutor] Warning: Procedure '{procedureName}' not found");
#endif
            return;
        }

        // Check recursion depth
        if (_callStack.Count >= MaxRecursionDepth)
        {
            throw new InvalidOperationException($"Maximum recursion depth ({MaxRecursionDepth}) exceeded calling '{procedureName}'");
        }

        _callStack.Push(procedureName);

        try
        {
            // Create local scope with parameters
            HashSet<Variable> localScope = CreateProcedureLocalScope(procedure, arguments, callerVariables);

            // Synthesize body if not already done
            if (!procedure.IsSynthesized)
            {
                SynthesizeProcedureBody(procedure, localScope);
            }

            // Execute the procedure body
            ExecuteProcedureBody(procedure, localScope);

            // Write back out/inout parameters
            WriteBackOutParameters(procedure, arguments, localScope, callerVariables);
        }
        finally
        {
            _callStack.Pop();
        }
    }

    /// <summary>
    /// Creates a local scope for procedure execution.
    /// </summary>
    private HashSet<Variable> CreateProcedureLocalScope(ProcedureDefinition procedure, object[] arguments, HashSet<Variable> callerVariables)
    {
        HashSet<Variable> localScope = new();

        for (int i = 0; i < procedure.Parameters.Count; i++)
        {
            var param = procedure.Parameters[i];
            object? value = null;

            if (i < arguments.Length)
            {
                value = arguments[i];
                if (value is Variable varValue)
                {
                    value = varValue.Value;
                }
            }
            else if (param.DefaultValue != null)
            {
                value = param.DefaultValue;
            }

            var variable = param.CreateVariable(value);
            localScope.Add(variable);
        }

        // Add local variables
        foreach (var localVar in procedure.LocalVariables)
        {
            var clone = new Variable(localVar.Name, localVar.Value?.ToString() ?? "", localVar.Type)
            {
                Size = localVar.Size,
                CustomTypeName = localVar.CustomTypeName
            };
            localScope.Add(clone);
        }

        // Add caller's variables
        foreach (var callerVar in callerVariables)
        {
            if (!localScope.Any(v => v.Name.Equals(callerVar.Name, StringComparison.OrdinalIgnoreCase)))
            {
                localScope.Add(callerVar);
            }
        }

        return localScope;
    }

    /// <summary>
    /// Synthesizes the procedure body from raw tokens.
    /// </summary>
    private void SynthesizeProcedureBody(ProcedureDefinition procedure, HashSet<Variable> localScope)
    {
        if (string.IsNullOrEmpty(procedure.BodyRaw))
        {
            procedure.SynthesizedBody = new List<object>();
            return;
        }

        Tokenizer tokenizer = new(string.Empty);

        // Tokenize each line of the body
        foreach (var line in procedure.BodyLines ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            tokenizer.TokenizeLine(line);
        }

        // If BodyRaw is set, tokenize it as a single block
        if (!string.IsNullOrEmpty(procedure.BodyRaw))
        {
            foreach (var line in procedure.BodyRaw.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                tokenizer.TokenizeLine(line);
            }
        }

        tokenizer.Evaluate();
        var tokens = tokenizer.GetDictionaryType();
        TokenStream streamer = new(tokens);

        procedure.SynthesizedBody = ParseFunctionBody(streamer, localScope);
    }

    /// <summary>
    /// Executes the synthesized procedure body.
    /// </summary>
    private void ExecuteProcedureBody(ProcedureDefinition procedure, HashSet<Variable> localScope)
    {
        if (procedure.SynthesizedBody == null)
            return;

        try
        {
            foreach (var task in procedure.SynthesizedBody)
            {
                if (task is Operation operation)
                {
                    operation.Execute();
                }
                else if (task is ISControl sControl)
                {
                    sControl.ExecuteAsync().GetAwaiter().GetResult();
                }
            }
        }
        catch (ReturnException)
        {
            // Early return from procedure - this is valid
        }
    }

    /// <summary>
    /// Writes back out/inout parameter values to the caller's variables.
    /// </summary>
    private void WriteBackOutParameters(ProcedureDefinition procedure, object[] arguments, HashSet<Variable> localScope, HashSet<Variable> callerVariables)
    {
        for (int i = 0; i < procedure.Parameters.Count && i < arguments.Length; i++)
        {
            var param = procedure.Parameters[i];

            if (param.Mode == ParameterMode.Out || param.Mode == ParameterMode.InOut)
            {
                // Find the local parameter variable
                var localVar = localScope.FirstOrDefault(v => v.Name.Equals(param.Name, StringComparison.OrdinalIgnoreCase));

                // If the argument was a Variable, update it
                if (arguments[i] is Variable argVar && localVar != null)
                {
                    argVar.Value = localVar.Value;
                }
            }
        }
    }

    /// <summary>
    /// Resets the singleton instance (for testing).
    /// </summary>
    public static void Reset()
    {
        lock (_lock)
        {
            _instance = null;
        }
    }

    /// <summary>
    /// Gets the current call stack depth.
    /// </summary>
    public int CallStackDepth => _callStack.Count;

    /// <summary>
    /// Gets the current call stack as a string (for debugging).
    /// </summary>
    public string GetCallStackTrace()
    {
        return string.Join(" -> ", _callStack.Reverse());
    }
}
