using Interpreter.Services;
using Parser.Models;
using Parser.Repositories;

namespace Interpreter;

class Program
{
    // Flag to control verbose progress output
    static bool _verboseProgress = false;

    // Cancellation token source for Ctrl+C handling
    static CancellationTokenSource _cts = new();

    static void Main(string[] args)
    {
        // Parse command line arguments
        _verboseProgress = args.Contains("--verbose") || args.Contains("-v");
        bool showProgressBar = args.Contains("--progress") || args.Contains("-p");

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

                string raw = File.ReadAllText(testFile);
                var result = ProcessVhdlCode(raw, _cts.Token);

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
                }
                passedTests++;
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

    static (List<Entity> entities, int architectureCount) ProcessVhdlCode(string raw, CancellationToken cancellationToken = default)
    {
        Tokenizer tok = new("");
        var prev = raw.Split("\n")
                      .Where(p => !String.IsNullOrEmpty(p.Trim()) && p != " ")
                      .ToList()
                      .ToArray();

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

        foreach (var item in prev)
        {
            // Check for cancellation periodically during parsing
            cancellationToken.ThrowIfCancellationRequested();
            string comparer = item.Trim().ToLower();

            // Skip package declarations and bodies
            if (comparer.StartsWith("package ") && !comparer.StartsWith("package body"))
            {
                inPackage = true;
                continue;
            }
            if (comparer.StartsWith("package body"))
            {
                inPackage = true;
                continue;
            }
            if (inPackage)
            {
                // Check for end of package
                if (comparer.StartsWith("end ") && comparer.EndsWith(";"))
                {
                    inPackage = false;
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

                // v0.13.0 Fix: Track function/procedure depth to avoid premature architecture end detection
                // When we see "function name(...) return type is" or "procedure name(...) is"
                // increment the depth counter
                if ((comparer.Contains("function ") && comparer.Contains(" is")) ||
                    (comparer.Contains("procedure ") && comparer.Contains(" is")))
                {
                    funcProcDepth++;
                }
                // When we see "end function;", "end procedure;", or "end <name>;" while inside a function/procedure
                // decrement the depth counter and don't check for architecture end yet
                else if (comparer.StartsWith("end ") && comparer.EndsWith(";"))
                {
                    var parts = comparer.Split(' ');
                    if (parts.Length >= 2)
                    {
                        var endName = parts[1].TrimEnd(';').ToLower();
                        // Explicit "end function;" or "end procedure;"
                        if (endName == "function" || endName == "procedure")
                        {
                            if (funcProcDepth > 0) funcProcDepth--;
                            continue;  // Don't check for architecture end
                        }
                        // "end <function_name>;" pattern when we're inside a function/procedure
                        if (funcProcDepth > 0)
                        {
                            funcProcDepth--;
                            continue;  // Don't check for architecture end
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
                        // Check if it's the architecture name (not a control structure)
                        var skipNames = new[] { "if", "process", "case", "loop", "for", "while", "generate", "function", "procedure" };
                        if (!skipNames.Contains(endName))
                        {
                            // Check if end matches either the architecture name OR the entity name
                            // Supports both: "end behavioral;" and "end example;"
                            if (endName == currentArchName.ToLower() ||
                                endName == currentEntityName.ToLower() ||
                                comparer.Contains(currentArchName.ToLower()) ||
                                comparer.Contains(currentEntityName.ToLower()))
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
}
