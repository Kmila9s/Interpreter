using Interpreter.Services;
using Parser.Models;
using Parser.Repositories;

namespace Interpreter;

/// <summary>
/// Main entry point for the Kmila VHDL interpreter and simulator.
/// Supports test file processing, delta cycle testing, and simulation with VCD export.
/// </summary>
/// <remarks>
/// <para><strong>Command-line usage:</strong></para>
/// <list type="bullet">
/// <item><c>dotnet run</c> -- Process all test*.vhdl files in the current directory</item>
/// <item><c>dotnet run -- --test-delta</c> -- Run delta cycle engine unit tests</item>
/// <item><c>dotnet run -- --sim file.vhdl --cycles 100 --vcd output.vcd</c> -- Simulate and export VCD</item>
/// <item><c>dotnet run -- --verbose</c> -- Enable verbose progress reporting</item>
/// <item><c>dotnet run -- --progress</c> -- Show progress bar during batch processing</item>
/// </list>
/// </remarks>
class Program
{
    /// <summary>
    /// Flag to control verbose progress output. Enabled with <c>--verbose</c> or <c>-v</c>.
    /// </summary>
    static bool _verboseProgress = false;

    /// <summary>
    /// Cancellation token source for graceful Ctrl+C handling.
    /// </summary>
    static CancellationTokenSource _cts = new();

