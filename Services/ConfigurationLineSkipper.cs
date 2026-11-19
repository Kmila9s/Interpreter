using System.Text.RegularExpressions;

namespace Interpreter.Services;

/// <summary>
/// Line-based skipping of a configuration declaration (G20) for the line-driven
/// structural parsers: from <c>configuration c of e is</c> to its closing
/// <c>end [configuration] [c];</c>, counting <c>for … end for</c> so a nested
/// <c>end for;</c> never ends it early. The elaborator (<see cref="DesignLibrary"/>)
/// is what applies configurations.
/// </summary>
public sealed partial class ConfigurationLineSkipper
{
    private bool _inside;
    private int _depth;

    /// <summary>
    /// Feeds one lower-cased, trimmed code line; true when it belongs to a
    /// configuration declaration (the caller skips it).
    /// </summary>
    public bool Consume(string lowerLine)
    {
        if (!_inside && !Opener().IsMatch(lowerLine)) return false;
        _inside = true;
        _depth += For().Count(lowerLine) - EndFor().Count(lowerLine);
        if (_depth <= 0 && Closer().IsMatch(lowerLine))
        {
            _inside = false;
            _depth = 0;
        }
        return true;
    }

    [GeneratedRegex(@"^configuration\s+\w+\s+of\b")]
    private static partial Regex Opener();

    [GeneratedRegex(@"(?<!\bend\s+)\bfor\b")]
    private static partial Regex For();

    [GeneratedRegex(@"\bend\s+for\b")]
    private static partial Regex EndFor();

    [GeneratedRegex(@"^end\b(?!\s+for\b)")]
    private static partial Regex Closer();
}
