using Interpreter.Repositories;

namespace Interpreter.Services;

/// <summary>
/// The static delays a design writes (<c>wait for T</c>, literal <c>after T</c>),
/// read from the parsed entity that is about to run, so the run picks a tick that
/// lands every one of them exactly: the greatest common divisor of the requested
/// tick and those delays. A <c>wait for 1 ns</c> under the default 10 ns tick would
/// otherwise round up to a whole tick and run slower than written. Pure: it reads
/// only the given entity (no process-global state, so concurrent loads cannot mix).
/// </summary>
public static class StaticDelays
{
    /// <summary>
    /// The tick in nanoseconds a run of <paramref name="entity"/> should use:
    /// <paramref name="requestedNs"/> narrowed to the greatest common divisor with
    /// the design's static delays (1 ns at least).
    /// </summary>
    public static int TickNs(Entity entity, int requestedNs)
    {
        long tick = Math.Max(1, requestedNs);
        if (entity.Architecture?.Tasks is { } tasks)
            foreach (long delay in Of(tasks))
                tick = Gcd(tick, delay);
        return (int)tick;
    }

    /// <summary>Every positive static delay (ns) in <paramref name="tasks"/>, nested statements included.</summary>
    public static IEnumerable<long> Of(IEnumerable<object> tasks)
    {
        foreach (var task in tasks)
        {
            var delays = task switch
            {
                WaitForStatement wait => [wait.DelayNs],
                Operation op => op.Waveform?.StaticDelaysNs ?? [],
                ConditionalAssignment ca => ca.Branches.SelectMany(b => Models.Waveform.Parse(b.ValueExpression).StaticDelaysNs),
                SelectedAssignment sa => sa.Choices.SelectMany(c => Models.Waveform.Parse(c.ValueExpression).StaticDelaysNs),
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

    private static long Gcd(long a, long b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return a;
    }
}
