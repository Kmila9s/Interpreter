using Interpreter.Models;
using Interpreter.Services;
using Parser.Models;
using Parser.Repositories;
using static Parser.Models.Constants.TYPE;

namespace Interpreter.Repositories;

/// <summary>Kind of body a <see cref="SequentialStatementReader"/> reads.</summary>
public enum SequentialBody
{
    /// <summary>Process statement part (no <c>return</c>).</summary>
    Process,
    /// <summary>Function or procedure body.</summary>
    Subprogram,
}

/// <summary>
/// The single reader of sequential statements (VHDL LRM §10), shared by process
/// bodies, subprogram bodies and the bodies nested in <c>if</c>, <c>case</c> and loops.
/// It owns the dispatch (if/case/for/while/loop/wait/exit/next/return/null/report/
/// assert/selected assignment/procedure call/assignment), statement labels and the
/// closing <c>end … [label];</c> of nested statements. A statement that is not valid
/// VHDL is a <see cref="ParserException"/> at the offending token; nothing is skipped.
/// </summary>
public sealed class SequentialStatementReader
{
    /// <summary>Enclosing loop labels, innermost first (null entries are unlabeled loops).</summary>
    private readonly IReadOnlyList<string?> _loops;
    private readonly SequentialBody _body;
    private readonly CancellationToken _cancellation;

    /// <summary>Token stream being read.</summary>
    public TokenStream Stream { get; }

    /// <summary>Objects visible to the statements (signals, ports, locals, loop parameters).</summary>
    public HashSet<Variable> Scope { get; }

    /// <summary>Creates a reader for a process or subprogram body.</summary>
    public SequentialStatementReader(TokenStream stream, HashSet<Variable> scope,
        SequentialBody body = SequentialBody.Process, CancellationToken cancellation = default)
        : this(stream, scope, body, cancellation, Array.Empty<string?>()) { }

    private SequentialStatementReader(TokenStream stream, HashSet<Variable> scope, SequentialBody body,
        CancellationToken cancellation, IReadOnlyList<string?> loops)
    {
        Stream = stream;
        Scope = scope;
        _body = body;
        _cancellation = cancellation;
        _loops = loops;
    }

    /// <summary>The same reader over another scope (a loop body sees its loop parameter).</summary>
    public SequentialStatementReader WithScope(HashSet<Variable> scope) =>
        new(Stream, scope, _body, _cancellation, _loops);

    /// <summary>The same reader inside a loop with the given label (null when unlabeled).</summary>
    public SequentialStatementReader InLoop(string? label) =>
        new(Stream, Scope, _body, _cancellation, [label, .. _loops]);

    /// <summary>
    /// Reads statements into <paramref name="sink"/> until a statement starts with one
    /// of <paramref name="stopWords"/> (left unconsumed).
    /// </summary>
    /// <exception cref="ParserException">A statement is malformed or the input ends first.</exception>
    public void ReadUntil(List<object> sink, params string[] stopWords) => Read(sink, false, stopWords);

    /// <summary>
    /// Reads a subprogram body (captured text) into <paramref name="sink"/>: up to its
    /// closing <c>end</c> or the end of the captured text.
    /// </summary>
    public void ReadToEnd(List<object> sink) => Read(sink, true, ["end"]);

    private void Read(List<object> sink, bool endOfInputStops, string[] stopWords)
    {
        while (true)
        {
            _cancellation.ThrowIfCancellationRequested();
            if (Stream.Current is null && endOfInputStops) return;
            var cur = Stream.Current
                ?? throw new ParserException($"Unexpected end of input — '{stopWords[^1]}' expected", Stream.Previous);
            if (cur.Type == KEYWORDS && IsStopWord(cur.Value, stopWords)) return;
            if (ParseTrace.Enabled)
                ParseTrace.Log($"Sequential tok='{cur.Value}' @{cur.Line + 1}");
            sink.Add(ReadStatement());
        }
    }

