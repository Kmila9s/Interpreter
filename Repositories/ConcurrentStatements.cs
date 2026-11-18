using Interpreter.Models;
using Interpreter.Services;
using Parser.Models;
using Parser.Repositories;

namespace Interpreter.Repositories;

/// <summary>
/// The concurrent statements that become an equivalent process — assertion and
/// procedure call (LRM §11.4, §11.5) — shared by the architecture body and the
/// generate bodies so both read them the same way.
/// </summary>
public static class ConcurrentStatements
{
    /// <summary>
    /// Reads <c>[postponed] assert cond [report …] [severity …];</c> (stream on
    /// <c>assert</c>) as its equivalent process.
    /// </summary>
    public static Process Assert(TokenStream stream, HashSet<Variable> scope, string? label)
    {
        var assert = new AssertStatement(scope, stream);
        assert.Syntetize();
        return Process.Implicit(scope, assert, [assert.ConditionString], label);
    }

    /// <summary>
    /// Reads a concurrent procedure call (stream on its name) as its equivalent
    /// process; an unknown name is an error (<c>no declaration for "foo"</c>).
    /// </summary>
    public static Process ProcedureCall(TokenStream stream, HashSet<Variable> scope, string? label)
    {
        if (ProcedureCallStatement.IsProcedureCall(stream, scope))
        {
            var call = new ProcedureCallStatement(scope, stream);
            call.Syntetize();
            return Process.Implicit(scope, call, call.Arguments, label);
        }
        var st = SimpleStatement.Read(stream, StatementRegion.Concurrent);
        if (!st.IsCall)
            throw new ParserException($"procedure call expected after label '{label}'", st.First);
        return Process.Implicit(scope, StandardProcedures.Resolve(st, scope),
            st.CallArguments().Select(a => string.Join(' ', a.Select(t => t.Value))), label);
    }

    /// <summary>
    /// True when the statement at the current position (after any label) is a
    /// procedure call: a name, an optional argument list, then <c>;</c>.
    /// </summary>
    public static bool IsProcedureCall(TokenStream stream) => SimpleStatement.PeekAfterTarget(stream)?.Value == ";";

    /// <summary>
    /// <c>[postponed] process …</c> or a concurrent <c>[postponed] assert …;</c>
    /// (stream on <c>process</c>, <c>postponed</c> or <c>assert</c>).
    /// </summary>
    public static Process ProcessOrAssert(TokenStream stream, HashSet<Variable> scope, string? label,
        TypeRegistry? types = null, CancellationToken cancellationToken = default)
    {
        if (EndClause.Is(stream.Current, "postponed")) stream.MoveNext();
        if (EndClause.Is(stream.Current, "assert")) return Assert(stream, scope, label);
        if (!EndClause.Is(stream.Current, "process"))
            throw new ParserException("'process' or 'assert' expected after 'postponed'", stream.Current ?? stream.Previous);
        var process = new Process(scope, stream, types) { Label = label };
        process.Syntetize(cancellationToken);
        return process;
    }

    /// <summary>
    /// The task of a concurrent signal assignment <paramref name="operation"/>
    /// (<c>target &lt;= rhs</c>): a conditional assignment when it has
    /// <c>when … else</c>, otherwise an <see cref="Operation"/>.
    /// </summary>
    /// <param name="operation">The assignment text.</param>
    /// <param name="scope">Objects visible to it.</param>
    /// <param name="line">1-based source line.</param>
    public static object AssignmentTask(string operation, HashSet<Variable> scope, int line)
    {
        if (operation.Contains("when", StringComparison.OrdinalIgnoreCase)
            && ConditionalAssignment.TryParse(operation, scope, line) is { } conditional)
            return conditional;
        return new Operation(operation, scope, sourceLine: line, isAssignmentStatement: true);
    }
}