    /// <summary>
    /// Application entry point. Parses command-line arguments and dispatches to the
    /// appropriate mode: batch test processing, delta cycle tests, or simulation.
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    static void Main(string[] args)
    {
        // Parse command line arguments
        _verboseProgress = args.Contains("--verbose") || args.Contains("-v");
        bool showProgressBar = args.Contains("--progress") || args.Contains("-p");

        // Check for delta cycle test mode
        if (args.Contains("--test-delta") || args.Contains("-td"))
        {
            RunDeltaCycleTests().Wait();
            return;
        }

        // Check for simulation mode with VCD export
        // Usage: --sim <file.vhdl> [--cycles N] [--vcd output.vcd]
        int simIndex = Array.FindIndex(args, a => a == "--sim" || a == "-s");
        if (simIndex >= 0 && simIndex + 1 < args.Length)
        {
            string vhdlFile = args[simIndex + 1];
            int cycles = 10; // Default simulation cycles
            string? vcdOutput = null;

            // Parse optional arguments
            int cyclesIndex = Array.FindIndex(args, a => a == "--cycles" || a == "-c");
            if (cyclesIndex >= 0 && cyclesIndex + 1 < args.Length)
            {
                int.TryParse(args[cyclesIndex + 1], out cycles);
            }

            int vcdIndex = Array.FindIndex(args, a => a == "--vcd");
            if (vcdIndex >= 0 && vcdIndex + 1 < args.Length)
            {
                vcdOutput = args[vcdIndex + 1];
            }
            else
            {
                // Default VCD filename based on VHDL file
                vcdOutput = Path.ChangeExtension(vhdlFile, ".vcd");
            }

            RunSimulation(vhdlFile, cycles, vcdOutput).Wait();
            return;
        }

        // Setup Ctrl+C handling for graceful cancellation
        Console.CancelKeyPress += (sender, e) =>
        {
            e.Cancel = true; // Prevent immediate termination
            Console.WriteLine("\nCancellation requested... Please wait.");
            _cts.Cancel();
        };

        // Setup progress reporting
        var progress = ProgressReporter.Instance;
        progress.IsEnabled = true;
        progress.ConsoleOutput = false; // We'll handle output ourselves

        // Register cancellation token with progress reporter
        progress.StartCancellableOperation(_cts);

        // Subscribe to cancellation events
        progress.CancellationRequested += (sender, e) =>
        {
            Console.WriteLine($"Operation cancelled: {e.Update.Message}");
        };

        if (_verboseProgress)
        {
            // Subscribe to all progress updates for verbose mode
            progress.ProgressChanged += OnProgressChanged;
            progress.ErrorOccurred += OnErrorOccurred;
        }

        // Find all test*.vhdl files in the current directory
        string currentDir = Directory.GetCurrentDirectory();
        var testFiles = Directory.GetFiles(currentDir, "test*.vhdl")
                                 .OrderBy(f => f)
                                 .ToArray();

        if (testFiles.Length == 0)
        {
            Console.WriteLine("No test*.vhdl files found in: " + currentDir);
            return;
        }

        Console.WriteLine($"Found {testFiles.Length} test file(s)");
        Console.WriteLine(new string('=', 60));

        // v0.16.1: Pre-scan ALL test files for entities before processing any
        // This allows cross-file entity references to work correctly
        EntityRegistry.Instance.Clear();
        PackageRegistry.Instance.Clear();
        FunctionRegistry.Instance.Clear();
        foreach (var file in testFiles)
        {
            EntityRegistry.Instance.ScanFile(file);
        }

        int passedTests = 0;
        int failedTests = 0;

        for (int i = 0; i < testFiles.Length; i++)
        {
            var testFile = testFiles[i];
            string fileName = Path.GetFileName(testFile);

            // Reset progress for each file
            progress.Reset();

            if (showProgressBar)
            {
                // Show overall test progress
                float overallProgress = (float)i / testFiles.Length;
                Console.Write($"\r[{GetProgressBar(overallProgress, 20)}] {overallProgress * 100:F0}% | {fileName}".PadRight(70));
            }
            else
            {
                Console.WriteLine($"\n[TEST] Processing: {fileName}");
                Console.WriteLine(new string('-', 40));
            }

            try
            {
                // Check for cancellation before processing each file
                if (_cts.Token.IsCancellationRequested)
                {
                    Console.WriteLine("\nProcessing cancelled by user.");
                    break;
                }

                // Note: EntityRegistry already pre-scanned, just clear PackageRegistry per file
                PackageRegistry.Instance.Clear();
                // Same for FunctionRegistry — function/procedure names from a
                // previous fixture must not leak into the next one.
                FunctionRegistry.Instance.Clear();
                // Reset the silent-skip diagnostic log so this fixture's
                // entries are isolated from the previous one.
                SkipDiagnosticLog.Instance.Clear();

                string raw = File.ReadAllText(testFile);
                var result = ProcessVhdlCode(raw, _cts.Token);
                raw = null!;  // release the source string immediately

                if (!showProgressBar)
                {
                    Console.WriteLine($"  Entities parsed: {result.entities.Count}");
                    foreach (var entity in result.entities)
                    {
                        Console.WriteLine($"    - {entity.Name}: {entity.Ports.Count} ports, {entity.Generics.Count} generics");
                        if (entity.Architecture != null)
                        {
                            Console.WriteLine($"      Architecture tasks: {entity.Architecture.Tasks.Count}");
                        }

                        // Display resource estimation (only if architecture exists)
                        if (entity.Architecture != null && entity.ResourceUsage != null)
                        {
                            Console.WriteLine($"      Resources: FF={entity.ResourceUsage.FlipFlops}, " +
                                            $"LUT={entity.ResourceUsage.LUTs}, " +
                                            $"DSP={entity.ResourceUsage.DSPSlices}, " +
                                            $"BRAM={entity.ResourceUsage.BRAMBlocks:F1}, " +
                                            $"IO={entity.ResourceUsage.IOPins}, " +
                                            $"CLK={entity.ResourceUsage.ClockDomains}");
                        }
                    }

                    Console.WriteLine($"  [PASS] {fileName} - Synthesized successfully!");

                    // Surface silent-skip diagnostics so the user sees what was
                    // dropped or stubbed during this fixture's parse.
                    var diags = SkipDiagnosticLog.Instance.Drain();
                    if (diags.Count > 0)
                    {
                        foreach (var d in diags)
                        {
                            string sev = d.Severity == SkipSeverity.Warning ? "WARN" : "INFO";
                            string subj = string.IsNullOrEmpty(d.Subject) ? "" : $" [{d.Subject}]";
                            Console.WriteLine($"    [{sev} {d.Code}]{subj} {d.Message}");
                        }
                    }
                }
                passedTests++;
                // Release the per-fixture entity graph + its tokenizer/streamer
                // before the next iteration so peak RSS doesn't accumulate
                // proportionally to fixture count.
                result = default;
                if ((i & 7) == 7) GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
                if (Environment.GetEnvironmentVariable("KMILA_TRACE_HEAP") == "1")
                {
                    long alloc = GC.GetTotalMemory(forceFullCollection: false);
                    Console.WriteLine($"    [heap] {alloc / 1024,8:n0} KB after {fileName}");
                }
            }
            catch (OperationCanceledException)
            {
                // User requested cancellation
                Console.WriteLine($"\n  [CANCELLED] {fileName}");
                Console.WriteLine("Processing was cancelled by user request.");
                break;
            }
            catch (Exception ex)
            {
                if (showProgressBar)
                {
                    Console.WriteLine(); // New line after progress bar
                }
                Console.WriteLine($"  [FAIL] {fileName}");
                Console.WriteLine($"    Error: {ex.Message}");
                if (_verboseProgress)
                {
                    Console.WriteLine($"    Stack: {ex.StackTrace}");
                }
                failedTests++;
            }
        }

        if (showProgressBar)
        {
            Console.WriteLine(); // Final newline after progress bar
        }

        Console.WriteLine(new string('=', 60));
        Console.WriteLine($"\nTest Results: {passedTests} passed, {failedTests} failed, {testFiles.Length} total");

        if (failedTests == 0)
        {
            Console.WriteLine("All tests passed successfully!");
        }
        else
        {
            Console.WriteLine("Some tests failed. Please review the errors above.");
            Environment.ExitCode = 1;
        }
    }

