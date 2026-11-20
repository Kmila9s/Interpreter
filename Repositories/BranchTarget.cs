using Interpreter.Models;
using Interpreter.Services;
using Parser.Models;
using Parser.Repositories;

namespace Interpreter.Repositories;

/// <summary>
/// The single target of a multi-branch assignment (<c>with … select</c>,
/// <c>when … else</c>) and the assignment each branch performs
/// (<c>target &lt;= value</c>). Shared by <see cref="SelectedAssignment"/> and
/// <see cref="ConditionalAssignment"/>, which differ only in how they pick
/// the branch.
/// </summary>
internal sealed class BranchTarget<TBranch> where TBranch : notnull
{
    private readonly HashSet<Variable> _scope;
    private readonly Dictionary<TBranch, Operation> _assignments = new();

    public BranchTarget(HashSet<Variable> scope) => _scope = scope;

    /// <summary>The target as written, with any index or range (<c>y</c>, <c>y(0)</c>, <c>v(3 downto 2)</c>).</summary>
    public string Text { get; private set; } = string.Empty;

    /// <summary>The target resolved; null when undeclared.</summary>
    public AssignmentTarget? Destination { get; private set; }

    /// <summary>The aggregate target <c>(a, b)</c> of declared objects (G55); null for a name target.</summary>
    public AggregateTarget? Aggregate { get; private set; }

    /// <summary>True for an aggregate target.</summary>
    public bool IsAggregate => Aggregate is not null;

    /// <summary>True when the target is a declared object or an aggregate of them.</summary>
    public bool IsDeclared => Destination is not null || IsAggregate;

    /// <summary>The delay mechanism written after <c>&lt;=</c> (with the first branch), applied to every branch.</summary>
    private string _mechanism = string.Empty;

    /// <summary><c>&lt;=</c> for a signal assignment, <c>:=</c> for a VHDL-2008 conditional/selected variable assignment (G77).</summary>
    public string Operator { get; set; } = "<=";

    /// <summary>
    /// <c>target &lt;= value</c> for one branch. The delay mechanism
    /// (<c>transport</c>, <c>reject t inertial</c>) is written once, before the
    /// first branch's value, and applies to all of them.
    /// </summary>
    private string AssignmentText(string value)
    {
        if (Operator == ":=") return $"{Text} := {value}";
        string prefix = Waveform.MechanismPrefix(value);
        if (prefix.Length > 0) _mechanism = prefix;
        else value = _mechanism + value;
        return $"{Text} <= {value}";
    }

    /// <summary>Records and resolves the target.</summary>
    public void Set(string text)
    {
        Text = text.Trim();
        Destination = AssignmentTarget.Resolve(Text, _scope);
        Aggregate = Destination is null ? AggregateTarget.TryParse(Text, _scope, 0) : null;
    }

    /// <summary>
    /// Reads the target from <paramref name="streamer"/>: a name, with its
    /// index or range when present (kept so only that part is written and
    /// sibling per-bit statements merge instead of clobbering each other).
    /// </summary>
    public void Read(TokenStream streamer)
    {
        if (streamer.Current?.Value == "(")
        {
            Set($"({ParseTrace.ReadParenthesized(streamer)})");   // aggregate target (G55)
            return;
        }
        if (streamer.Current?.Type != Constants.TYPE.IDENTIFIERS) return;
        string name = streamer.Current.Value;
        streamer.MoveNext();
        Set(streamer.Current?.Value == "(" ? $"{name}({ParseTrace.ReadParenthesized(streamer)})" : name);
    }

    /// <summary>
    /// Builds <paramref name="branch"/>'s assignment with the same input-port
    /// (C8), part-target and type/width (C4/C5) validation as a plain
    /// assignment, and keeps it for execution. Only a <see cref="ParserException"/>
    /// (the intended rejection) escapes; any other ctor failure on an
    /// unusual-but-valid value is swallowed and the assignment is built on
    /// first use instead.
    /// </summary>
    public void Validate(TBranch branch, string value, int sourceLine)
    {
        if (Text.Length == 0 || string.IsNullOrWhiteSpace(value)) return;
        try
        {
            _assignments[branch] = new Operation(AssignmentText(value), _scope, sourceLine, isAssignmentStatement: true);
        }
        catch (ParserException) { throw; }
        catch { /* never destabilise a valid design on an unexpected ctor failure */ }
    }

    /// <summary>
    /// Performs <paramref name="branch"/>'s assignment through the same path as
    /// a plain assignment: whole or part target, <c>after</c> delay, scheduled
    /// when a <paramref name="scheduler"/> is given, immediate otherwise.
    /// </summary>
    public void Assign(TBranch branch, string value, SignalScheduler? scheduler)
    {
        if (!_assignments.TryGetValue(branch, out var op))
            _assignments[branch] = op = new Operation(AssignmentText(value), _scope);
        if (scheduler != null) ScheduledOperation.Run(op, scheduler);
        else op.Execute();
    }
}
