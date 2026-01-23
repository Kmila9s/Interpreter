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

        // v0.16.1: Pre-scan ALL test files for entities before processing any
        // This allows cross-file entity references to work correctly
        EntityRegistry.Instance.Clear();
        PackageRegistry.Instance.Clear();
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
}
