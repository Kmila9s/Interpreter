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

    /// <summary>
    /// Compares two VCD files and returns differences.
    /// </summary>
    /// <param name="referenceVcd">The reference VCD content (from GHDL).</param>
    /// <param name="testVcd">The test VCD content (from our interpreter).</param>
    /// <param name="signalMapping">Maps reference signal names to test signal names.</param>
    /// <returns>List of differences found.</returns>
    public static List<string> CompareVcds(
        string referenceVcd,
        string testVcd,
        Dictionary<string, string>? signalMapping = null)
    {
        var differences = new List<string>();

        var (refSignals, refChanges) = ParseVcd(referenceVcd);
        var (testSignals, testChanges) = ParseVcd(testVcd);

        // Build signal name to ID mappings
        var refNameToId = refSignals.ToDictionary(kv => kv.Value.ToLower(), kv => kv.Key);
        var testNameToId = testSignals.ToDictionary(kv => kv.Value.ToLower(), kv => kv.Key);

        // Determine signals to compare
        signalMapping ??= new Dictionary<string, string>();

        // Auto-map by name if not specified
        foreach (var refName in refNameToId.Keys)
        {
            if (!signalMapping.ContainsKey(refName) && testNameToId.ContainsKey(refName))
            {
                signalMapping[refName] = refName;
            }
        }

        // Extract values at each time point for each signal
        foreach (var (refName, testName) in signalMapping)
        {
            if (!refNameToId.TryGetValue(refName.ToLower(), out var refId))
            {
                differences.Add($"Reference signal '{refName}' not found");
                continue;
            }
            if (!testNameToId.TryGetValue(testName.ToLower(), out var testId))
            {
                differences.Add($"Test signal '{testName}' not found");
                continue;
            }

            // Get all changes for this signal
            var refSigChanges = refChanges.Where(c => c.SignalId == refId).ToList();
            var testSigChanges = testChanges.Where(c => c.SignalId == testId).ToList();

            // Compare values at key time points
            var allTimes = refSigChanges.Select(c => c.TimeFs)
                .Union(testSigChanges.Select(c => c.TimeFs))
                .OrderBy(t => t)
                .ToList();

            string? lastRefValue = null;
            string? lastTestValue = null;

            foreach (var time in allTimes)
            {
                // Get current values
                var refChange = refSigChanges.LastOrDefault(c => c.TimeFs <= time);
                var testChange = testSigChanges.LastOrDefault(c => c.TimeFs <= time);

                string refValue = refChange?.Value ?? lastRefValue ?? "U";
                string testValue = testChange?.Value ?? lastTestValue ?? "U";

                lastRefValue = refValue;
                lastTestValue = testValue;

                // Normalize values for comparison
                refValue = NormalizeValue(refValue);
                testValue = NormalizeValue(testValue);

                if (refValue != testValue)
                {
                    double timeNs = time / 1000000.0;
                    differences.Add($"Signal '{refName}' mismatch at {timeNs:F1}ns: expected '{refValue}', got '{testValue}'");
                }
            }
        }

        return differences;
    }

    /// <summary>
    /// Normalizes a signal value for comparison.
    /// </summary>
    private static string NormalizeValue(string value)
    {
        // Convert to lowercase, remove leading zeros
        value = value.ToLower();

        // For multi-bit values, normalize format
        if (value.Length > 1 && value.All(c => c == '0' || c == '1' || c == 'x' || c == 'z' || c == 'u'))
        {
            // Remove leading zeros but keep at least one digit
            value = value.TrimStart('0');
            if (string.IsNullOrEmpty(value)) value = "0";
        }

        return value;
    }

    /// <summary>
    /// Runs comparison test and prints results.
    /// </summary>
    public static bool RunComparison(string referenceVcd, string testVcd, string testName)
    {
        Console.WriteLine($"=== VCD Comparison: {testName} ===");

        var differences = CompareVcds(referenceVcd, testVcd);

        if (differences.Count == 0)
        {
            Console.WriteLine("PASS: All signals match!");
            return true;
        }
        else
        {
            Console.WriteLine($"FAIL: {differences.Count} differences found:");
            foreach (var diff in differences.Take(10))
            {
                Console.WriteLine($"  - {diff}");
            }
            if (differences.Count > 10)
            {
                Console.WriteLine($"  ... and {differences.Count - 10} more");
            }
            return false;
        }
    }
}
