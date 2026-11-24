using Interpreter.Repositories;

namespace Interpreter.Services;

/// <summary>
/// The static delays a design writes (<c>wait for T</c>, literal <c>after T</c>),
/// read from the parsed entity that is about to run, so the run picks a tick that
/// lands every one of them exactly: the greatest common divisor, in femtoseconds, of
/// the requested tick and those delays. A <c>wait for 500 ps</c> or a half period of
/// <c>5.26315789474 ns</c> under the default 10 ns tick would otherwise round to a
/// whole tick and run at the wrong time (G103). Pure: it reads only the given entity
/// (no process-global state, so concurrent loads cannot mix).
/// </summary>
public static class StaticDelays
{
    /// <summary>
    /// The tick in femtoseconds a run of <paramref name="entity"/> should use:
    /// <paramref name="requestedFs"/> narrowed to the greatest common divisor with
    /// the design's static delays (1 fs at least).
    /// </summary>
    public static long TickFs(Entity entity, long requestedFs)
    {
        long tick = Math.Max(1, requestedFs);
        if (entity.Architecture?.Tasks is { } tasks)
            foreach (long delay in Of(tasks))
                tick = Gcd(tick, delay);
        return tick;
    }

    /// <summary>Every positive static delay (fs) in <paramref name="tasks"/>, nested statements included.</summary>
    public static IEnumerable<long> Of(IEnumerable<object> tasks)
    {
        foreach (var task in tasks)
        {
            var delays = task switch
            {
                WaitForStatement wait => [wait.DelayFs],
                Operation op => op.Waveform?.StaticDelaysFs(op.Variables) ?? [],
                ConditionalAssignment ca => ca.Branches.Where(b => HasAfter(b.ValueExpression)).SelectMany(b => Models.Waveform.Parse(b.ValueExpression).StaticDelaysFs(ca.Scope)),
                SelectedAssignment sa => sa.Choices.Where(c => HasAfter(c.ValueExpression)).SelectMany(c => Models.Waveform.Parse(c.ValueExpression).StaticDelaysFs(sa.Scope)),
                Process p => Of(p.Tasks),
                IfStructure ifs => ifs.Branches.SelectMany(b => Of(b.Tasks)),
                CaseStructure cs => cs.Branches.SelectMany(b => Of(b.Tasks)),
                ForLoopStructure fs => Of(fs.Tasks),
                WhileLoopStructure ws => Of(ws.Tasks),
                SimpleLoopStructure ss => Of(ss.Tasks),
                GenerateBlock g => Of(g.Tasks),
                _ => (IEnumerable<long>)[],
            };
            foreach (long d in delays)
                if (d > 0) yield return d;
        }
    }

    /// <summary>Cheap pre-check: only a value with the word <c>after</c> can carry a delay.</summary>
    private static bool HasAfter(string value) => value.Contains("after", StringComparison.OrdinalIgnoreCase);

    /// <summary>The greatest common divisor of <paramref name="a"/> and <paramref name="b"/>.</summary>
    public static long Gcd(long a, long b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return a;
    }
}
