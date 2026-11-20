using System.Text.RegularExpressions;
using Interpreter.Services;
using Parser.Models;

namespace Interpreter.Models;

/// <summary>
/// The left-hand side of a signal (<c>&lt;=</c>) or variable (<c>:=</c>)
/// assignment: a whole object (<c>y</c>), one bit/element (<c>y(3)</c>,
/// <c>mem(to_integer(addr))</c>), a range (<c>v(3 downto 2)</c>,
/// <c>v(0 to 1)</c>) or a record field (<c>p.lo</c>, <c>m(i).hi</c>,
/// <c>r.d(2)</c>). The single target resolver for every assignment form
/// (plain, conditional, selected, procedure out-parameters), and the single
/// place that turns an assignment into a write.
/// </summary>
public sealed partial class AssignmentTarget
{
    /// <summary>The name and selectors written.</summary>
    public ValuePath Path { get; }

    /// <summary>The signal or variable written.</summary>
    public Variable Base => Path.Base;

    /// <summary>Single index, or the left bound of a range, of a plain <c>v(…)</c> target; null otherwise.</summary>
    public IndexValue? Left => Path.Steps is [ValuePath.IndexStep only] ? only.Left : null;

    /// <summary>Right bound of a plain <c>v(l to|downto r)</c> target; null otherwise.</summary>
    public IndexValue? Right => Path.Steps is [ValuePath.IndexStep only] ? only.Right : null;

    private AssignmentTarget(ValuePath path) => Path = path;

    /// <summary>True when only part of <see cref="Base"/> is written.</summary>
    public bool IsPartial => Path.Steps.Count > 0;

    /// <summary>The written part as constant indices of <see cref="Base"/> (see <see cref="ValuePath.StaticIndices"/>).</summary>
    public (int Left, int Right)? StaticIndices => Path.StaticIndices;

    /// <summary>
    /// Resolves <paramref name="lhs"/> against <paramref name="scope"/>; null
    /// when it is not a declared object followed by index/slice/field selectors
    /// (e.g. the left operand of a <c>&lt;=</c> comparison).
    /// </summary>
    public static AssignmentTarget? Resolve(string lhs, HashSet<Variable> scope) =>
        ValuePath.Parse(lhs, scope) is { } path ? new AssignmentTarget(path) : null;

    /// <summary>
    /// Writes <paramref name="value"/>: immediately for variables
    /// (<paramref name="immediate"/>), otherwise as a zero-delay scheduled
    /// update. A partial write whose selectors do not resolve is dropped.
    /// </summary>
    public void Assign(SignalScheduler? scheduler, object value, int sourceLine, bool immediate)
    {
        if (immediate || scheduler is null)
        {
            (int Start, int Width)? span;
            try
            {
                if (!TryEncode(ref value, out span)) return;
            }
            catch (SimulationRuntimeException e) { throw e.At(sourceLine); }
            Write(value, span);
        }
        else Schedule(scheduler, [new WaveformElement(value, 0)], sourceLine, DelayMechanism.Inertial, -1);
    }

    /// <summary>
    /// Schedules the waveform <paramref name="elements"/> on the current
    /// process's driver of <see cref="Base"/> (the part named by the selectors,
    /// resolved now; a dynamic index makes the driver cover the whole object).
    /// </summary>
    public void Schedule(SignalScheduler scheduler, IReadOnlyList<WaveformElement> elements, int sourceLine,
        DelayMechanism mechanism, long rejectNs)
    {
        (int Start, int Width)? span = null;
        var encoded = new WaveformElement[elements.Count];
        try
        {
            for (int i = 0; i < elements.Count; i++)
            {
                object value = elements[i].Value;
                if (!TryEncode(ref value, out span)) return;
                encoded[i] = elements[i] with { Value = value };
            }
        }
        catch (SimulationRuntimeException e) { throw e.At(sourceLine); }
        scheduler.ScheduleWaveform(Base, encoded, sourceLine, span, wholeDriver: IsPartial && !Path.IsStatic, mechanism, rejectNs);
    }

    /// <summary>Drives <paramref name="value"/> from outside the design (the stimulus of an <c>inout</c> port, see <see cref="SignalScheduler.DriveExternal"/>).</summary>
    public void DriveExternal(SignalScheduler scheduler, object value)
    {
        if (TryEncode(ref value, out var span)) scheduler.DriveExternal(Base, value, span);
    }

    /// <summary>Writes <paramref name="value"/> now (variables, external stimulus).</summary>
    public void WriteNow(object value)
    {
        if (TryEncode(ref value, out var span)) Write(value, span);
    }

    /// <summary>
    /// For a partial target, resolves the span written and encodes the value
    /// for it (field encoding or logic bits); false when the selectors do not
    /// resolve now (dynamic index out of range) and the write is dropped.
    /// </summary>
    private bool TryEncode(ref object value, out (int Start, int Width)? span)
    {
        span = null;
        if (!IsPartial) return true;
        if (Path.Resolve() is not { } at) return false;
        span = (at.Start, at.Width);
        value = at.Field is { } field ? FieldCodec.Encode(field, value) : FieldCodec.LogicBits(value, at.Width);
        return true;
    }

    private void Write(object value, (int Start, int Width)? span) =>
        Base.Value = span is { } part
            ? VectorIndexing.Write(Base, Base.Value?.ToString() ?? string.Empty, part, (string)value)
            : value;

    /// <summary>
    /// Splits an index text at a top-level <c>to</c>/<c>downto</c>:
    /// <c>("3", "2")</c> for <c>3 downto 2</c>, <c>("i", null)</c> for a single index.
    /// </summary>
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

    [GeneratedRegex(@"\s(downto|to)\s", RegexOptions.IgnoreCase)]
    private static partial Regex RangeKeyword();
}
