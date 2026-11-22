namespace Interpreter.Services;

/// <summary>
/// The static delays a design writes (<c>wait for T</c>, literal <c>after T</c>),
/// recorded while it is parsed, so the run can pick a tick that lands every one
/// of them exactly: the greatest common divisor of the requested tick and those
/// delays. A <c>wait for 1 ns</c> under the default 10 ns tick would otherwise
/// round up to a whole tick and the design would run slower than written.
/// </summary>
/// <remarks>Cleared with the other per-design registries on every design load.</remarks>
public sealed class DesignTimeGrid
{
    private static readonly DesignTimeGrid _instance = new();
    /// <summary>Process-wide singleton.</summary>
    public static DesignTimeGrid Instance => _instance;

    private readonly object _lock = new();
    private long _gcdNs;

    private DesignTimeGrid() { }

    /// <summary>Records a static delay in nanoseconds (zero and negative ones are ignored).</summary>
    public void Note(long delayNs)
    {
        if (delayNs <= 0) return;
        lock (_lock) _gcdNs = Gcd(_gcdNs, delayNs);
    }

    /// <summary>Forgets every recorded delay.</summary>
    public void Clear()
    {
        lock (_lock) _gcdNs = 0;
    }

    /// <summary>
    /// The tick in nanoseconds a run should use: <paramref name="requestedNs"/>
    /// narrowed to the greatest common divisor with the recorded delays (1 ns at least).
    /// </summary>
    public int TickNs(int requestedNs)
    {
        long requested = Math.Max(1, requestedNs);
        lock (_lock) return (int)(_gcdNs > 0 ? Gcd(requested, _gcdNs) : requested);
    }

    private static long Gcd(long a, long b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return a;
    }
}