    private static bool IsStopWord(string value, string[] stopWords)
    {
        foreach (var w in stopWords)
            if (value.Equals(w, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Reads one sequential statement (with its optional label).</summary>
    private object ReadStatement()
    {
        string? label = null;
        if (Stream.Current is { Type: IDENTIFIERS } head && Stream.Peek()?.Value == ":")
        {
            label = head.Value;
            Stream.MoveNext();
            Stream.MoveNext();
        }
        var cur = Stream.Current
            ?? throw new ParserException("Unexpected end of input after a statement label", Stream.Previous);
        if (cur.Type != KEYWORDS) return ReadSimple(cur);

        switch (cur.Value.ToLowerInvariant())
        {
            case "if":
                var ifs = new IfStructure(this);
                ifs.Syntetize();
                EndClause.Consume(Stream, "if", label);
                return ifs;
            case "case":
                var cs = new CaseStructure(this);
                cs.Syntetize();
                EndClause.Consume(Stream, "case", label);
                return cs;
            case "for":
                var fl = new ForLoopStructure(this) { LoopLabel = label };
                fl.Syntetize();
                EndClause.Consume(Stream, "loop", label);
                return fl;
            case "while":
                var wl = new WhileLoopStructure(this) { LoopLabel = label };
                wl.Syntetize();
                EndClause.Consume(Stream, "loop", label);
                return wl;
            case "loop":
                var sl = new SimpleLoopStructure(this) { LoopLabel = label };
                sl.Syntetize();
                EndClause.Consume(Stream, "loop", label);
                return sl;
            case "exit":
                var exit = new ExitStatement(Scope, Stream);
                exit.Syntetize();
                CheckLoopTarget(cur, exit.Label);
                return exit;
            case "next":
                var next = new NextStatement(Scope, Stream);
                next.Syntetize();
                CheckLoopTarget(cur, next.Label);
                return next;
            case "null":
                var nul = new NullStatement(Stream);
                nul.Syntetize();
                return nul;
            case "return":
                if (_body != SequentialBody.Subprogram)
                    throw new ParserException("return statement not allowed in a process", cur);
                var ret = new ReturnStatement(Scope, Stream);
                ret.Syntetize();
                return ret;
            case "wait":
                var wait = new WaitForStatement(Stream, Scope);
                wait.Syntetize();
                return wait;
            case "report":
                var rpt = new ReportStatement(Scope, Stream);
                rpt.Syntetize();
                return rpt;
            case "assert":
                var ast = new AssertStatement(Scope, Stream);
                ast.Syntetize();
                return ast;
            case "with":
                // VHDL-2008 selected signal assignment inside a process.
                var sel = new SelectedAssignment(Scope, Stream);
                sel.Syntetize();
                return sel;
            default:
                return ReadSimple(cur); // reports the misplaced reserved word
        }
    }

    /// <summary>Reads an assignment or a procedure call.</summary>
    private object ReadSimple(Token first)
    {
        if (ProcedureCallStatement.IsProcedureCall(Stream, Scope))
        {
            var call = new ProcedureCallStatement(Scope, Stream);
            call.Syntetize();
            return call;
        }

        int line = first.SourceLine;
        var st = SimpleStatement.Read(Stream, StatementRegion.Sequential);
        if (st.IsCall) return StandardProcedures.Resolve(st, Scope);

        if (st.Operator == "<=" && st.HasTopLevelWhen)
        {
            // VHDL-2008 conditional signal assignment inside a process.
            return ConditionalAssignment.TryParse(st.Text, Scope, line)
                ?? throw new ParserException("malformed conditional signal assignment", first);
        }
        if (st.Operator == ":=" && st.HasTopLevelWhen)
            throw new ParserException("conditional variable assignments are not supported yet", first);
        return new Operation(st.Text, Scope, sourceLine: line, isAssignmentStatement: true);
    }

    /// <summary>
    /// <c>exit</c>/<c>next</c> must sit inside a loop, and a label must name an
    /// enclosing loop (GHDL: <c>exit/next must be inside a loop</c>).
    /// </summary>
    private void CheckLoopTarget(Token keyword, string? label)
    {
        if (_loops.Count == 0)
            throw new ParserException($"{keyword.Value.ToLowerInvariant()} must be inside a loop", keyword);
        if (label is not null && !_loops.Any(l => label.Equals(l, StringComparison.OrdinalIgnoreCase)))
            throw new ParserException($"\"{label}\" is not the label of an enclosing loop", keyword);
    }
}
