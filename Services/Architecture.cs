using System.Text;
using Interpreter.Interfaces;
using Interpreter.Models;
using Interpreter.Repositories;
using Parser.Models;
using Parser.Repositories;
using static Parser.Models.Constants.TYPE;
namespace Interpreter.Services;

/// <summary>
/// Represents a VHDL architecture and manages its behavioral synthesis and execution.
/// </summary>
/// <remarks>
/// <para>The Architecture class is responsible for:</para>
/// <list type="bullet">
/// <item>Parsing signal declarations from the architecture's declarative region</item>
/// <item>Synthesizing behavioral statements (processes, concurrent assignments, component instantiations)</item>
/// <item>Managing labeled statements (labeled processes, generate blocks)</item>
/// <item>Coordinating the execution of all architectural tasks</item>
/// </list>
/// <para><strong>v0.13.0 Improvements:</strong></para>
/// <list type="bullet">
/// <item>Added support for labeled component instantiation (label : ComponentName port map (...))</item>
/// <item>Enhanced labeled statement detection for process, for-generate, if-generate</item>
/// <item>Added SkipComponentInstantiation() for structural VHDL constructs</item>
/// </list>
/// </remarks>
public class Architecture : ISControl, IControl
{
    /// <summary>
    /// Gets the name of the architecture.
    /// </summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the name of the entity.
    /// </summary>
    public string EntityName { get; private set; } = string.Empty;
    private readonly HashSet<Variable> _ports = new(), _generics = new();
    private HashSet<Variable> _signals = new();

    /// <summary>
    /// Gets the list of tasks.
    /// </summary>
    public List<object> Tasks { get; private set; } = new();

    /// <summary>
    /// Gets the registry of custom type definitions for this architecture.
    /// </summary>
    public TypeRegistry CustomTypes { get; private set; } = new();

