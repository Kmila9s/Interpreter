using Interpreter.Services;
using Parser.Models;
using Parser.Repositories;

namespace Interpreter;

class Program
{
    static void Main(string[] args)
    {
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

        foreach (var testFile in testFiles)
        {
            string fileName = Path.GetFileName(testFile);
            Console.WriteLine($"\n[TEST] Processing: {fileName}");
            Console.WriteLine(new string('-', 40));

            try
            {
                string raw = File.ReadAllText(testFile);
                var result = ProcessVhdlCode(raw);

                Console.WriteLine($"  Entities parsed: {result.entities.Count}");
                foreach (var entity in result.entities)
                {
                    Console.WriteLine($"    - {entity.Name}: {entity.Ports.Count} ports, {entity.Generics.Count} generics");
                    if (entity.Architecture != null)
                    {
                        Console.WriteLine($"      Architecture tasks: {entity.Architecture.Tasks.Count}");
                    }
                }

                Console.WriteLine($"  [PASS] {fileName} - Synthesized successfully!");
                passedTests++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [FAIL] {fileName}");
                Console.WriteLine($"    Error: {ex.Message}");
                Console.WriteLine($"    Stack: {ex.StackTrace}");
                failedTests++;
            }
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

    static (List<Entity> entities, int architectureCount) ProcessVhdlCode(string raw)
    {
        Tokenizer tok = new("");
        var prev = raw.Split("\n")
                      .Where(p => !String.IsNullOrEmpty(p.Trim()) && p != " ")
                      .ToList()
                      .ToArray();

        List<Entity> Entities = new();
        List<string> rawEntity = new();
        int it = 0;
        string currentEntityArchName = string.Empty;
        int architectureCount = 0;

        bool inPackage = false;
        bool inConfiguration = false;
        // v0.13.0 Fix: Track function/procedure nesting depth to prevent premature architecture end
        // Issue: 'end FunctionName;' was being confused with 'end ArchitectureName;'
        // Solution: Count function/procedure starts and ends, only check for architecture end when depth is 0
        int funcProcDepth = 0;

        foreach (var item in prev)
        {
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
                currentEntityArchName = item.Split(' ')[3];
                it = 2;
            }

            if (it == 1)
            {
                if (comparer.StartsWith("end ") && comparer.EndsWith(";"))
                {
                    rawEntity.Add(item.Trim());
                    Entity entity = new(rawEntity.ToArray());
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
                            // Also check if it matches the architecture name
                            string entityNameMatch = Entities.LastOrDefault()?.Name?.ToLower() ?? "";
                            if (endName == currentEntityArchName.ToLower() ||
                                comparer.Contains(currentEntityArchName.ToLower()) ||
                                endName == entityNameMatch ||
                                comparer.Contains(entityNameMatch))
                            {
                                isEndOfArchitecture = true;
                            }
                        }
                    }
                }

                if (isEndOfArchitecture)
                {
                    Entity entity = Entities.FirstOrDefault(p => p.Name.Equals(currentEntityArchName)) ?? default!;
                    if (entity == null) throw new Exception($"Entity '{currentEntityArchName}' not found");

                    Architecture arch = new(entity.Ports, entity.Generics);
                    arch.Syntetize(rawEntity.ToArray());
                    entity.SetArchitecture(arch);
                    architectureCount++;
                    rawEntity = new();
                    it = 0;
                }
            }
        }

        return (Entities, architectureCount);
    }
}
