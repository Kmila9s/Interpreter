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
    /// Gets the internal signals of the architecture (read-only access).
    /// </summary>
    public IReadOnlyCollection<Variable> Signals => _signals;

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
    /// Delta cycle engine for proper VHDL simulation semantics.
    /// </summary>
    private DeltaCycleEngine? _deltaEngine;

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
    /// Executes the architecture asynchronously using delta cycle simulation.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task ExecuteAsync()
    {
        InitializeDeltaEngine();

        // Connect to TimeMachine for time-driven simulation
        _deltaEngine!.ConnectToTimeMachine();

        // Run simulation (driven by TimeMachine ticks)
        // For now, run a single time step to maintain backward compatibility
        await _deltaEngine.RunTimeStepAsync();
    }

    /// <summary>
    /// Creates and initializes the delta cycle engine without connecting to TimeMachine.
    /// Used by SimulationRunner for direct engine control.
    /// </summary>
    /// <returns>The initialized DeltaCycleEngine.</returns>
    public DeltaCycleEngine InitializeDeltaEngine()
    {
        _deltaEngine = new DeltaCycleEngine();
        HashSet<Variable> allSignals = GetAllSignals();
        _deltaEngine.Initialize(allSignals, Tasks);
        return _deltaEngine;
    }

    /// <summary>
    /// Returns all signals in this architecture: ports, generics, and internal signals.
    /// </summary>
    public HashSet<Variable> GetAllSignals()
    {
        return [.. _ports, .. _generics, .. _signals];
    }

    /// <summary>
    /// Executes the architecture asynchronously using the legacy sequential executor.
    /// Use this method when delta cycle semantics are not required.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task ExecuteLegacyAsync()
    {
        Executer executer = new(Tasks);
        await executer.ExecuteAsync();
    }

    /// <summary>
    /// Gets the delta cycle engine for external control.
    /// </summary>
    public DeltaCycleEngine? DeltaEngine => _deltaEngine;

    /// <summary>
    /// Synthesizes the architecture.
    /// </summary>
    public void Syntetize() => throw new NotImplementedException();

    /// <summary>
    /// Synthesizes the architecture from raw data.
    /// </summary>
    /// <param name="RawArchitecture">The raw data of the architecture.</param>
    public void Syntetize(string[] RawArchitecture) => Syntetize(RawArchitecture, CancellationToken.None);

    /// <summary>
    /// Synthesizes the architecture from raw data.
    /// </summary>
    /// <param name="RawArchitecture">The raw data of the architecture.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <summary>
    /// Synthesizes the architecture using lines that carry their original
    /// 1-based source-file line numbers, so diagnostics reference the
    /// user's file rather than a re-indexed slice.
    /// </summary>
    public void Syntetize(IReadOnlyList<(int sourceLine, string text)> RawArchitectureWithLines,
                          CancellationToken cancellationToken = default)
    {
        var progress = ProgressReporter.Instance;
        progress.ReportPhase(0f, "Tokenizing architecture", ProcessingPhase.Tokenizing);
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var (line, text) in RawArchitectureWithLines)
            _tokenizer.TokenizeLineAt(text, line);
        _tokenizer.Evaluate();
        _streamer = new(_tokenizer.GetDictionaryType());
        var tokens = _tokenizer.GetDictionaryType();

        cancellationToken.ThrowIfCancellationRequested();
        progress.ReportPhase(0.2f, "Parsing architecture header", ProcessingPhase.ParsingArchitecture);
        _streamer.Expect(KEYWORDS);
        Name = _streamer.Current?.Value ?? string.Empty;
        _streamer.Expect(IDENTIFIERS);
        _streamer.Expect(KEYWORDS);
        EntityName = _streamer.Current?.Value ?? string.Empty;
        _streamer.Expect(IDENTIFIERS);
        _streamer.Expect(KEYWORDS);
        progress.ReportPhase(0.3f, "Found architecture", ProcessingPhase.ParsingArchitecture, $"{Name} of {EntityName}");
        progress.ReportPhase(0.4f, "Parsing signals and types", ProcessingPhase.ParsingSignals);
        VariableSyntetizer syntetizer = new(_streamer, [.._ports, .._generics], IsSignal: true, TypeRegistry: CustomTypes);
        syntetizer.Syntetize(cancellationToken);
        _signals = syntetizer.Variables;
        progress.ReportPhase(0.6f, $"Parsed {_signals.Count} signal(s)", ProcessingPhase.ParsingSignals);
        cancellationToken.ThrowIfCancellationRequested();
        if (syntetizer.ParsedTypes.Count > 0)
        {
            progress.ReportPhase(0.7f, $"Registering {syntetizer.ParsedTypes.Count} custom type(s)", ProcessingPhase.ParsingTypes);
            foreach (var typeDef in syntetizer.ParsedTypes.Types.Values)
                CustomTypes.TryRegister(typeDef);
        }
        _streamer.Expect(KEYWORDS); // begin
        progress.ReportPhase(0.8f, "Synthesizing behavior", ProcessingPhase.SynthesizingBehavior);
        SyntetizeBehaviour(cancellationToken);
        progress.ReportPhase(1f, $"Architecture complete ({Tasks.Count} tasks)", ProcessingPhase.ParsingArchitecture, Name);
    }

    public void Syntetize(string[] RawArchitecture, CancellationToken cancellationToken)
    {
        var progress = ProgressReporter.Instance;
        progress.ReportPhase(0f, "Tokenizing architecture", ProcessingPhase.Tokenizing);

        // Check for cancellation before starting
        cancellationToken.ThrowIfCancellationRequested();

        RawArchitecture.ToList().ForEach(_tokenizer.TokenizeLine);
        _tokenizer.Evaluate();
        _streamer = new(_tokenizer.GetDictionaryType());
        var tokens = _tokenizer.GetDictionaryType();

        // Check for cancellation after tokenization
        cancellationToken.ThrowIfCancellationRequested();

        progress.ReportPhase(0.2f, "Parsing architecture header", ProcessingPhase.ParsingArchitecture);
        _streamer.Expect(KEYWORDS);
        Name = _streamer.Current?.Value ?? string.Empty;
        _streamer.Expect(IDENTIFIERS);
        _streamer.Expect(KEYWORDS); // of
        EntityName = _streamer.Current?.Value ?? string.Empty;
        _streamer.Expect(IDENTIFIERS);
        _streamer.Expect(KEYWORDS); // is

        progress.ReportPhase(0.3f, "Found architecture", ProcessingPhase.ParsingArchitecture, $"{Name} of {EntityName}");

        // Pass TypeRegistry to VariableSyntetizer for custom type parsing
        progress.ReportPhase(0.4f, "Parsing signals and types", ProcessingPhase.ParsingSignals);
        VariableSyntetizer syntetizer = new(_streamer, [.._ports, .._generics], IsSignal: true, TypeRegistry: CustomTypes);
        syntetizer.Syntetize(cancellationToken);
        _signals = syntetizer.Variables;

        progress.ReportPhase(0.6f, $"Parsed {_signals.Count} signal(s)", ProcessingPhase.ParsingSignals);

        // Check for cancellation after signals
        cancellationToken.ThrowIfCancellationRequested();

        // Transfer any parsed types to our registry (in case syntetizer created new ones)
        if (syntetizer.ParsedTypes.Count > 0)
        {
            progress.ReportPhase(0.7f, $"Registering {syntetizer.ParsedTypes.Count} custom type(s)", ProcessingPhase.ParsingTypes);
            foreach (var typeDef in syntetizer.ParsedTypes.Types.Values)
            {
                CustomTypes.TryRegister(typeDef);
            }
        }

#if DEBUG
        if (CustomTypes.Count > 0)
        {
            Console.WriteLine($"[Architecture] {CustomTypes.GetSummary()}");
        }
#endif

        _streamer.Expect(KEYWORDS); // begin
        progress.ReportPhase(0.8f, "Synthesizing behavior", ProcessingPhase.SynthesizingBehavior);
        SyntetizeBehaviour(cancellationToken);
        progress.ReportPhase(1f, $"Architecture complete ({Tasks.Count} tasks)", ProcessingPhase.ParsingArchitecture, Name);
    }

    /// <summary>
    /// Synthesizes the behavior of the architecture.
    /// </summary>
    public void SyntetizeBehaviour() => SyntetizeBehaviour(CancellationToken.None);

    /// <summary>
    /// Synthesizes the behavior of the architecture.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public void SyntetizeBehaviour(CancellationToken cancellationToken)
    {
        // v0.13.0 Fix: Move comparators list outside the loop to prevent repeated allocation
        // Previously created a new List<Variable> on every iteration, causing memory pressure
        HashSet<Variable> comparators = [.. _ports, .. _generics, .. _signals];

        // v0.16.1: Add iteration limit to prevent infinite loops
        const int MAX_ITERATIONS = 100000;
        int iterations = 0;

        while (_streamer.Current != null && _streamer.Current.Value.ToLower() != "end")
        {
            // Check for cancellation at each iteration
            cancellationToken.ThrowIfCancellationRequested();

            // v0.16.1: Safety check for infinite loops
            if (++iterations > MAX_ITERATIONS)
            {
                throw new InvalidOperationException($"Architecture parsing exceeded {MAX_ITERATIONS} iterations - possible infinite loop");
            }

            // Check for labeled statements (label : process/for/if/etc or component instantiation)
            if (_streamer.Current?.Type == IDENTIFIERS)
            {
                // v0.16.0: Capture the label before moving past it
                string label = _streamer.Current.Value;
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
                                process.Syntetize(cancellationToken);
                                Tasks.Add(process);
                                continue;
                            case "for":
                                // For-generate. Nested or feature-rich generate blocks
                                // (multi-level nesting, embedded processes, with/select
                                // bodies) sometimes blow up the synthesizer; treat the
                                // failure as "blackbox this generate" and skip to its
                                // matching `end generate <label>;` so the rest of the
                                // architecture still gets synthesized.
                                try
                                {
                                    GenerateBlock forGen = new(comparators, _streamer, label);
                                    forGen.Syntetize();
                                    Tasks.Add(forGen);
                                }
                                catch
                                {
                                    SkipGenerateBody(label);
                                }
                                continue;
                            case "if":
                                // If-generate — same skip-on-failure policy as for-generate.
                                try
                                {
                                    GenerateBlock ifGen = new(comparators, _streamer, label);
                                    ifGen.Syntetize();
                                    Tasks.Add(ifGen);
                                }
                                catch
                                {
                                    SkipGenerateBody(label);
                                }
                                continue;
                            default:
                                _streamer.MoveNext();
                                continue;
                        }
                    }
                    else if (_streamer.Current?.Type == IDENTIFIERS ||
                             _streamer.Current?.Value?.ToLower() == "entity")
                    {
                        // Component instantiation: label : ComponentName [generic map (...)] port map (...)
                        // Or direct entity instantiation: label : entity library.EntityName [generic map (...)] port map (...)
                        // v0.16.0: Parse and register component instances for hierarchical design support
                        try
                        {
                            var instance = ParseComponentInstantiation(label);
                            if (instance != null)
                            {
                                EntityRegistry.Instance.RegisterComponentInstance(EntityName, instance);
                            }
                        }
                        catch
                        {
                            // Fall back to skipping the component instantiation
                            SkipToSemicolon();
                        }
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
                        process.Syntetize(cancellationToken);
                        Tasks.Add(process);
                        break;
                    case "if":
                        IfStructure ifStructure = new(comparators, _streamer);
                        ifStructure.Syntetize();
                        Tasks.Add(ifStructure);
                        // Consume 'end if ;' after the if structure
                        ConsumeEndIf();
                        break;
                    case "with":
                        // With...select statement
                        SelectedAssignment selectedAssign = new(comparators, _streamer);
                        selectedAssign.Syntetize();
                        Tasks.Add(selectedAssign);
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
    /// Skips the body of a generate block whose synthesis failed, advancing the
    /// streamer past the matching <c>end generate [label] ;</c>. Tracks nesting
    /// depth so a containing block isn't terminated by an inner block's end.
    /// </summary>
    private void SkipGenerateBody(string label)
    {
        int depth = 1;
        while (_streamer.Current != null && depth > 0)
        {
            string val = _streamer.Current.Value.ToLower();
            // Nested labeled `<id> : if|for ... generate` increments depth.
            // We don't know the iterator-vs-condition tail length, so detect
            // the trailing `generate` keyword instead of the leading `:`.
            if (val == "generate" && _streamer.Previous?.Value?.ToLower() != "end")
            {
                depth++;
                _streamer.MoveNext();
                continue;
            }
            if (val == "end")
            {
                _streamer.MoveNext();
                if (_streamer.Current?.Value.ToLower() == "generate")
                {
                    _streamer.MoveNext(); // 'generate'
                    if (_streamer.Current?.Type == IDENTIFIERS)
                        _streamer.MoveNext(); // optional label
                    if (_streamer.Current?.Value == ";")
                        _streamer.MoveNext();
                    depth--;
                    continue;
                }
                continue;
            }
            _streamer.MoveNext();
        }
    }

    /// <summary>
    /// Skips a generate or loop construct (for now).
    /// </summary>
    private void SkipGenerateOrLoop()
    {
        int depth = 1;
        string startKeyword = _streamer.Current?.Value?.ToLower() ?? "";
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
                string endWhat = _streamer.Current?.Value?.ToLower() ?? "";
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
        if (_streamer.Current?.Value?.ToLower() == "end")
        {
            _streamer.MoveNext(); // consume 'end'
            if (_streamer.Current?.Value?.ToLower() == "if")
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
    /// Parses a component instantiation and returns metadata about it.
    /// v0.16.0: Added to support hierarchical design discovery.
    /// </summary>
    private ComponentInstance? ParseComponentInstantiation(string instanceLabel)
    {
        var instance = new ComponentInstance { InstanceName = instanceLabel };

        // Check for 'entity' keyword (direct entity instantiation)
        if (_streamer.Current?.Value?.ToLower() == "entity")
        {
            _streamer.MoveNext(); // consume 'entity'

            // Parse library.entity format: work.EntityName or just EntityName
            string entityRef = _streamer.Current?.Value ?? string.Empty;
            _streamer.MoveNext();

            if (_streamer.Current?.Value == ".")
            {
                // Has library prefix
                instance.LibraryName = entityRef;
                _streamer.MoveNext(); // consume '.'
                instance.EntityName = _streamer.Current?.Value ?? string.Empty;
                _streamer.MoveNext();
            }
            else
            {
                instance.EntityName = entityRef;
            }

            // Check for architecture specification: (arch_name)
            if (_streamer.Current?.Value == "(")
            {
                _streamer.MoveNext(); // consume '('
                // Skip architecture name
                while (_streamer.Current != null && _streamer.Current.Value != ")")
                {
                    _streamer.MoveNext();
                }
                if (_streamer.Current?.Value == ")")
                    _streamer.MoveNext(); // consume ')'
            }
        }
        else
        {
            // Component name directly (uses component declaration)
            instance.EntityName = _streamer.Current?.Value ?? string.Empty;
            _streamer.MoveNext();
        }

        // Parse optional generic map
        if (_streamer.Current?.Value?.ToLower() == "generic")
        {
            _streamer.MoveNext(); // consume 'generic'
            if (_streamer.Current?.Value?.ToLower() == "map")
            {
                _streamer.MoveNext(); // consume 'map'
                ParseGenericMap(instance);
            }
        }

        // Parse port map
        if (_streamer.Current?.Value?.ToLower() == "port")
        {
            _streamer.MoveNext(); // consume 'port'
            if (_streamer.Current?.Value?.ToLower() == "map")
            {
                _streamer.MoveNext(); // consume 'map'
                ParsePortMap(instance);
            }
        }

        // Consume trailing semicolon
        if (_streamer.Current?.Value == ";")
        {
            _streamer.MoveNext();
        }

        return instance;
    }

    /// <summary>
    /// Parses a generic map: ( GenericName => Value, ... )
    /// </summary>
    private void ParseGenericMap(ComponentInstance instance)
    {
        if (_streamer.Current?.Value != "(")
            return;

        _streamer.MoveNext(); // consume '('

        int parenDepth = 1;
        string currentGeneric = string.Empty;
        string currentValue = string.Empty;
        bool afterArrow = false;

        while (_streamer.Current != null && parenDepth > 0)
        {
            string val = _streamer.Current.Value;

            if (val == "(")
            {
                parenDepth++;
                if (afterArrow)
                    currentValue += val + " ";
            }
            else if (val == ")")
            {
                parenDepth--;
                if (parenDepth > 0 && afterArrow)
                    currentValue += val + " ";
                else if (parenDepth == 0)
                {
                    // End of generic map - save current mapping
                    if (!string.IsNullOrEmpty(currentGeneric))
                    {
                        instance.GenericMap[currentGeneric.Trim()] = currentValue.Trim();
                    }
                }
            }
            else if (val == "=>")
            {
                afterArrow = true;
            }
            else if (val == "," && parenDepth == 1)
            {
                // Save current mapping and start new one
                if (!string.IsNullOrEmpty(currentGeneric))
                {
                    instance.GenericMap[currentGeneric.Trim()] = currentValue.Trim();
                }
                currentGeneric = string.Empty;
                currentValue = string.Empty;
                afterArrow = false;
            }
            else
            {
                if (afterArrow)
                    currentValue += val + " ";
                else
                    currentGeneric += val + " ";
            }

            _streamer.MoveNext();
        }
    }

    /// <summary>
    /// Parses a port map: ( PortName => SignalName, ... )
    /// </summary>
    private void ParsePortMap(ComponentInstance instance)
    {
        if (_streamer.Current?.Value != "(")
            return;

        _streamer.MoveNext(); // consume '('

        int parenDepth = 1;
        string currentPort = string.Empty;
        string currentSignal = string.Empty;
        bool afterArrow = false;

        while (_streamer.Current != null && parenDepth > 0)
        {
            string val = _streamer.Current.Value;

            if (val == "(")
            {
                parenDepth++;
                if (afterArrow)
                    currentSignal += val + " ";
            }
            else if (val == ")")
            {
                parenDepth--;
                if (parenDepth > 0 && afterArrow)
                    currentSignal += val + " ";
                else if (parenDepth == 0)
                {
                    // End of port map - save current mapping
                    if (!string.IsNullOrEmpty(currentPort))
                    {
                        instance.PortMap[currentPort.Trim()] = currentSignal.Trim();
                    }
                }
            }
            else if (val == "=>")
            {
                afterArrow = true;
            }
            else if (val == "," && parenDepth == 1)
            {
                // Save current mapping and start new one
                if (!string.IsNullOrEmpty(currentPort))
                {
                    instance.PortMap[currentPort.Trim()] = currentSignal.Trim();
                }
                currentPort = string.Empty;
                currentSignal = string.Empty;
                afterArrow = false;
            }
            else
            {
                if (afterArrow)
                    currentSignal += val + " ";
                else
                    currentPort += val + " ";
            }

            _streamer.MoveNext();
        }
    }

    /// <summary>
    /// Skips tokens until semicolon is found.
    /// </summary>
    private void SkipToSemicolon()
    {
        int parenDepth = 0;
        while (_streamer.Current != null)
        {
            if (_streamer.Current.Value == "(")
                parenDepth++;
            else if (_streamer.Current.Value == ")")
                parenDepth--;
            else if (_streamer.Current.Value == ";" && parenDepth == 0)
            {
                _streamer.MoveNext(); // consume ';'
                break;
            }
            _streamer.MoveNext();
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
        // Strict shape: a concurrent statement must look like
        //   <signal>[ ( <index> ) ] ( <= | := ) <expr> ;
        //   <procedure_name>[ ( <args> ) ] ;
        // Anything else (e.g. two identifiers in a row at depth 0 like the
        // `MHz bit2 <= q2` shape produced when garbage gets pasted after a
        // `;`) means the line is malformed. Catching it here surfaces a
        // precise Monaco marker via Editor.razor's ParserException catch
        // instead of building a quietly-different Operation that the
        // simulator silently mis-evaluates.

        var first = _streamer.Current;
        if (first == null)
            throw new ParserException(
                "Unexpected end of input where a concurrent statement was expected",
                _streamer.Previous);
        if (first.Type != IDENTIFIERS && first.Type != QUALIFIED_IDENTIFIERS)
            throw new ParserException(
                $"Unexpected token '{first.Value}' — expected a signal name to start a concurrent statement",
                first);

        var rawDataBuilder = new StringBuilder();
        rawDataBuilder.Append(first.Value).Append(' ');
        _streamer.MoveNext();

        // Optional indexed access: `bus(7 downto 0) <= ...`, `mem(i)(j) <= ...`.
        // Consume balanced parentheses immediately following the LHS.
        while (_streamer.Current?.Value == "(")
        {
            int depth = 0;
            do
            {
                if (_streamer.Current == null)
                    throw new ParserException(
                        "Unterminated parenthesized expression after signal name",
                        _streamer.Previous);
                if (_streamer.Current.Value == "(") depth++;
                else if (_streamer.Current.Value == ")") depth--;
                rawDataBuilder.Append(_streamer.Current.Value).Append(' ');
                _streamer.MoveNext();
            } while (depth > 0);
        }

        // Now the next token MUST be `<=`, `:=`, or `;` (bare procedure call).
        var sep = _streamer.Current;
        if (sep == null)
            throw new ParserException(
                $"Statement starting with '{first.Value}' is missing its assignment operator or terminator",
                _streamer.Previous);

        bool isProcedureCall = sep.Type == OPERATORS && sep.Value == ";";
        bool isAssignment    = sep.Type == OPERATORS && (sep.Value == "<=" || sep.Value == ":=");

        if (!isProcedureCall && !isAssignment)
            throw new ParserException(
                $"Unexpected token '{sep.Value}' after '{first.Value}' — expected '<=', ':=', or ';'",
                sep);

        if (isProcedureCall)
        {
            // Bare procedure call: just record the LHS so downstream
            // classifiers see the same operation string as before (no
            // assignment operator, no RHS). The existing detector treats
            // this as a no-op below.
            _streamer.MoveNext(); // consume ;
            string procOp = rawDataBuilder.ToString().Trim();
            _ = _detector.GetOperationType(procOp);
            return;
        }

        // Assignment — append the operator and slurp the RHS until `;`.
        rawDataBuilder.Append(sep.Value).Append(' ');
        _streamer.MoveNext();

        while (_streamer.Current?.Value != ";")
        {
            if (_streamer.Current == null)
                throw new ParserException(
                    $"Unexpected end of input in concurrent assignment for '{first.Value}' — missing ';'?",
                    _streamer.Previous);
            // Reject stray UNKNOWN tokens (e.g. `$`, mojibake from a paste)
            // inside the RHS — once they reach Operation.Evaluate they
            // become a confusing runtime error far from the source line.
            if (_streamer.Current.Type == UNKNOWN)
                throw new ParserException(
                    $"Unexpected token '{_streamer.Current.Value}' inside concurrent statement — not a valid VHDL identifier, keyword, operator, or literal",
                    _streamer.Current);
            rawDataBuilder.Append(_streamer.Current.Value).Append(' ');
            _streamer.MoveNext();
        }
        string operation = rawDataBuilder.ToString().Trim();
        _streamer.MoveNext(); // ;
        var type = _detector.GetOperationType(operation);
        switch (type)
        {
            case OperationType.ASSIGNATION:
                List<Variable> variables = [.. _ports, .. _signals, .. _generics];

                // Phase B+ Deep AST: capture the operation's 1-based source
                // line. Use the assignment-operator's line specifically — it
                // sits on the actual `<=` statement no matter how the RHS
                // wraps — falling back to the streamer cursor (post-`;`) only
                // when we couldn't find the `<=` token in the rawDataBuilder.
                var assignLine = _streamer.Current?.Line ?? 0;

                // Check for conditional assignment (when...else pattern)
                if (operation.Contains("when", StringComparison.OrdinalIgnoreCase))
                {
                    var condAssign = ConditionalAssignment.TryParse(operation, variables.ToHashSet());
                    if (condAssign != null)
                    {
                        Tasks.Add(condAssign);
                        break;
                    }
                }

                // Regular assignment
                Operation op = new(operation, variables.ToHashSet(), sourceLine: assignLine);
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