    /// <summary>
    /// Runs the delta cycle engine unit tests.
    /// </summary>
    static async Task RunDeltaCycleTests()
    {
        int failures = await Tests.DeltaCycleEngineTests.RunAllTests();
        failures += await Tests.SynthesisTests.RunAllTests();
        failures += Tests.IEEELibraryLoaderTests.RunAllTests();
        failures += Tests.LibraryCompilerTests.RunAllTests();
        failures += Tests.IEEEPrimitivesTests.RunAllTests();
        failures += Tests.IEEELoweringEndToEndTests.RunAllTests();
        failures += Tests.NestedIEEECallTests.RunAllTests();
        failures += Tests.PracticeExerciseTests.RunAllTests();
        Environment.ExitCode = failures;
    }

    /// <summary>
    /// Runs a simulation on a VHDL file and exports waveforms to VCD.
    /// </summary>
    /// <param name="vhdlFile">The VHDL file to simulate.</param>
    /// <param name="cycles">Number of simulation cycles to run.</param>
    /// <param name="vcdOutput">Output VCD file path.</param>
    static async Task RunSimulation(string vhdlFile, int cycles, string vcdOutput)
    {
        Console.WriteLine($"=== VHDL Simulation ===");
        Console.WriteLine($"File: {vhdlFile}");
        Console.WriteLine($"Cycles: {cycles}");
        Console.WriteLine($"VCD Output: {vcdOutput}");
        Console.WriteLine();

        if (!File.Exists(vhdlFile))
        {
            Console.WriteLine($"Error: File not found: {vhdlFile}");
            Environment.ExitCode = 1;
            return;
        }

        try
        {
            // Read and process the VHDL file
            string raw = File.ReadAllText(vhdlFile);
            var (entities, archCount) = ProcessVhdlCode(raw);

            if (entities.Count == 0)
            {
                Console.WriteLine("Error: No entities found in file");
                Environment.ExitCode = 1;
                return;
            }

            // Get the first entity with an architecture
            var entity = entities.FirstOrDefault(e => e.Architecture != null);
            if (entity == null)
            {
                Console.WriteLine("Error: No entity with architecture found");
                Environment.ExitCode = 1;
                return;
            }

            Console.WriteLine($"Simulating entity: {entity.Name}");
            Console.WriteLine($"Ports: {entity.Ports.Count}");
            Console.WriteLine($"Signals: {entity.Architecture!.Signals.Count}");
            Console.WriteLine();

            // Collect all signals for simulation
            HashSet<Variable> allSignals = [.. entity.Ports, .. entity.Architecture.Signals];

            // Set default values for input ports
            Console.WriteLine("Setting up input signals:");
            foreach (var signal in allSignals)
            {
                if (signal.PortType == Parser.Models.Constants.PortType.IN)
                {
                    string defaultVal = signal.Size > 1 ? new string('0', signal.Size) : "0";

                    // Special handling for common signal names
                    if (signal.Name.Equals("reset", StringComparison.OrdinalIgnoreCase) ||
                        signal.Name.Equals("rst", StringComparison.OrdinalIgnoreCase))
                    {
                        defaultVal = "0"; // Active low reset, not asserted
                    }
                    else if (signal.Name.Equals("enable", StringComparison.OrdinalIgnoreCase) ||
                             signal.Name.Equals("en", StringComparison.OrdinalIgnoreCase))
                    {
                        defaultVal = "1"; // Enable active
                    }

                    signal.Value = defaultVal;
                    Console.WriteLine($"  {signal.Name} = {defaultVal}");
                }
            }
            Console.WriteLine();

            // Create delta cycle engine
            var engine = new DeltaCycleEngine();
            engine.Initialize(allSignals, entity.Architecture.Tasks);

            Console.WriteLine("Running simulation...");

            // Find clock signal
            var clk = allSignals.FirstOrDefault(s =>
                s.Name.Equals("clk", StringComparison.OrdinalIgnoreCase) ||
                s.Name.Equals("clock", StringComparison.OrdinalIgnoreCase));

            // Run simulation for specified cycles. NOTE: use `is not null`
            // (pattern match) — Variable's overloaded `operator !=(Variable, string)`
            // wrongly returns true when both operands are null because of an
            // implicit string-conversion path picked up by overload resolution.
            for (int cycle = 0; cycle < cycles; cycle++)
            {
                if (clk is not null)
                {
                    // Rising edge
                    clk.Value = "1";
                    engine.AdvanceTime((ulong)(cycle * 2));
                    await engine.RunTimeStepAsync();

                    // Falling edge
                    clk.Value = "0";
                    engine.AdvanceTime((ulong)(cycle * 2 + 1));
                    await engine.RunTimeStepAsync();
                }
                else
                {
                    engine.AdvanceTime((ulong)cycle);
                    await engine.RunTimeStepAsync();
                }

                // Progress indicator
                if ((cycle + 1) % 10 == 0 || cycle == cycles - 1)
                {
                    Console.Write($"\rCycle {cycle + 1}/{cycles}");
                }
            }
            Console.WriteLine();

            // Export to VCD
            Console.WriteLine($"\nExporting waveform to: {vcdOutput}");
            engine.History.SaveToVcd(vcdOutput, entity.Name, timescaleNs: 10);

            Console.WriteLine("Simulation complete!");
            Console.WriteLine($"\nTo view waveforms:");
            Console.WriteLine($"  gtkwave {vcdOutput}");

            // Print summary of final signal values
            Console.WriteLine("\n=== Final Signal Values ===");
            foreach (var signal in allSignals.OrderBy(s => s.Name))
            {
                Console.WriteLine($"  {signal.Name} = {signal.Value}");
            }

            engine.Dispose();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error during simulation: {ex.Message}");
            if (_verboseProgress)
            {
                Console.WriteLine(ex.StackTrace);
            }
            Environment.ExitCode = 1;
        }
    }

