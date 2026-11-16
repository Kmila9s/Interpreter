using System.Text.RegularExpressions;

namespace Interpreter.Tests;

/// <summary>
/// Compares VCD files to verify simulation correctness.
/// </summary>
public static class VcdComparison
{
    /// <summary>
    /// Represents a signal value change.
    /// </summary>
    public record SignalChange(long TimeFs, string SignalId, string Value);

    /// <summary>
    /// Parses a VCD file and extracts signal changes.
    /// </summary>
    public static (Dictionary<string, string> SignalNames, List<SignalChange> Changes) ParseVcd(string vcdContent)
    {
        var signalNames = new Dictionary<string, string>();
        var changes = new List<SignalChange>();
        long currentTime = 0;
        long timescaleFs = 1; // Default femtoseconds

        var lines = vcdContent.Split('\n');
        bool inDefinitions = true;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            // Parse timescale
            if (trimmed.StartsWith("$timescale"))
            {
                continue;
            }
            if (inDefinitions && !trimmed.StartsWith("$") && !trimmed.StartsWith("#"))
            {
                // Parse timescale value like "1 fs" or "10ns"
                var tsMatch = Regex.Match(trimmed, @"(\d+)\s*(fs|ps|ns|us|ms|s)");
                if (tsMatch.Success)
                {
                    int value = int.Parse(tsMatch.Groups[1].Value);
                    string unit = tsMatch.Groups[2].Value;
                    timescaleFs = unit switch
                    {
                        "fs" => value,
                        "ps" => value * 1000L,
                        "ns" => value * 1000000L,
                        "us" => value * 1000000000L,
                        "ms" => value * 1000000000000L,
                        "s" => value * 1000000000000000L,
                        _ => value
                    };
                }
            }

            // Parse variable definitions
            if (trimmed.StartsWith("$var"))
            {
                // $var reg 4 $ count[3:0] $end
                // $var wire 1 ! clk $end
                var varMatch = Regex.Match(trimmed, @"\$var\s+\w+\s+\d+\s+(\S+)\s+(\S+)");
                if (varMatch.Success)
                {
                    string id = varMatch.Groups[1].Value;
                    string name = varMatch.Groups[2].Value.TrimEnd('[').Split('[')[0];
                    signalNames[id] = name;
                }
            }

            if (trimmed == "$enddefinitions $end")
            {
                inDefinitions = false;
            }

            // Parse time changes
            if (trimmed.StartsWith("#"))
            {
                if (long.TryParse(trimmed.Substring(1), out long time))
                {
                    currentTime = time * timescaleFs;
                }
            }

            // Parse value changes
            if (!inDefinitions && !trimmed.StartsWith("$") && !trimmed.StartsWith("#"))
            {
                // Binary value: b0101 #
                var binaryMatch = Regex.Match(trimmed, @"^b([01xXzZuU]+)\s+(\S+)");
                if (binaryMatch.Success)
                {
                    string value = binaryMatch.Groups[1].Value;
                    string id = binaryMatch.Groups[2].Value;
                    changes.Add(new SignalChange(currentTime, id, value));
                }
                else if (trimmed.Length >= 2)
                {
                    // Single bit: 0! or 1!
                    char value = trimmed[0];
                    string id = trimmed.Substring(1);
                    if (value == '0' || value == '1' || value == 'x' || value == 'X' || value == 'z' || value == 'Z')
                    {
                        changes.Add(new SignalChange(currentTime, id, value.ToString()));
                    }
                }
            }
        }

        return (signalNames, changes);
    }

}
