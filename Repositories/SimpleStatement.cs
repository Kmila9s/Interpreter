using System.Text;
using Interpreter.Services;
using Parser.Models;
using Parser.Repositories;
using static Parser.Models.Constants.TYPE;

namespace Interpreter.Repositories;

/// <summary>Statement region a <see cref="SimpleStatement"/> is read in.</summary>
public enum StatementRegion
{
    /// <summary>Architecture / block / generate body (concurrent statements).</summary>
    Concurrent,
    /// <summary>Process or subprogram body (sequential statements).</summary>
    Sequential,
}

/// <summary>
/// The single reader and shape validator of a simple statement — an assignment
/// <c>target ( &lt;= | := ) rhs ;</c> or a procedure call <c>name [ ( args ) ] ;</c> —
/// shared by the concurrent region (<see cref="Architecture"/>) and every
/// sequential body (<see cref="SequentialStatementReader"/>). Anything that does not
/// have that shape (<c>by one;</c>, <c>count + 1;</c>, a reserved word as a statement,
/// a statement that runs past a missing <c>;</c>) is a <see cref="ParserException"/>
/// at the offending token, the way GHDL reports it — never a silently dropped line.
/// </summary>
/// <param name="First">First token of the statement (its line is the statement's line).</param>
/// <param name="Target">Target text (with any index/slice/field selectors), or the procedure name and arguments of a call.</param>
/// <param name="Operator"><c>&lt;=</c> or <c>:=</c>; null for a procedure call.</param>
/// <param name="Rhs">Right-hand side text (empty for a call).</param>
/// <param name="Guarded">True for <c>target &lt;= guarded …</c>.</param>
/// <param name="TargetTokens">Tokens of <paramref name="Target"/> (a call's name and argument list).</param>
public sealed record SimpleStatement(Token First, string Target, string? Operator, string Rhs, bool Guarded,
    IReadOnlyList<Token> TargetTokens)
{
    /// <summary>True when the right-hand side has a <c>when</c> outside parentheses (conditional assignment).</summary>
    public bool HasTopLevelWhen { get; init; }

    /// <summary>True for a procedure call statement.</summary>
    public bool IsCall => Operator is null;

    /// <summary>The statement as one text (<c>target op rhs</c>), the form <see cref="Operation"/> parses.</summary>
    public string Text => IsCall ? Target : $"{Target} {Operator} {Rhs}".Trim();

    /// <summary>Name of the called procedure (first word of <see cref="Target"/>) for a call.</summary>
    public string CallName => First.Value;

    /// <summary>
    /// Reserved words that end an expression: one of them inside a right-hand side
    /// at the top level means the <c>;</c> of the statement is missing.
    /// </summary>
    private static readonly HashSet<string> StatementWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "end", "begin", "if", "elsif", "then", "case", "is", "loop", "for", "while",
        "process", "wait", "report", "assert", "exit", "next", "return", "with", "select",
        "generate", "port", "map", "variable", "signal", "constant", "procedure", "function",
        "entity", "architecture", "component", "block", "until", "severity", "postponed",
    };

    /// <summary>
    /// Reads one simple statement at the current position, including its <c>;</c>.
    /// </summary>
    /// <param name="stream">Stream positioned on the statement's first token.</param>
    /// <param name="region">Concurrent (no <c>:=</c>) or sequential.</param>
    /// <exception cref="ParserException">The statement is malformed.</exception>
    public static SimpleStatement Read(TokenStream stream, StatementRegion region)
    {
        var first = stream.Current
            ?? throw new ParserException("Unexpected end of input where a statement was expected", stream.Previous);
        var target = new List<Token>();

        if (first.Value == "(")
        {
            // Aggregate target `(a, b) <= …` (G55); its elements are resolved by Operation.
            AppendBalanced(stream, target);
        }
        else if (first.Type == IDENTIFIERS || first.Type == QUALIFIED_IDENTIFIERS)
        {
            ReadNameTarget(stream, target);
        }
        else if (first.Type == KEYWORDS)
        {
            throw new ParserException(region == StatementRegion.Concurrent
                ? $"unexpected token '{first.Value}' in a concurrent statement list"
                : $"'{first.Value}' cannot start a sequential statement", first);
        }
        else
        {
            throw new ParserException(region == StatementRegion.Concurrent
                ? $"Unexpected token '{first.Value}' — expected a signal name to start a concurrent statement"
                : $"Unexpected token '{first.Value}' — expected a name to start a statement", first);
        }

        var sep = stream.Current
            ?? throw new ParserException(
                $"Statement starting with '{first.Value}' is missing its assignment operator or terminator", stream.Previous);
        bool isCall = sep.Type == OPERATORS && sep.Value == ";";
        bool isAssignment = sep.Type == OPERATORS && (sep.Value == "<=" || sep.Value == ":=");
        if (!isCall && !isAssignment)
            throw new ParserException(
                $"Unexpected token '{sep.Value}' after '{first.Value}' — expected '<=', ':=', or ';'", sep);
        if (region == StatementRegion.Concurrent && sep.Value == ":=")
            throw new ParserException(
                $"':=' cannot assign to signal '{first.Value}' in a concurrent statement; use '<='", sep);
        stream.MoveNext();
        string targetText = string.Join(' ', target.Select(t => t.Value));
        if (isCall) return new SimpleStatement(first, targetText, null, string.Empty, false, target);

        bool guarded = false;
        if (EndClause.Is(stream.Current, "guarded"))
        {
            guarded = true;
            stream.MoveNext();
        }
        var (rhs, topLevelWhen) = ReadRhs(stream, first, sep.Value);
        stream.MoveNext(); // ;
        return new SimpleStatement(first, targetText, sep.Value, rhs, guarded, target) { HasTopLevelWhen = topLevelWhen };
    }

    /// <summary>A name with its selectors: <c>bus(7 downto 0)</c>, <c>mem(i)(j)</c>, <c>m(i).lo</c>.</summary>
    private static void ReadNameTarget(TokenStream stream, List<Token> into)
    {
        into.Add(stream.Current!);
        stream.MoveNext();
        while (stream.Current?.Value == "(" || stream.Current?.IsFieldSelector == true)
        {
            if (stream.Current.Value == "(") AppendBalanced(stream, into);
            else { into.Add(stream.Current); stream.MoveNext(); }
        }
    }

    /// <summary>
    /// Looks ahead (without consuming) past a name and its selectors and returns the
    /// token that follows — <c>;</c> for a procedure call, <c>&lt;=</c> for a signal
    /// assignment — or null when the position does not start with a name.
    /// </summary>
    public static Token? PeekAfterTarget(TokenStream stream)
    {
        if (stream.Current is not { Type: IDENTIFIERS or QUALIFIED_IDENTIFIERS }) return null;
        stream.SetRollBack();
        try { ReadNameTarget(stream, []); return stream.Current; }
        catch (ParserException) { return null; }
        finally { stream.RollBack(); }
    }

    /// <summary>
    /// Reads the right-hand side up to (not including) its <c>;</c>; also tells whether
    /// it has a top-level <c>when</c> (a conditional assignment).
    /// </summary>
    private static (string Rhs, bool TopLevelWhen) ReadRhs(TokenStream stream, Token first, string op)
    {
        var rhs = new StringBuilder();
        int depth = 0;
        // True while inside a when…else CONDITION (between a depth-0 `when` and the
        // following depth-0 `else`/`;`), where a `<=` is a relational comparison.
        bool inCondition = false, topLevelWhen = false;
        var history = new List<Token>();
        while (stream.Current?.Value != ";")
        {
            var cur = stream.Current
                ?? throw new ParserException(
                    $"Unexpected end of input in the assignment to '{first.Value}' — missing ';'?", stream.Previous);
            // Stray UNKNOWN tokens (`$`, pasted mojibake) are never valid VHDL.
            if (cur.Type == UNKNOWN && !Token.IsQualifierMarkText(cur.Value))
                throw new ParserException(
                    $"Unexpected token '{cur.Value}' inside the statement — not a valid VHDL identifier, keyword, operator, or literal", cur);
            if (depth == 0 && cur.Type == KEYWORDS && StatementWords.Contains(cur.Value))
                throw new ParserException(
                    $"';' expected to terminate the assignment to '{first.Value}' (found '{cur.Value}')", cur);

            if (cur.Value == "(") depth++;
            else if (cur.Value == ")") depth--;
            else if (depth == 0 && cur.Value.Equals("when", StringComparison.OrdinalIgnoreCase)) inCondition = topLevelWhen = true;
            else if (depth == 0 && cur.Value.Equals("else", StringComparison.OrdinalIgnoreCase)) inCondition = false;
            // C11: a stray assignment operator at the top level means the RHS ran
            // past a missing ';' into the next statement. In a variable assignment
            // a `<=` is always the relational operator, so only `:=` counts there.
            else if ((op == "<=" || cur.Value == ":=")
                     && ConcurrentAssignmentTermination.IsStrayAssignmentOperator(cur, depth, inCondition, history))
                throw new ValidationException(
                    $"';' expected to terminate the assignment to '{first.Value}' (found '{cur.Value}' — missing ';'?)", cur);

            rhs.Append(cur.Value).Append(' ');
            history.Add(cur);
            stream.MoveNext();
        }
        return (rhs.ToString().Trim(), topLevelWhen);
    }

    /// <summary>Appends a balanced parenthesised group (stream on its <c>(</c>).</summary>
    private static void AppendBalanced(TokenStream stream, List<Token> into)
    {
        int depth = 0;
        do
        {
            var cur = stream.Current
                ?? throw new ParserException("Unterminated parenthesized expression", stream.Previous);
            if (cur.Value == "(") depth++;
            else if (cur.Value == ")") depth--;
            into.Add(cur);
            stream.MoveNext();
        } while (depth > 0);
    }

    /// <summary>
    /// The arguments of a call (<c>name ( a , f ( b , c ) )</c>): token lists split on
    /// the top-level commas of the outer parentheses; empty for <c>name;</c>.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<Token>> CallArguments()
    {
        var args = new List<IReadOnlyList<Token>>();
        if (TargetTokens.Count < 3 || TargetTokens[1].Value != "(") return args;
        var current = new List<Token>();
        int depth = 0;
        for (int i = 2; i < TargetTokens.Count - 1; i++)
        {
            var t = TargetTokens[i];
            if (t.Value == "(") depth++;
            else if (t.Value == ")") depth--;
            else if (depth == 0 && t.Value == ",") { args.Add(current); current = []; continue; }
            current.Add(t);
        }
        args.Add(current);
        return args;
    }
}