    /// <summary>
    /// Processes raw VHDL source code: tokenizes, parses entity declarations and architecture
    /// bodies, and synthesizes behavioral descriptions.
    /// </summary>
    /// <param name="raw">The raw VHDL source code as a single string.</param>
    /// <param name="cancellationToken">Optional cancellation token for long-running operations.</param>
    /// <returns>
    /// A tuple containing the list of parsed <see cref="Entity"/> objects and the number
    /// of architecture bodies that were successfully synthesized.
    /// </returns>
    // Marked `internal` (was implicit private) so the in-assembly test
    // suite at Interpreter/Tests/PracticeExerciseTests.cs can run the
    // same parsing pipeline the --sim CLI flag uses, without copying its
    // ~300-line line-state-machine + private helpers (JoinContinuationLines,
    // ParsePackageConstants, …) into the test file. Still invisible
    // outside the assembly.
    internal static (List<Entity> entities, int architectureCount) ProcessVhdlCode(string raw, CancellationToken cancellationToken = default)
    {
        // Seed the IEEE library symbol table once per process so calls into
        // the IEEE.std_logic_1164 / IEEE.numeric_std API resolve through
        // FunctionRegistry rather than falling back to the implicit-
        // placeholder path (#20). Idempotent.
        bool firstLoad = !IEEELibraryLoader.Instance.IsLoaded;
        IEEELibraryLoader.Instance.EnsureLoaded();
        if (firstLoad && Environment.GetEnvironmentVariable("KMILA_TRACE_IEEE") == "1")
        {
            Console.WriteLine($"[IEEE] stub root: {IEEELibraryLoader.Instance.StubRoot ?? "(not found — fallback names seeded)"}");
        }

        Tokenizer tok = new("");
        var lines = raw.Split("\n")
                       .Where(p => !String.IsNullOrEmpty(p.Trim()) && p != " ")
                       .ToArray();
        // Pre-pass: VHDL has no continuation character. Join physical lines
        // that belong to a single logical statement so the line-by-line
        // state machine below (it=1/it=2 transitions, funcProcDepth tracking,
        // arch-end detection) sees one statement per element.
        var prev = JoinContinuationLines(lines);

        List<Entity> Entities = new();
        List<string> rawEntity = new();
        int it = 0;
        string currentEntityName = string.Empty;
        string currentArchName = string.Empty;
        int architectureCount = 0;

        bool inPackage = false;
        bool inConfiguration = false;
        // v0.13.0 Fix: Track function/procedure nesting depth to prevent premature architecture end
        // Issue: 'end FunctionName;' was being confused with 'end ArchitectureName;'
        // Solution: Count function/procedure starts and ends, only check for architecture end when depth is 0
        int funcProcDepth = 0;

        // v0.15.0: Collect package lines for parsing
        List<string> packageLines = new();
        string currentPackageName = string.Empty;
        bool isPackageBody = false;

        foreach (var item in prev)
        {
            // Check for cancellation periodically during parsing
            cancellationToken.ThrowIfCancellationRequested();
            string comparer = item.Trim().ToLower();

            // v0.15.0: Parse package declarations and bodies to extract constants
            if (comparer.StartsWith("package ") && !comparer.StartsWith("package body"))
            {
                inPackage = true;
                isPackageBody = false;
                // Extract package name: "package pkg_name is" -> "pkg_name"
                var parts = item.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                    currentPackageName = parts[1];
                packageLines.Clear();
                packageLines.Add(item);
                continue;
            }
            if (comparer.StartsWith("package body"))
            {
                inPackage = true;
                isPackageBody = true;
                // Extract package name: "package body pkg_name is" -> "pkg_name"
                var parts = item.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3)
                    currentPackageName = parts[2];
                packageLines.Clear();
                packageLines.Add(item);
                continue;
            }
            if (inPackage)
            {
                packageLines.Add(item);
                // Check for end of package
                if (comparer.StartsWith("end ") && comparer.EndsWith(";"))
                {
                    inPackage = false;
                    // v0.15.0: Parse package constants (simple integer constants only)
                    try
                    {
                        ParsePackageConstants(currentPackageName, packageLines.ToArray(), isPackageBody);
                    }
                    catch
                    {
                        // Ignore errors in package parsing - don't affect main parsing
                    }
                    packageLines.Clear();
                }
                continue;
            }

            // Skip configuration declarations
            if (comparer.StartsWith("configuration "))
            {
                inConfiguration = true;
                continue;
            }
            if (inConfiguration)
            {
                // Check for end of configuration
                if (comparer.StartsWith("end ") && comparer.EndsWith(";"))
                {
                    inConfiguration = false;
                }
                continue;
            }

            // Skip library and use statements
            if (comparer.StartsWith("library ") || comparer.StartsWith("use "))
            {
                continue;
            }

            if (comparer.StartsWith("entity"))
            {
                it = 1;
            }
            else if (comparer.StartsWith("architecture"))
            {
                var archParts = item.Split(' ');
                currentArchName = archParts[1];  // Architecture name (e.g., "behavioral")
                currentEntityName = archParts[3];  // Entity name (e.g., "example")
                it = 2;
            }

            if (it == 1)
            {
                if (comparer.StartsWith("end ") && comparer.EndsWith(";"))
                {
                    rawEntity.Add(item.Trim());
                    Entity entity = new();
                    entity.Syntetize(rawEntity.ToArray(), cancellationToken);
                    Entities.Add(entity);
                    rawEntity = new();
                    it = 0;
                }
                else
                {
                    rawEntity.Add(item);
                }
            }
            else if (it == 2)
            {
                rawEntity.Add(item);

                // Track function/procedure depth so the matching `end <fn_name>;`
                // line doesn't get classified as the architecture end.
                //
                // The previous heuristic decremented the counter on every `end <X>;`
                // it saw inside a function — including `end if;`, `end loop;`,
                // and `end case;`. That made the counter zero out before reaching
                // the actual `end <fn_name>;`, after which the substring fallback
                // below would happily match "end maxv;" against arch name "A"
                // (because "maxv" contains an "a").
                //
                // New rule:
                //   • Increment on `function ... is` / `procedure ... is`.
                //   • Decrement ONLY on `end function;`, `end procedure;`, or
                //     `end <name>;` where <name> is NOT a control-flow keyword.
                //   • Control-flow ends (`end if`, `end loop`, `end case`,
                //     `end for`, `end while`, `end generate`, `end record`) are
                //     ignored at this layer.
                if ((comparer.Contains("function ") && comparer.Contains(" is")) ||
                    (comparer.Contains("procedure ") && comparer.Contains(" is")))
                {
                    funcProcDepth++;
                }
                else if (comparer.StartsWith("end ") && comparer.EndsWith(";"))
                {
                    var parts = comparer.Split(' ');
                    if (parts.Length >= 2)
                    {
                        var endName = parts[1].TrimEnd(';').ToLower();
                        var controlFlow = new[] { "if", "loop", "case", "for",
                                                  "while", "generate", "record" };
                        if (endName == "function" || endName == "procedure")
                        {
                            if (funcProcDepth > 0) funcProcDepth--;
                            continue;
                        }
                        if (controlFlow.Contains(endName))
                        {
                            // Control-flow end — does NOT close the enclosing
                            // function/procedure. Leave funcProcDepth alone.
                            continue;
                        }
                        if (funcProcDepth > 0)
                        {
                            // `end <fn_name>;` while inside a function — close it.
                            funcProcDepth--;
                            continue;
                        }
                    }
                }

                // Only check for architecture end when not inside a function/procedure (depth == 0)
                if (funcProcDepth > 0)
                    continue;

                // Check for "end <arch_name>;" to end the architecture block
                bool isEndOfArchitecture = false;

                if (comparer.StartsWith("end ") && comparer.EndsWith(";"))
                {
                    var parts = comparer.Split(' ');
                    if (parts.Length >= 2)
                    {
                        var endName = parts[1].TrimEnd(';').ToLower();
                        // Skip control structures and inner-decl ends.
                        var skipNames = new[] { "if", "process", "case", "loop",
                                                "for", "while", "generate",
                                                "function", "procedure", "record",
                                                "component", "block", "units" };
                        if (!skipNames.Contains(endName))
                        {
                            // Exact-name match only. The previous code also
                            // accepted any line containing the arch/entity
                            // name as a substring, which made `end maxv;`
                            // match arch name "A" via the embedded `a`.
                            // Also accept the bare `end;` form (parts.Length==2,
                            // endName == ""). And accept the explicit
                            // `end architecture;` keyword form.
                            if (endName == currentArchName.ToLower() ||
                                endName == currentEntityName.ToLower() ||
                                endName == "architecture" ||
                                string.IsNullOrEmpty(endName))
                            {
                                isEndOfArchitecture = true;
                            }
                        }
                    }
                }

                if (isEndOfArchitecture)
                {
                    Entity entity = Entities.FirstOrDefault(p => p.Name.Equals(currentEntityName)) ?? default!;
                    if (entity == null) throw new Exception($"Entity '{currentEntityName}' not found");

                    Architecture arch = new(entity.Ports, entity.Generics);
                    arch.Syntetize(rawEntity.ToArray(), cancellationToken);
                    entity.SetArchitecture(arch);
                    architectureCount++;
                    rawEntity = new();
                    it = 0;
                }
            }
        }