    private Tokenizer _tokenizer = new(string.Empty);
    private TokenStream _streamer = default!;
    private readonly OperationDetector _detector = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="Architecture"/> class.
    /// </summary>
    /// <param name="Ports">The ports of the entity.</param>
    /// <param name="Generics">The generics of the entity.</param>
    public Architecture(HashSet<Variable> Ports, HashSet<Variable> Generics)
    {
        _ports = Ports;
        _generics = Generics;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="Architecture"/> class.
    /// </summary>
    /// <param name="Name">The name of the architecture.</param>
    /// <param name="Ports">The ports of the entity.</param>
    /// <param name="Generics">The generics of the entity.</param>
    public Architecture(string Name, HashSet<Variable> Ports, HashSet<Variable> Generics)
    {
        this.Name = Name;
        _ports = Ports;
        _generics = Generics;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="Architecture"/> class.
    /// </summary>
    /// <param name="EntityName">The name of the entity.</param>
    /// <param name="Name">The name of the architecture.</param>
    /// <param name="Ports">The ports of the entity.</param>
    /// <param name="Generics">The generics of the entity.</param>
    public Architecture(string EntityName, string Name, HashSet<Variable> Ports, HashSet<Variable> Generics)
    {
        this.EntityName = EntityName;
        this.Name = Name;
        _ports = Ports;
        _generics = Generics;
    }

    /// <summary>
    /// Executes the architecture asynchronously.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task ExecuteAsync()
    {
        Executer executer = new(Tasks);
        await executer.ExecuteAsync();
    }

    /// <summary>
    /// Synthesizes the architecture.
    /// </summary>
    public void Syntetize() => throw new NotImplementedException();

    /// <summary>
    /// Synthesizes the architecture from raw data.
    /// </summary>
    /// <param name="RawArchitecture">The raw data of the architecture.</param>
    public void Syntetize(string[] RawArchitecture)
    {
        RawArchitecture.ToList().ForEach(_tokenizer.TokenizeLine);
        _tokenizer.Evaluate();
        _streamer = new(_tokenizer.GetDictionaryType());
        var tokens = _tokenizer.GetDictionaryType();
        _streamer.Expect(KEYWORDS);
        Name = _streamer.Current?.Value ?? string.Empty;
        _streamer.Expect(IDENTIFIERS);
        _streamer.Expect(KEYWORDS); // of
        EntityName = _streamer.Current?.Value ?? string.Empty;
        _streamer.Expect(IDENTIFIERS);
        _streamer.Expect(KEYWORDS); // is

        // Pass TypeRegistry to VariableSyntetizer for custom type parsing
        VariableSyntetizer syntetizer = new(_streamer, [.._ports, .._generics], IsSignal: true, TypeRegistry: CustomTypes);
        syntetizer.Syntetize();
        _signals = syntetizer.Variables;

        // Transfer any parsed types to our registry (in case syntetizer created new ones)
        foreach (var typeDef in syntetizer.ParsedTypes.Types.Values)
        {
            CustomTypes.TryRegister(typeDef);
        }

#if DEBUG
        if (CustomTypes.Count > 0)
        {
            Console.WriteLine($"[Architecture] {CustomTypes.GetSummary()}");
        }
#endif

        _streamer.Expect(KEYWORDS); // begin
        SyntetizeBehaviour();
    }

    /// <summary>
    /// Synthesizes the behavior of the architecture.
    /// </summary>
    public void SyntetizeBehaviour()
    {
        // v0.13.0 Fix: Move comparators list outside the loop to prevent repeated allocation
        // Previously created a new List<Variable> on every iteration, causing memory pressure
        HashSet<Variable> comparators = [.. _ports, .. _generics, .. _signals];

        while (_streamer.Current?.Value.ToLower() != "end")
        {
            // Check for labeled statements (label : process/for/if/etc or component instantiation)
            if (_streamer.Current?.Type == IDENTIFIERS)
            {
                _streamer.SetRollBack();
                _streamer.MoveNext();
                if (_streamer.Current?.Value == ":")
                {
                    _streamer.MoveNext(); // consume ':'
                    // Now check what follows the label
                    if (_streamer.Current?.Type == KEYWORDS)
                    {
                        string keyword = _streamer.Current.Value.ToLower();
                        switch (keyword)
                        {
                            case "process":
                                Process process = new(comparators, _streamer);
                                process.Syntetize();
                                Tasks.Add(process);
                                continue;
                            case "for":
                                // For generate - skip for now
                                SkipGenerateOrLoop();
                                continue;
                            case "if":
                                // If generate - skip for now
                                SkipGenerateOrLoop();
                                continue;
                            default:
                                _streamer.MoveNext();
                                continue;
                        }
                    }
                    else if (_streamer.Current?.Type == IDENTIFIERS)
                    {
                        // Component instantiation: label : ComponentName [generic map (...)] port map (...)
                        SkipComponentInstantiation();
                        continue;
                    }
                }
                else
                {
                    // Not a label, roll back
                    _streamer.RollBack();
                }
            }

            if (_streamer.Current?.Type == KEYWORDS)
            {
                // FOR COMPLEX BEHAVIOURS
                switch (_streamer.Current.Value.ToLower())
                {
                    case "process":
                        Process process = new(comparators, _streamer);
                        process.Syntetize();
                        Tasks.Add(process);
                        break;
                    case "if":
                        IfStructure ifStructure = new(comparators, _streamer);
                        ifStructure.Syntetize();
                        Tasks.Add(ifStructure);
                        // Consume 'end if ;' after the if structure
                        ConsumeEndIf();
                        break;
                    default:
                        // Skip unknown keywords
                        _streamer.MoveNext();
                        break;
                }
            }
            else
            {
                // FOR SIMPLE ASSIGNATIONS
                SyntetizeAssingations();
            }
        }
    }

    /// <summary>
    /// Skips a generate or loop construct (for now).
    /// </summary>
    private void SkipGenerateOrLoop()
    {
        int depth = 1;
        string startKeyword = _streamer.Current?.Value.ToLower() ?? "";
        _streamer.MoveNext(); // consume the starting keyword

        while (_streamer.Current != null && depth > 0)
        {
            string currentVal = _streamer.Current.Value.ToLower();
            if (currentVal == "for" || currentVal == "if" || currentVal == "process" || currentVal == "loop")
            {
                depth++;
            }
            else if (currentVal == "end")
            {
                _streamer.MoveNext(); // consume 'end'
                string endWhat = _streamer.Current?.Value.ToLower() ?? "";
                if (endWhat == "generate" || endWhat == "loop" || endWhat == "for" || endWhat == "if" || endWhat == "process")
                {
                    depth--;
                    _streamer.MoveNext(); // consume the end type
                    // Handle optional label after 'end generate label'
                    if (_streamer.Current?.Type == IDENTIFIERS)
                        _streamer.MoveNext();
                    if (_streamer.Current?.Value == ";")
                        _streamer.MoveNext();
                    continue;
                }
            }
            _streamer.MoveNext();
        }
    }

    /// <summary>
    /// Skips a component instantiation statement.
    /// Handles the pattern: label : ComponentName [generic map (...)] port map (...) ;
    /// Added in v0.13.0 to support component instantiation syntax.
    /// </summary>
    /// <remarks>
    /// Component instantiation connects an entity/component to signals in the architecture.
    /// For simulation purposes, we skip these declarations as the interpreter focuses on
    /// behavioral execution rather than structural hierarchy.
    /// </remarks>
    private void SkipComponentInstantiation()
    {
        // Current is at the ComponentName (IDENTIFIER)
        _streamer.MoveNext(); // consume component name

        // Skip until we find the semicolon that ends the instantiation
        // The pattern is: [generic map (...)] port map (...) ;
        // Track parenthesis depth to handle nested expressions in port maps
        int parenDepth = 0;
        while (_streamer.Current != null)
        {
            string currentVal = _streamer.Current.Value;
            if (currentVal == "(")
            {
                parenDepth++;
            }
            else if (currentVal == ")")
            {
                parenDepth--;
            }
            else if (currentVal == ";" && parenDepth == 0)
            {
                _streamer.MoveNext(); // consume ';'
                break;
            }
            _streamer.MoveNext();
        }
    }

    /// <summary>
    /// Consumes the 'end if ;' tokens after an if structure.
    /// </summary>
    private void ConsumeEndIf()
    {
        if (_streamer.Current?.Value.ToLower() == "end")
        {
            _streamer.MoveNext(); // consume 'end'
            if (_streamer.Current?.Value.ToLower() == "if")
            {
                _streamer.MoveNext(); // consume 'if'
            }
            if (_streamer.Current?.Value == ";")
            {
                _streamer.MoveNext(); // consume ';'
            }
        }
    }

    /// <summary>
    /// Synthesizes a process.
    /// </summary>
    public void SyntetizeProcess()
    {
        _streamer.Expect(KEYWORDS); // process
        if (_streamer.Current?.Value == "(")
        {
            
        }
    }

    /// <summary>
    /// Synthesizes the assignations.
    /// </summary>
    public void SyntetizeAssingations()
    {
        var rawDataBuilder = new StringBuilder();
        while (_streamer.Current?.Value != ";")
        {
            rawDataBuilder.Append(_streamer.Current?.Value + " ");
            _streamer.MoveNext();
        }
        string operation = rawDataBuilder.ToString().Trim();
        _streamer.MoveNext(); // ;
        var type = _detector.GetOperationType(operation);
        switch (type)
        {
            case OperationType.ASSIGNATION:
                Operation op;
                // NEEDS CHANGE UP FOR AVOIDING OR SILENT EXCEPTIONS
                // LIKE OUT OF INDEX ON VECTORS
                List<Variable> variables = [.. _ports, .. _signals, .. _generics];
                op = new(operation, variables.ToHashSet());
                Tasks.Add(op);
                break;
        }
    }

    /// <summary>
    /// Synthesizes the behavior of the architecture from raw data.
    /// </summary>
    /// <param name="Behaviour">The raw data of the behavior.</param>
    public void SyntetizeBehaviour(string[] Behaviour) => throw new NotImplementedException();
}