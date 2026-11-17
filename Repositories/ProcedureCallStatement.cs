using System.Text;
using Interpreter.Interfaces;
using Interpreter.Models;
using Interpreter.Services;
using Parser.Models;
using Parser.Repositories;
using static Parser.Models.Constants.TYPE;

namespace Interpreter.Repositories;

/// <summary>
/// Represents and executes a VHDL procedure call statement.
/// </summary>
/// <remarks>
/// <para><strong>Supported VHDL Syntax:</strong></para>
/// <code>
/// procedure_name;                     -- No arguments
/// procedure_name(arg1, arg2);         -- Positional arguments
/// procedure_name(param1 => val1);     -- Named arguments
/// </code>
/// <para><strong>Execution:</strong></para>
/// <para>
/// Evaluates arguments, then calls FunctionExecutor.ExecuteProcedure() to execute
/// the procedure body. Out/InOut parameters are written back to caller's variables.
/// </para>
/// </remarks>
public class ProcedureCallStatement : ISControl
{
    /// <summary>The variables in scope during procedure argument evaluation.</summary>
    private readonly HashSet<Variable> _variables;
    /// <summary>The token stream used for parsing the procedure call, or null if constructed explicitly.</summary>
    private readonly TokenStream? _streamer;

    /// <summary>
    /// Gets the procedure name being called.
    /// </summary>
    public string ProcedureName { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the raw argument strings.
    /// </summary>
    public List<string> Arguments { get; private set; } = new();

    /// <summary>
    /// Gets whether this is a valid procedure call.
    /// </summary>
    public bool IsValid { get; private set; } = false;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProcedureCallStatement"/> class.
    /// </summary>
    /// <param name="variables">The variables in scope.</param>
    /// <param name="streamer">The token stream for parsing.</param>
    public ProcedureCallStatement(HashSet<Variable> variables, TokenStream streamer)
    {
        _variables = variables;
        _streamer = streamer;
    }

    /// <summary>
    /// Initializes a new instance with explicit values.
    /// </summary>
    /// <param name="variables">The variables in scope.</param>
    /// <param name="procedureName">The procedure name.</param>
    /// <param name="arguments">The argument strings.</param>
    public ProcedureCallStatement(HashSet<Variable> variables, string procedureName, List<string> arguments)
    {
        _variables = variables;
        _streamer = null;
        ProcedureName = procedureName;
        Arguments = arguments;
        IsValid = true;
    }

    /// <summary>
    /// Executes the procedure call. F-G-12 MVP (2026-07-18): looks up the
    /// procedure's <see cref="FunctionDefinition"/> in <see cref="FunctionRegistry"/>,
    /// walks its <c>BodyRaw</c> tokens, and for every <c>report … ;</c>
    /// substring substitutes parameter names with the corresponding argument
    /// tokens and fires the message via <see cref="ReportBus"/>.
    /// F-G-14 (Session 23, 2026-07-18): additionally handles signal
    /// assignments (<c>&lt;=</c>) inside the body — the LHS formal is
    /// textually renamed to the caller's argument so an <see cref="Operation"/>
    /// built against the caller's variable set resolves to the caller's
    /// <see cref="Variable"/> object; the assignment is then routed through
    /// <see cref="ScheduledOperation"/> so the write enters the delta cycle
    /// and downstream sensitivity propagates. Overload receiving a
    /// <see cref="SignalScheduler"/> from the engine's context handles this
    /// path; the parameterless overload falls back to report-only for
    /// pre-F-G-14 call sites.
    /// </summary>
    public Task ExecuteAsync() => ExecuteAsync(scheduler: null);

    /// <summary>
    /// F-G-14 (Session 23, 2026-07-18): scheduler-aware execution — allows
    /// signal assignments in the procedure body to enter the delta cycle
    /// through the caller's <see cref="SignalScheduler"/>. Called from
    /// <c>DeltaCycleEngine.ExecuteControlWithSchedulingAsync</c>.
    /// </summary>
    public Task ExecuteAsync(SignalScheduler? scheduler)
    {
        if (string.IsNullOrEmpty(ProcedureName))
            return Task.CompletedTask;

        var def = Interpreter.Services.FunctionRegistry.Instance.Get(ProcedureName);
        if (def == null || string.IsNullOrEmpty(def.BodyRaw))
            return Task.CompletedTask;

        // Positional param name → arg token list.
        var subs = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < def.Parameters.Count && i < Arguments.Count; i++)
        {
            // Split each captured argument string back to tokens on space —
            // Syntetize stored them as `token + " "` concatenations.
            subs[def.Parameters[i].Name] = Arguments[i]
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        }

        var tokens = def.BodyRaw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < tokens.Length; i++)
        {
            if (tokens[i].Equals("report", StringComparison.OrdinalIgnoreCase))
            {
                i = EmitReport(tokens, i + 1, subs);
                continue;
            }

            // F-G-14: signal-assignment detection. A statement starting
            // with an identifier followed by `<=` (with possible tokens
            // between, e.g. `Q ( 0 ) <= '1'`) is a signal assignment to
            // that identifier's target. If it's a signal-class formal, we
            // substitute the caller's argument tokens for the LHS so the
            // Operation resolves against the caller's Variable object.
            if (TryHandleSignalAssignment(def, tokens, i, subs, scheduler, out int nextI))
            {
                i = nextI;
                continue;
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Phase 3 (Session 26, 2026-07-22): produces the per-call bound body
    /// used by the delta-cycle engine's <c>RunTopProcedureFromEntryAsync</c>.
    /// Textually substitutes formal identifiers with actual identifiers in
    /// <c>def.BodyRaw</c> and re-parses through
    /// <see cref="FunctionExecutor.ParseFunctionBodyStatic"/> against a
    /// caller-facing scope. Result: every <c>Operation</c> in the returned
    /// task list binds to caller-side <c>Variable</c>s, so signal writes
    /// route through the caller's scheduler and <c>wait until</c>
    /// sensitivity sets capture actual signal identity — no per-execute
    /// remap needed at engine level.
    /// </summary>
    /// <remarks>
    /// <para>Cache: (procedure name, joined-actual-name-signature) keyed
    /// on the <c>FunctionDefinition</c> so amortized cost is one parse
    /// per unique call-site actual pattern.</para>
    /// <para>Returns an empty list if the procedure is not in
    /// <see cref="FunctionRegistry"/> — callers should fall back to the
    /// legacy <see cref="ExecuteAsync(SignalScheduler?)"/> for
    /// PackageRegistry-only procedures.</para>
    /// </remarks>
    public List<object> ResolveBoundBody(
        SignalScheduler? scheduler,
        out HashSet<Variable> boundScope)
    {
        boundScope = new HashSet<Variable>(_variables);

        var def = Interpreter.Services.FunctionRegistry.Instance.Get(ProcedureName);
        if (def == null || string.IsNullOrEmpty(def.BodyRaw))
            return new List<object>();

        // Build formal→actual textual rename map + cache-key signature.
        // Values in the map can be multi-token strings (space-separated),
        // matching how BodyRaw is stored (space-tokenized).
        var renameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var actualSig = new List<string>(def.Parameters.Count);

        for (int i = 0; i < def.Parameters.Count && i < Arguments.Count; i++)
        {
            var formal = def.Parameters[i];
            var argRaw = Arguments[i].Trim();
            var argTokens = argRaw.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            Variable? actualVar = null;
            if (argTokens.Length == 1 && IsIdentifierStart(argTokens[0]))
            {
                actualVar = _variables.FirstOrDefault(v =>
                    v.Name.Equals(argTokens[0], StringComparison.OrdinalIgnoreCase));
            }

            // 2026-07-23 — use `is not null` (not `!= null`): `Variable`
            // overloads `operator ==`/`!=` to compare Names, and the
            // compiler picks that overload over the reference check.
            // A null Variable therefore falls into the `!= null` branch,
            // NREs on `actualVar.Name`. See memory
            // `project-constant-array-fix` ("the trap that ate hours").
            if (actualVar is not null)
            {
                renameMap[formal.Name] = actualVar.Name;
                actualSig.Add(actualVar.Name.ToLowerInvariant());
            }
            else
            {
                // Literal / expression / multi-token actual — textual splice.
                // Cache-key entry uses the raw arg to avoid false hits between
                // callsites passing different literals.
                renameMap[formal.Name] = string.Join(' ', argTokens);
                actualSig.Add("$lit:" + argRaw.ToLowerInvariant());
            }
        }

        // Clone locals into bound scope (share ref — locals are declared
        // per-procedure not per-call, and Phase 3 accepts the recursion
        // limitation of shared locals for now; testbench-shape procedures
        // don't recurse).
        foreach (var lv in def.LocalVariables)
            boundScope.Add(lv);

        // Cache probe.
        string signature = string.Join('|', actualSig);
        var key = (def.Name.ToLowerInvariant(), signature);
        def.BoundBodyCache ??= new Dictionary<(string, string), List<object>>();
        if (def.BoundBodyCache.TryGetValue(key, out var cached))
            return cached;

        // Textual formal→actual substitution over BodyRaw (skip inside
        // string literals — quote-parity toggle, same pattern as EmitReport).
        string substituted = SubstituteFormalsInBody(def.BodyRaw, renameMap);

        // Re-tokenize + reparse via the shared static entry so the
        // wait/report/assert/nested-call dispatch stays in sync between
        // registration-time synth (SynthesizeProcedureBody) and this
        // call-time resolve.
        var tokenizer = new Parser.Repositories.Tokenizer();
        foreach (var line in substituted.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            tokenizer.TokenizeLine(line);
        }
        tokenizer.Evaluate();
        var tokens = tokenizer.GetDictionaryType();
        var streamer = new Parser.Repositories.TokenStream(tokens);

        var tasks = Interpreter.Services.FunctionExecutor
            .ParseFunctionBodyStatic(streamer, boundScope);

        def.BoundBodyCache[key] = tasks;
        return tasks;
    }

    /// <summary>
    /// Tier 4.3 pass 8 (2026-07-28): runtime-scope-aware bound-body resolve
    /// for procedures called from inside a function body. Unlike
    /// <see cref="ResolveBoundBody"/>, this overload does NOT touch the
    /// parse-time <c>_variables</c> — actuals are looked up in the
    /// caller-provided <paramref name="runtimeScope"/> (typically the
    /// function's <c>localScope</c> from <c>FunctionExecutor.CreateLocalScope</c>)
    /// so the returned Operations write back into the fresh per-call
    /// Variable instances. Bypasses <c>BoundBodyCache</c> — since function
    /// locals get new identities on every invocation, a cache hit is
    /// impossible anyway; the re-parse cost is amortized against one
    /// procedure call per function invocation.
    /// </summary>
    /// <remarks>
    /// The rename map key is the formal name; the value is the raw actual
    /// argument text (multi-token OK), spliced into the body via the same
    /// <see cref="SubstituteFormalsInBody"/> helper the process-side path
    /// uses. Procedure locals declared on the <see cref="FunctionDefinition"/>
    /// are cloned into the parse scope so the procedure body has its own
    /// per-call state (no cross-call leakage).
    /// </remarks>
    public List<object> ResolveBoundBodyAgainstScope(HashSet<Variable> runtimeScope)
    {
        var def = Interpreter.Services.FunctionRegistry.Instance.Get(ProcedureName);
        if (def == null || string.IsNullOrEmpty(def.BodyRaw))
            return new List<object>();

        var renameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < def.Parameters.Count && i < Arguments.Count; i++)
        {
            var formal = def.Parameters[i];
            var argRaw = Arguments[i].Trim();
            if (!string.IsNullOrEmpty(argRaw))
                renameMap[formal.Name] = argRaw;
        }

        var parseScope = new HashSet<Variable>(runtimeScope);
        foreach (var lv in def.LocalVariables)
        {
            if (!parseScope.Any(v => v.Name.Equals(lv.Name, StringComparison.OrdinalIgnoreCase)))
            {
                parseScope.Add(new Variable(lv.Name, lv.Value?.ToString() ?? string.Empty, lv.Type)
                {
                    Size = lv.Size,
                    CustomTypeName = lv.CustomTypeName,
                    PortType = Parser.Models.Constants.PortType.OUT
                });
            }
        }

        string substituted = SubstituteFormalsInBody(def.BodyRaw, renameMap);

        var tokenizer = new Parser.Repositories.Tokenizer();
        foreach (var line in substituted.Split('\n'))
        {
            if (!string.IsNullOrWhiteSpace(line))
                tokenizer.TokenizeLine(line);
        }
        tokenizer.Evaluate();
        var tokens = tokenizer.GetDictionaryType();
        var streamer = new Parser.Repositories.TokenStream(tokens);

        return Interpreter.Services.FunctionExecutor
            .ParseFunctionBodyStatic(streamer, parseScope);
    }

    /// <summary>
    /// Phase 3: whitespace-tokenized substitution over the procedure's
    /// <c>BodyRaw</c>. Skips tokens inside string literals (quote-parity
    /// toggle mirrors <see cref="EmitReport"/>).
    /// </summary>
    private static string SubstituteFormalsInBody(string bodyRaw, Dictionary<string, string> renameMap)
    {
        if (renameMap.Count == 0) return bodyRaw;

        var sb = new StringBuilder(bodyRaw.Length + 32);
        var lines = bodyRaw.Split('\n');
        for (int li = 0; li < lines.Length; li++)
        {
            var line = lines[li];
            var tokens = line.Split(' ');
            bool insideString = false;
            for (int i = 0; i < tokens.Length; i++)
            {
                var t = tokens[i];
                int qc = 0;
                foreach (var c in t) if (c == '"') qc++;
                bool wasInside = insideString;
                if (qc % 2 == 1) insideString = !insideString;

                if (!wasInside && renameMap.TryGetValue(t, out var repl))
                    sb.Append(repl);
                else
                    sb.Append(t);

                if (i < tokens.Length - 1) sb.Append(' ');
            }
            if (li < lines.Length - 1) sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// F-G-14: renders one <c>report … ;</c> starting at token index <paramref name="start"/>
    /// and returns the index of the terminating <c>;</c> (the outer loop's
    /// post-increment moves past it).
    /// </summary>
    private int EmitReport(string[] tokens, int start, Dictionary<string, string[]> subs)
    {
        int i = start;
        var expr = new List<string>();
        bool insideString = false;
        while (i < tokens.Length)
        {
            var t = tokens[i];
            int qc = 0;
            foreach (var c in t) if (c == '"') qc++;
            if (qc % 2 == 1) insideString = !insideString;

            if (!insideString && t == ";") break;
            if (!insideString && t.Equals("severity", StringComparison.OrdinalIgnoreCase))
            {
                i++;
                if (i < tokens.Length && tokens[i] != ";") i++;
                continue;
            }
            if (!insideString && t == "&") { i++; continue; }

            if (!insideString && subs.TryGetValue(t, out var argTokens))
                expr.AddRange(argTokens);
            else
                expr.Add(t);
            i++;
        }

        var rendered = Interpreter.Services.ParseTrace.RenderReportTokens(expr, _variables);
        if (!string.IsNullOrEmpty(rendered))
            Interpreter.Services.ReportBus.Instance.Emit(rendered);
        return i;
    }

    /// <summary>
    /// F-G-14: if the token stream at <paramref name="start"/> parses as a
    /// signal assignment to one of the procedure's formals, execute it
    /// against the caller's Variable set (via textual formal→actual
    /// substitution) and return the index of the terminating <c>;</c>.
    /// Otherwise returns false and the outer walker moves on.
    /// </summary>
    private bool TryHandleSignalAssignment(
        FunctionDefinition def,
        string[] tokens,
        int start,
        Dictionary<string, string[]> subs,
        SignalScheduler? scheduler,
        out int nextI)
    {
        nextI = start;

        // Statement must start with an identifier we recognise as either
        // a formal parameter, a local declaration, or a caller-scope name.
        if (start >= tokens.Length) return false;
        string first = tokens[start];
        if (!IsIdentifierStart(first)) return false;

        // Find the assignment operator or the terminator.
        int semi = -1;
        int assignAt = -1;
        bool isSignal = false;
        for (int j = start; j < tokens.Length; j++)
        {
            if (tokens[j] == ";") { semi = j; break; }
            if (tokens[j] == "<=") { assignAt = j; isSignal = true; }
            else if (tokens[j] == ":=") { assignAt = j; isSignal = false; }
        }
        if (assignAt < 0 || semi < 0 || semi <= assignAt) return false;

        // Textual formal→actual substitution over the whole statement span.
        var subbedTokens = new List<string>();
        for (int j = start; j < semi; j++)
        {
            var t = tokens[j];
            if (subs.TryGetValue(t, out var argTokens))
                subbedTokens.AddRange(argTokens);
            else
                subbedTokens.Add(t);
        }
        string rawStmt = string.Join(" ", subbedTokens);

        // Only accept signal assignments — variable := writes to procedure
        // locals stay swallowed like before (their side effects don't reach
        // the caller in the current MVP).
        if (!isSignal) return false;

        try
        {
            // Build the operation against the CALLER's variable set. If the
            // substitution mapped the LHS to a caller signal name, the
            // Operation's Result field will point at the caller's Variable
            // object and ScheduledOperation.Execute will schedule the write
            // through the delta cycle.
            var op = new Operation(rawStmt, _variables);
            if (scheduler != null && !ReferenceEquals(op.Result, null) && !string.IsNullOrEmpty(op.Result.Name))
            {
                var scheduled = new ScheduledOperation(op, scheduler);
                scheduled.Execute();
            }
            else
            {
                // No scheduler available (fallback path) — execute
                // immediately so the caller at least sees the effect.
                op.Execute();
            }
        }
        catch
        {
            // Malformed assignment — swallow to keep the run alive; the
            // pre-F-G-14 behaviour also silently ignored the statement.
        }

        nextI = semi;
        return true;
    }

    private static bool IsIdentifierStart(string t) =>
        !string.IsNullOrEmpty(t) && (char.IsLetter(t[0]) || t[0] == '_');

    /// <summary>
    /// Synthesizes the procedure call from the token stream.
    /// </summary>
    public void Syntetize()
    {
        if (_streamer == null) return;

        // Get procedure name
        if (_streamer.Current?.Type == IDENTIFIERS)
        {
            ProcedureName = _streamer.Current.Value;
            _streamer.MoveNext();
        }
        else
        {
            IsValid = false;
            return;
        }

        // Check for arguments in parentheses
        if (_streamer.Current?.Value == "(")
        {
            _streamer.MoveNext(); // consume '('

            var currentArg = new StringBuilder();
            int parenDepth = 1;

            while (_streamer.Current != null && parenDepth > 0)
            {
                string token = _streamer.Current.Value;

                if (token == "(")
                {
                    parenDepth++;
                    currentArg.Append(token);
                }
                else if (token == ")")
                {
                    parenDepth--;
                    if (parenDepth > 0)
                    {
                        currentArg.Append(token);
                    }
                }
                else if (token == "," && parenDepth == 1)
                {
                    // End of current argument
                    string arg = currentArg.ToString().Trim();
                    if (!string.IsNullOrEmpty(arg))
                    {
                        // Handle named association (param => value)
                        if (arg.Contains("=>"))
                        {
                            var parts = arg.Split("=>");
                            if (parts.Length == 2)
                            {
                                arg = parts[1].Trim();
                            }
                        }
                        Arguments.Add(arg);
                    }
                    currentArg.Clear();
                }
                else
                {
                    currentArg.Append(token + " ");
                }

                _streamer.MoveNext();
            }

            // Add last argument
            string lastArg = currentArg.ToString().Trim();
            if (!string.IsNullOrEmpty(lastArg))
            {
                if (lastArg.Contains("=>"))
                {
                    var parts = lastArg.Split("=>");
                    if (parts.Length == 2)
                    {
                        lastArg = parts[1].Trim();
                    }
                }
                Arguments.Add(lastArg);
            }
        }

        // Consume ';'
        if (_streamer.Current?.Value == ";")
        {
            _streamer.MoveNext();
        }

        // Validate — F-G-12 (2026-07-18): check FunctionRegistry (where
        // SkipProcedureDeclaration registers) in addition to PackageRegistry.
        var fnDef = Interpreter.Services.FunctionRegistry.Instance.Get(ProcedureName);
        var pkgRegistry = PackageRegistry.Instance;
        IsValid = (fnDef != null && !fnDef.IsPure) || pkgRegistry.ContainsProcedure(ProcedureName);

#if DEBUG
        if (!IsValid)
        {
            Console.WriteLine($"[ProcedureCallStatement] Warning: Procedure '{ProcedureName}' not found in registry");
        }
#endif
    }


    /// <summary>
    /// Checks if a statement is a procedure call (identifier followed by '(' or ';').
    /// </summary>
    /// <param name="streamer">The token stream.</param>
    /// <param name="variables">The variables in scope.</param>
    /// <returns>True if this looks like a procedure call.</returns>
    public static bool IsProcedureCall(TokenStream streamer, HashSet<Variable> variables)
    {
        if (streamer.Current?.Type != IDENTIFIERS)
            return false;

        string name = streamer.Current.Value;

        // F-G-12 (2026-07-18): check FunctionRegistry too — that's where
        // SkipProcedureDeclaration registers, and where our body / parameters
        // live for the inlining path used by ExecuteAsync.
        var fnDef = Interpreter.Services.FunctionRegistry.Instance.Get(name);
        if (fnDef != null && !fnDef.IsPure) return true;

        // Legacy path: PackageRegistry's procedure catalogue.
        var registry = PackageRegistry.Instance;
        if (registry.ContainsProcedure(name))
            return true;

        return false; // Conservative — require procedure registration.
    }
}