        return (Entities, architectureCount);
    }

    /// <summary>
    /// Parses package constants and registers them in the PackageRegistry.
    /// v0.15.0: Added to support package constant resolution.
    /// </summary>
    /// <param name="packageName">The name of the package.</param>
    /// <param name="lines">The package source lines.</param>
    /// <param name="isBody">True if this is a package body, false for package declaration.</param>
    static void ParsePackageConstants(string packageName, string[] lines, bool isBody)
    {
        if (string.IsNullOrEmpty(packageName) || lines.Length == 0)
            return;

        var registry = PackageRegistry.Instance;

        // Check if package already exists
        var existingPackage = registry.GetPackage(packageName);
        var packageDef = existingPackage ?? new PackageDefinition { Name = packageName };

        // Parse each line looking for constant declarations
        foreach (var line in lines)
        {
            string trimmed = line.Trim();
            string lower = trimmed.ToLower();

            // Skip non-constant lines
            if (!lower.StartsWith("constant "))
                continue;

            try
            {
                // Parse: constant NAME : TYPE := VALUE;
                // Example: constant DATA_WIDTH : integer := 32;
                // Example: constant DEPTH : integer := 16;

                // Remove "constant " prefix
                string rest = trimmed.Substring(9).Trim();

                // Find the colon
                int colonIdx = rest.IndexOf(':');
                if (colonIdx < 0) continue;

                string constName = rest.Substring(0, colonIdx).Trim();

                // Find the := for initialization
                int assignIdx = rest.IndexOf(":=");
                if (assignIdx < 0)
                {
                    // Declaration without initialization (common in package declaration)
                    // Just register the name, value will be set in package body
                    continue;
                }

                // Get type (between : and :=)
                string typeStr = rest.Substring(colonIdx + 1, assignIdx - colonIdx - 1).Trim();

                // Get value (after := until ;)
                int semiIdx = rest.IndexOf(';', assignIdx);
                if (semiIdx < 0) semiIdx = rest.Length;
                string valueStr = rest.Substring(assignIdx + 2, semiIdx - assignIdx - 2).Trim();

                // Determine datatype
                var dataType = Variable.GetDatatype(typeStr.Split('(')[0].Trim());

                // Evaluate value
                object value;
                if (int.TryParse(valueStr, out int intVal))
                {
                    value = intVal;
                }
                else if (valueStr.StartsWith("\"") && valueStr.EndsWith("\""))
                {
                    value = valueStr.Trim('"');
                }
                else if (valueStr.StartsWith("'") && valueStr.EndsWith("'"))
                {
                    value = valueStr.Trim('\'');
                }
                else if (valueStr.ToLower() == "true")
                {
                    value = true;
                }
                else if (valueStr.ToLower() == "false")
                {
                    value = false;
                }
                else if (valueStr.StartsWith("x\"") || valueStr.StartsWith("X\""))
                {
                    // Hex literal
                    string hex = valueStr.Substring(2, valueStr.Length - 3);
                    value = string.Join("", hex.Select(c =>
                        Convert.ToString(Convert.ToInt32(c.ToString(), 16), 2).PadLeft(4, '0')));
                }
                else if (StandardFunctions.IsStandardFunction(valueStr.Split('(')[0].Trim()))
                {
                    // Function call like clog2(16)
                    string funcName = valueStr.Split('(')[0].Trim();
                    int openParen = valueStr.IndexOf('(');
                    int closeParen = valueStr.LastIndexOf(')');
                    if (openParen >= 0 && closeParen > openParen)
                    {
                        string argsStr = valueStr.Substring(openParen + 1, closeParen - openParen - 1);
                        var args = new List<object>();

                        // Parse arguments (simple case: single integer or constant reference)
                        foreach (var arg in argsStr.Split(','))
                        {
                            string argTrim = arg.Trim();
                            if (int.TryParse(argTrim, out int argInt))
                            {
                                args.Add(argInt);
                            }
                            else
                            {
                                // Try to resolve as constant
                                var refConst = registry.GetConstant(argTrim);
                                if (refConst != null && refConst.Value != null)
                                    args.Add(refConst.Value);
                                else
                                    args.Add(argTrim);
                            }
                        }

                        value = StandardFunctions.EvaluateFunction(funcName, args.ToArray(), new HashSet<Variable>());
                    }
                    else
                    {
                        value = valueStr;
                    }
                }
                else
                {
                    // Check if it's a reference to another constant
                    var refConst = registry.GetConstant(valueStr);
                    if (refConst != null && refConst.Value != null)
                        value = refConst.Value;
                    else
                        value = valueStr;
                }

                // Create and register the constant
                var constant = new Variable
                {
                    Name = constName,
                    Type = dataType,
                    Value = value,
                    PortType = Parser.Models.Constants.PortType.IN // Constants are read-only
                };

                registry.RegisterConstant(constant, packageName);

#if DEBUG
                Console.WriteLine($"[ParsePackageConstants] Registered: {packageName}.{constName} = {value}");
#endif
            }
            catch (Exception ex)
            {
#if DEBUG
                Console.WriteLine($"[ParsePackageConstants] Error parsing constant: {ex.Message}");
#endif
            }
        }

        // Register the package if it's new
        if (existingPackage == null)
        {
            registry.TryRegisterPackage(packageDef);
        }
    }

    /// <summary>
    /// Event handler for progress updates in verbose mode.
    /// </summary>
    static void OnProgressChanged(object? sender, ProgressEventArgs e)
    {
        var update = e.Update;
        string phaseIcon = update.Phase switch
        {
            ProcessingPhase.Initializing => "🔄",
            ProcessingPhase.Tokenizing => "📝",
            ProcessingPhase.ParsingEntity => "📦",
            ProcessingPhase.ParsingPorts => "🔌",
            ProcessingPhase.ParsingGenerics => "⚙️",
            ProcessingPhase.ParsingArchitecture => "🏗️",
            ProcessingPhase.ParsingSignals => "📡",
            ProcessingPhase.ParsingTypes => "📐",
            ProcessingPhase.SynthesizingBehavior => "⚡",
            ProcessingPhase.ParsingProcess => "🔄",
            ProcessingPhase.ParsingIfStructure => "❓",
            ProcessingPhase.ParsingCaseStructure => "🔀",
            ProcessingPhase.ParsingLoop => "🔁",
            ProcessingPhase.EvaluatingExpression => "🧮",
            ProcessingPhase.CalculatingResources => "📊",
            ProcessingPhase.Completed => "✅",
            ProcessingPhase.Error => "❌",
            _ => "  "
        };

        Console.WriteLine($"    {phaseIcon} {update}");
    }

    /// <summary>
    /// Event handler for error events.
    /// </summary>
    static void OnErrorOccurred(object? sender, ProgressEventArgs e)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"    ❌ ERROR: {e.Update.Message}");
        if (e.Update.Detail != null)
            Console.WriteLine($"       Detail: {e.Update.Detail}");
        Console.ResetColor();
    }

    /// <summary>
    /// Gets an ASCII progress bar.
    /// </summary>
    static string GetProgressBar(float progress, int width = 20)
    {
        int filled = (int)(progress * width);
        int empty = width - filled;
        return $"{"█".PadRight(filled, '█')}{"░".PadRight(empty, '░')}";
    }

    /// <summary>
    /// Joins physical VHDL source lines that belong to one logical statement
    /// before they reach the line-by-line state machine in <see cref="ProcessVhdlCode"/>.
    /// </summary>
    /// <remarks>
    /// VHDL lacks an explicit continuation character — a statement is whatever
    /// runs from one terminator to the next, regardless of how many physical
    /// lines it spans. The downstream code uses string matching on individual
    /// lines (StartsWith("end "), Contains("function ... is"), etc.), so a
    /// statement broken across lines was silently mis-classified.
    ///
    /// A line is considered "complete" when:
    ///   • parenthesis depth has returned to 0, AND
    ///   • the trailing token is one of: <c>;</c>, <c>is</c>, <c>then</c>,
    ///     <c>else</c>, <c>elsif</c>, <c>loop</c>, <c>begin</c>,
    ///     <c>generate</c>, or <c>record</c>.
    /// Comments (<c>--</c> to EOL) are stripped before the completion check
    /// but preserved in the joined output so downstream tooling that reads
    /// them still works.
    /// </remarks>
    static string[] JoinContinuationLines(string[] rawLines)
    {
        var joined = new List<string>();
        var buffer = new System.Text.StringBuilder();
        int parenDepth = 0;

        foreach (var raw in rawLines)
        {
            string codeOnly = raw;
            int commentIdx = codeOnly.IndexOf("--", StringComparison.Ordinal);
            if (commentIdx >= 0) codeOnly = codeOnly[..commentIdx];
            string trimmed = codeOnly.TrimEnd();

            if (trimmed.Length == 0)
            {
                // Blank line or comment-only. Inside an open statement we skip
                // it entirely; otherwise pass it through so existing diagnostics
                // that read raw lines still see the comment.
                if (parenDepth > 0 && buffer.Length > 0) continue;
                joined.Add(raw);
                continue;
            }

            foreach (char c in codeOnly)
            {
                if (c == '(') parenDepth++;
                else if (c == ')') parenDepth = Math.Max(0, parenDepth - 1);
            }

            if (buffer.Length > 0) buffer.Append(' ');
            // Append the comment-stripped form, not the raw line. Otherwise an
            // inline `-- …` comment on one continued line (e.g. mid-port-list:
            //   dir : in STD_LOGIC;  -- '1' = up, '0' = down
            //   q   : out STD_LOGIC_VECTOR(3 downto 0)
            // );)
            // ends up inside the joined buffer; the tokenizer's
            // `line.Split("--")[0]` then deletes the rest of the joined
            // string — including subsequent ports and the closing `);` —
            // and the streamer reports the next `end entity …` as an
            // unexpected keyword. Using `codeOnly` keeps the comment out
            // of the buffer entirely.
            buffer.Append(codeOnly.Trim());

            bool complete = parenDepth == 0 && IsLineTerminator(trimmed);
            if (complete)
            {
                joined.Add(buffer.ToString());
                buffer.Clear();
            }
        }

        if (buffer.Length > 0) joined.Add(buffer.ToString());
        return joined.ToArray();
    }

    /// <summary>
    /// Whether <paramref name="trimmed"/> looks like the end of a VHDL
    /// statement (assuming all opening parens have been balanced).
    /// </summary>
    static bool IsLineTerminator(string trimmed)
    {
        if (trimmed.EndsWith(";", StringComparison.Ordinal)) return true;
        string lower = trimmed.ToLowerInvariant();
        if (lower.EndsWith(" is")       || lower == "is")       return true;
        if (lower.EndsWith(" then")     || lower == "then")     return true;
        if (lower.EndsWith(" else")     || lower == "else")     return true;
        if (lower.EndsWith(" elsif"))                           return true;
        if (lower.EndsWith(" loop")     || lower == "loop")     return true;
        if (lower.EndsWith(" begin")    || lower == "begin")    return true;
        if (lower.EndsWith(" generate"))                        return true;
        if (lower.EndsWith(" record"))                          return true;
        return false;
    }
}
