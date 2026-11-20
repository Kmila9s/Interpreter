using Interpreter.Services;
using Parser.Models;

namespace Interpreter.Models;

/// <summary>
/// An aggregate assignment target <c>(a, b(3 downto 0), r.lo) &lt;= value;</c>
/// (G55): the value's bits are split left to right over the element targets,
/// each written through its own <see cref="AssignmentTarget"/> in the same
/// update — the right-hand side is evaluated once, with no temporary signal.
/// </summary>
public sealed class AggregateTarget
{
    /// <summary>The element targets, leftmost first.</summary>
    public IReadOnlyList<AssignmentTarget> Elements { get; }

    private readonly IReadOnlyList<string> _texts;

    private AggregateTarget(IReadOnlyList<AssignmentTarget> elements, IReadOnlyList<string> texts) =>
        (Elements, _texts) = (elements, texts);

    /// <summary>
    /// Parses <paramref name="lhs"/> as a positional aggregate of at least two
    /// declared targets; null when it is not one (a parenthesised operand of a
    /// <c>&lt;=</c> comparison, a plain name).
    /// </summary>
    /// <exception cref="ValidationException">An element is undeclared, or the aggregate is named or nested.</exception>
    public static AggregateTarget? TryParse(string lhs, HashSet<Variable> scope, int sourceLine)
    {
        lhs = lhs.Trim();
        if (lhs.Length < 2 || lhs[0] != '(' || VhdlText.MatchingParen(lhs, 0) != lhs.Length - 1) return null;
        var parts = VhdlText.TopLevelSplit(lhs[1..^1], ",");
        if (parts.Count < 2) return null;

        var elements = new List<AssignmentTarget>(parts.Count);
        var texts = new List<string>(parts.Count);
        foreach (var raw in parts)
        {
            string part = raw.Trim();
            if (VhdlText.TopLevelSplit(part, "=>").Count > 1)
                throw new ValidationException("named aggregate assignment targets are not supported yet", sourceLine, 0, part);
            if (part.StartsWith('('))
                throw new ValidationException("nested aggregate assignment targets are not supported yet", sourceLine, 0, part);
            var element = AssignmentTarget.Resolve(part, scope)
                ?? throw new ValidationException($"no declaration for \"{part}\"", sourceLine, 0, part);
            if (element.Path.FieldError is { } fieldError)
                throw new ValidationException(fieldError, sourceLine, 0, part);
            elements.Add(element);
            texts.Add(part);
        }
        return new AggregateTarget(elements, texts);
    }

    /// <summary>
    /// Total width of the elements when every one is known before simulation; null
    /// otherwise. An aggregate target is as wide as the concatenation of its
    /// elements, so the right-hand side's width oracle answers for it too.
    /// </summary>
    public int? StaticWidth(HashSet<Variable> scope) =>
        ExpressionWidthInference.TryComputeWidth(string.Join(" & ", _texts), scope);

    /// <summary>Bit widths of the elements now (each at least 1) and their total.</summary>
    private (int[] Widths, int Total) Widths()
    {
        var widths = new int[Elements.Count];
        int total = 0;
        for (int i = 0; i < widths.Length; i++)
            total += widths[i] = Math.Max(Elements[i].Path.Resolve()?.Width ?? 1, 1);
        return (widths, total);
    }

    /// <summary>
    /// Writes <paramref name="value"/> over the elements, left to right, with the
    /// same immediate/scheduled semantics as <see cref="AssignmentTarget.Assign"/>.
    /// </summary>
    public void Assign(SignalScheduler? scheduler, object value, int sourceLine, bool immediate)
    {
        var (widths, total) = Widths();
        string bits = VectorIndexing.Fit(FieldCodec.LogicBits(value, total), total);
        for (int i = 0, at = 0; i < widths.Length; at += widths[i], i++)
            Elements[i].Assign(scheduler, bits.Substring(at, widths[i]), sourceLine, immediate);
    }

    /// <summary>Schedules the waveform <paramref name="elements"/> over the elements, left to right.</summary>
    public void Schedule(SignalScheduler scheduler, IReadOnlyList<WaveformElement> elements, int sourceLine,
        DelayMechanism mechanism, long rejectNs)
    {
        var (widths, total) = Widths();
        var bits = elements.Select(e => VectorIndexing.Fit(FieldCodec.LogicBits(e.Value, total), total)).ToArray();
        for (int i = 0, at = 0; i < widths.Length; at += widths[i], i++)
        {
            int from = at, width = widths[i];
            Elements[i].Schedule(scheduler, [.. elements.Select((e, k) => e with { Value = bits[k].Substring(from, width) })],
                sourceLine, mechanism, rejectNs);
        }
    }
}
