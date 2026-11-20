using Parser.Models;

namespace Interpreter.Models;

/// <summary>
/// One index of a vector/array access whose value may only be known at run
/// time: an integer literal, a variable (e.g. a loop iterator) or an
/// expression over live signals (e.g. <c>to_integer(unsigned(addr))</c>).
/// Shared by indexed reads (<see cref="DynamicIndex"/>) and indexed writes
/// (<see cref="AssignmentTarget"/>), so both evaluate an index the same way.
/// </summary>
public sealed class IndexValue
{
    private readonly int? _literal;
    private Services.Operation? _cachedOperation;

    /// <summary>The variable holding the index, in variable mode.</summary>
    public Variable? Source { get; }

    /// <summary>The index expression text, in expression mode.</summary>
    public string? Expression { get; }

    /// <summary>Variables the expression may reference, in expression mode.</summary>
    public HashSet<Variable>? Scope { get; }

    private IndexValue(int? literal, Variable? source, string? expression, HashSet<Variable>? scope)
    {
        _literal = literal;
        Source = source;
        Expression = expression;
        Scope = scope;
    }

    /// <summary>A fixed index.</summary>
    public static IndexValue Literal(int value) => new(value, null, null, null);

    /// <summary>The current value of <paramref name="source"/>.</summary>
    public static IndexValue Of(Variable source) => new(null, source, null, null);

    /// <summary>The value of <paramref name="expression"/> evaluated against <paramref name="scope"/>.</summary>
    public static IndexValue Of(string expression, HashSet<Variable> scope) => new(null, null, expression, scope);

    /// <summary>
    /// Reads index text: an integer literal, a name declared in
    /// <paramref name="scope"/>, or any other expression over it.
    /// </summary>
    public static IndexValue Parse(string text, HashSet<Variable> scope)
    {
        text = text.Trim();
        if (int.TryParse(text, out int literal)) return Literal(literal);
        var named = scope.FirstOrDefault(v => v is not null && string.Equals(v.Name, text, StringComparison.OrdinalIgnoreCase));
        return named is not null ? Of(named) : Of(text, scope);
    }

    /// <summary>The literal value, when the index is fixed.</summary>
    public int? StaticValue => _literal;

    /// <summary>The index value now, or -1 when it cannot be resolved (e.g. an uninitialised signal).</summary>
    public int Evaluate()
    {
        if (_literal is int fixedIndex) return fixedIndex;
        if (Source is not null)
        {
            // A variable index (loop iterator, integer signal) is decimal.
            return Source.Value switch
            {
                int i => i,
                long l => (int)l,
                var other => int.TryParse(other?.ToString(), out int parsed) ? parsed : -1,
            };
        }
        try
        {
            // Parse once; Evaluate re-reads its operands' live values.
            _cachedOperation ??= new Services.Operation(Expression!, Scope!);
            return ToInt(_cachedOperation.EvaluateResult());
        }
        catch (Exception e) when (e is not SimulationRuntimeException)
        {
            return -1;
        }
    }

    /// <summary>Integer value of an expression result (a number; a vector read unsigned), or -1.</summary>
    private static int ToInt(Variable result) =>
        result.Value is null ? -1
        : Services.Operator.TryNumber(result, out var n) ? (int)n
        : -1;
}
