using System.Text.RegularExpressions;
using Interpreter.Services;
using Parser.Models;

namespace Interpreter.Models;

/// <summary>
/// The left-hand side of a signal (<c>&lt;=</c>) or variable (<c>:=</c>)
/// assignment: a whole object (<c>y</c>), one bit/element (<c>y(3)</c>,
/// <c>mem(to_integer(addr))</c>) or a range (<c>v(3 downto 2)</c>,
/// <c>v(0 to 1)</c>). The single target resolver for every assignment form
/// (plain, conditional, selected, procedure out-parameters), and the single
/// place that turns an assignment into a write.
/// </summary>
public sealed partial class AssignmentTarget
{
    /// <summary>The signal or variable written.</summary>
    public Variable Base { get; }

    /// <summary>Single index, or the left bound of a range; null for a whole-object write.</summary>
    public IndexValue? Left { get; }

    /// <summary>Right bound of a range; null for a single index or a whole-object write.</summary>
    public IndexValue? Right { get; }

    private AssignmentTarget(Variable baseVar, IndexValue? left, IndexValue? right)
    {
        Base = baseVar;
        Left = left;
        Right = right;
    }

    /// <summary>True when only part of <see cref="Base"/> is written.</summary>
    public bool IsPartial => Left != null;

    /// <summary>The indices as written, when both are literals (null bounds mean a whole-object write).</summary>
    public (int Left, int Right)? StaticIndices =>
        Left?.StaticValue is int l ? (Right == null ? (l, l) : Right.StaticValue is int r ? (l, r) : null) : null;

    /// <summary>
    /// Resolves <paramref name="lhs"/> against <paramref name="scope"/>; null
    /// when it is not <c>name</c> or <c>name(index-or-range)</c> of a declared
    /// object (e.g. the left operand of a <c>&lt;=</c> comparison).
    /// </summary>
    public static AssignmentTarget? Resolve(string lhs, HashSet<Variable> scope)
    {
        var m = Shape().Match(lhs);
        if (!m.Success) return null;
        var baseVar = scope.FirstOrDefault(v => v is not null && !string.IsNullOrEmpty(v.Name)
                                                && v.Name.Equals(m.Groups["name"].Value, StringComparison.OrdinalIgnoreCase));
        if (baseVar is null) return null;
        if (!m.Groups["index"].Success) return new AssignmentTarget(baseVar, null, null);

        var (left, right) = SplitRange(m.Groups["index"].Value);
        return new AssignmentTarget(baseVar, IndexValue.Parse(left, scope),
            right == null ? null : IndexValue.Parse(right, scope));
    }

    /// <summary>
    /// Writes <paramref name="value"/>: immediately for a variable
    /// (<paramref name="immediate"/>), otherwise scheduled for the next delta
    /// or, with <paramref name="afterNs"/> &gt; 0, for a later time. A part
    /// whose index is not known yet (an uninitialised index signal) or falls
    /// outside the object is skipped.
    /// </summary>
    public void Assign(SignalScheduler scheduler, object value, int sourceLine, long afterNs, bool immediate)
    {
        (int Left, int Right)? range = null;
        if (IsPartial)
        {
            int l = Left!.Evaluate();
            int r = Right?.Evaluate() ?? l;
            int length = Math.Max(Base.Value?.ToString()?.Length ?? 0, Base.Size);
            if (l < 0 || r < 0 || VectorIndexing.Range(Base, l, r, length) == null) return;
            range = (l, r);
            value = Bits(value, VectorIndexing.RangeWidth(Base, l, r));
        }

        if (immediate)
            Base.Value = range is { } part
                ? VectorIndexing.Write(Base, Base.Value?.ToString() ?? string.Empty, part.Left, part.Right, (string)value)
                : value;
        else if (afterNs > 0)
            scheduler.ScheduleAfterNs(Base, value, afterNs, sourceLine, range);
        else
            scheduler.Schedule(Base, value, sourceLine, range);
    }

    /// <summary>Bit text of a value written into a part <paramref name="width"/> characters wide.</summary>
    private static string Bits(object value, int width)
    {
        string text = value?.ToString()?.Trim() ?? string.Empty;
        return HdlLiteral.TryOthersFill(text, out char fill)
            ? new string(fill, width)
            : text.Trim('\'').Trim('"');
    }

    /// <summary>Splits <c>a downto b</c> / <c>a to b</c> at parenthesis depth 0; a single index has no right bound.</summary>
    internal static (string Left, string? Right) SplitRange(string index)
    {
        var m = RangeKeyword().Match(index);
        while (m.Success)
        {
            string before = index[..m.Index];
            if (before.Count(c => c == '(') == before.Count(c => c == ')'))
                return (before, index[(m.Index + m.Length)..]);
            m = m.NextMatch();
        }
        return (index, null);
    }

    [GeneratedRegex(@"^\s*(?<name>[A-Za-z_]\w*)\s*(\((?<index>.*)\))?\s*$", RegexOptions.Singleline)]
    private static partial Regex Shape();

    [GeneratedRegex(@"\s(downto|to)\s", RegexOptions.IgnoreCase)]
    private static partial Regex RangeKeyword();
}
