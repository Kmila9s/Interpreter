using System.Text.RegularExpressions;
using Interpreter.Services;
using Parser.Models;

namespace Interpreter.Models;

/// <summary>
/// The right-hand side of a signal assignment as the LRM reads it (§10.5.2):
/// an optional delay mechanism (<c>transport</c>, <c>inertial</c>,
/// <c>reject t inertial</c>) and a waveform of one or more elements
/// <c>value [after t]</c> separated by commas. Delays are time expressions
/// evaluated each time the assignment runs.
/// </summary>
public sealed partial class Waveform
{
    /// <summary>The delay mechanism (inertial unless <c>transport</c> is written).</summary>
    public DelayMechanism Mechanism { get; private init; } = DelayMechanism.Inertial;

    /// <summary>The pulse-rejection limit expression of <c>reject t inertial</c>; null = the first delay.</summary>
    public string? Reject { get; private init; }

    /// <summary>Each element's value expression and delay expression (null = no <c>after</c>).</summary>
    public IReadOnlyList<(string Value, string? After)> Elements { get; private init; } = [];

    /// <summary>Each element's delay in fs when it is known before the run (a literal, or over constants once <see cref="StaticDelaysFs"/> folded it); null when it reads objects.</summary>
    private long?[] _folded = [];

    /// <summary>The value expression of the first element (what the assignment's operation evaluates).</summary>
    public string FirstValue => Elements[0].Value;

    /// <summary>True when the assignment is a plain <c>target &lt;= value</c> (no delay, one element).</summary>
    public bool IsPlain => Mechanism == DelayMechanism.Inertial && Reject is null && Elements is [(_, null)];

    /// <summary>Parses the right-hand side <paramref name="rhs"/> of a signal assignment.</summary>
    public static Waveform Parse(string rhs)
    {
        string text = rhs.Trim().TrimEnd(';').Trim();
        var mechanism = DelayMechanism.Inertial;
        string? reject = null;
        var head = Mechanism_().Match(text);
        if (head.Success)
        {
            if (head.Groups["transport"].Success) mechanism = DelayMechanism.Transport;
            else if (head.Groups["reject"].Success) reject = head.Groups["reject"].Value.Trim();
            text = text[head.Length..];
        }
        var elements = new List<(string Value, string? After)>();
        foreach (string part in VhdlText.TopLevelSplit(text, ","))
        {
            int after = TopLevelAfter(part);
            elements.Add(after < 0 ? (part.Trim(), null) : (part[..after].Trim(), part[(after + 5)..].Trim()));
        }
        long?[] folded = [.. elements.Select(e => e.After is null ? 0L : TimeValue.TryEvaluateFs(e.After, null, out long fs) ? (long?)fs : null)];
        return new Waveform { Mechanism = mechanism, Reject = reject, Elements = elements, _folded = folded };
    }

    /// <summary>
    /// The delay mechanism written at the start of <paramref name="rhs"/>
    /// (<c>transport </c>, <c>reject 2 ns inertial </c>…), or empty. In a
    /// conditional or selected assignment it applies to every branch.
    /// </summary>
    public static string MechanismPrefix(string rhs) => Mechanism_().Match(rhs) is { Success: true } m ? m.Value.Trim() + " " : string.Empty;

    /// <summary>Index of the keyword <c>after</c> outside parentheses and literals, or -1.</summary>
    private static int TopLevelAfter(string text)
    {
        int depth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"') { int close = text.IndexOf('"', i + 1); if (close > 0) i = close; continue; }
            if (c == '\'' && i + 2 < text.Length && text[i + 2] == '\'') { i += 2; continue; }
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (depth == 0 && i + 5 <= text.Length && string.Compare(text, i, "after", 0, 5, StringComparison.OrdinalIgnoreCase) == 0
                && (i == 0 || !IsWordChar(text[i - 1])) && (i + 5 == text.Length || !IsWordChar(text[i + 5])))
                return i;
        }
        return -1;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>
    /// The delays of the elements known before the run, in fs: literals, and time
    /// expressions over constants of <paramref name="scope"/> or of a package
    /// (<c>after HALF</c>, <c>after PERIOD/2</c>). Delays reading signals are left out.
    /// </summary>
    public IEnumerable<long> StaticDelaysFs(HashSet<Variable>? scope)
    {
        HashSet<Variable>? constants = null;
        for (int i = 0; i < Elements.Count; i++)
        {
            if (_folded[i] is null)
            {
                constants ??= scope is null ? [] : [.. scope.Where(v => v.IsConstant)];
                // Over constants the value is fixed: keep it, so the run reads it instead of re-evaluating.
                if (TimeValue.TryEvaluateFs(Elements[i].After!, constants, out long fs)) _folded[i] = fs;
            }
            if (_folded[i] is long delay) yield return delay;
        }
    }

    /// <summary>The delay in fs of element <paramref name="index"/>: folded when literal, else evaluated now against <paramref name="scope"/>.</summary>
    public long ElementDelayFs(int index, HashSet<Variable> scope, int line) =>
        _folded[index] ?? DelayFs(Elements[index].After, scope, line);

    /// <summary>
    /// The delay in femtoseconds of the time expression <paramref name="after"/>
    /// (0 when null), evaluated now against <paramref name="scope"/>.
    /// </summary>
    public static long DelayFs(string? after, HashSet<Variable> scope, int line)
    {
        if (after is null) return 0;
        if (TimeValue.TryEvaluateFs(after, scope, out long fs)) return fs;
        throw new ParserException($"cannot evaluate the time expression \"{after}\"", line, 0);
    }

    [GeneratedRegex(@"^\s*(?:(?<transport>transport)\b|reject\b(?<reject>.+?)\binertial\b|inertial\b)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Mechanism_();
}
