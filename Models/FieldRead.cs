using Parser.Models;

namespace Interpreter.Models;

/// <summary>
/// A record field read in an expression — <c>p.lo</c>, <c>q.a.hi</c>,
/// <c>r.d(2)</c>, <c>m(i).lo</c> — evaluated through its <see cref="ValuePath"/>
/// and decoded with the field's type.
/// </summary>
public sealed class FieldRead(ValuePath path) : IDeferredOperand
{
    /// <summary>The field path read.</summary>
    public ValuePath Path { get; } = path;

    /// <inheritdoc/>
    public Variable Evaluate() => Path.Read();

    /// <inheritdoc/>
    public IEnumerable<Variable>? Inputs { get; } =
        path.Indices.Any(i => i.Expression != null)
            ? null
            : [path.Base, .. path.Indices.Select(i => i.Source).OfType<Variable>()];

    /// <inheritdoc/>
    public int Width => Path.Resolve()?.Width ?? 1;
}
