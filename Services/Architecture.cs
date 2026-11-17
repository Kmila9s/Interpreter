using System.Text;
using Interpreter.Interfaces;
using Interpreter.Models;
using Interpreter.Repositories;
using Parser.Models;
using Parser.Repositories;
using Parser.Services;
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
public class Architecture : ISControl
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
    /// F-G-08 pass 5 (2026-07-22): pre-materialized union of
    /// <c>_ports ∪ _generics ∪ _signals</c>, populated at the start of
    /// <see cref="SyntetizeBehaviour(CancellationToken)"/> and reused by
    /// every <see cref="SyntetizeAssingations"/> call. Pre-pass-5 each
    /// assignment rebuilt this list + called <c>.ToHashSet()</c> on it,
    /// paying ~207 hashes per assignment × hundreds of assignments on the
    /// elaborated capstone. Null when synth hasn't started yet.
    /// </summary>
    private HashSet<Variable>? _assignmentVariables;

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

    private Tokenizer _tokenizer = new();
    private TokenStream _streamer = default!;
    private readonly OperationDetector _detector = new();

    /// <summary>
    /// Delta cycle engine for proper VHDL simulation semantics.
    /// </summary>
    private DeltaCycleEngine? _deltaEngine;

    /// <summary>
    /// Guard contexts for the enclosing <c>block</c> statements currently being
    /// synthesized, innermost on top. A <c>guarded</c> concurrent assignment is
    /// gated on the guard of the innermost enclosing guarded block (see
    /// <see cref="SyntetizeAssingationsInner"/>). Empty outside any block.
    /// </summary>
    private readonly Stack<BlockStatement> _blockStack = new();

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
    /// <param name="RawArchitectureWithLines">The raw architecture lines paired with their 1-based source-file line numbers.</param>
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

    /// <summary>
    /// Synthesizes the architecture from raw data lines, honoring cancellation.
    /// </summary>
    /// <param name="RawArchitecture">The raw data lines of the architecture.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
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
    /// <param name="cancellationToken">Cancellation token.</param>
    public void SyntetizeBehaviour(CancellationToken cancellationToken)
    {
        // precise-C10: publish THIS architecture's enum types (cleared + reloaded)
        // so CaseStructure can resolve a selector's enum type and its literals.
        // Done per architecture to avoid cross-architecture type-name leakage.
        EnumTypeRegistry.Instance.LoadFrom(CustomTypes);

        // v0.13.0 Fix: Move comparators list outside the loop to prevent repeated allocation
        // Previously created a new List<Variable> on every iteration, causing memory pressure
        HashSet<Variable> comparators = [.. _ports, .. _generics, .. _signals];
        // F-G-08 pass 5 (2026-07-22): stash the same set for reuse by
        // SyntetizeAssingations. Every concurrent assignment used to rebuild
        // this on-demand; on the elaborated capstone that was hundreds of
        // 207-item HashSet builds. `comparators` and `_assignmentVariables`
        // share the same backing HashSet — read-only downstream.
        _assignmentVariables = comparators;

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

            SyntetizeOneStatement(cancellationToken);
        }
    }

    /// <summary>
    /// Synthesizes exactly one concurrent statement at the current stream position
    /// and appends it to <see cref="Tasks"/>. Shared by <see cref="SyntetizeBehaviour(CancellationToken)"/>
    /// (the architecture body) and <see cref="HandleBlockStatement"/> (a block body),
    /// so both parse the same set of constructs identically — including nested blocks.
    /// The active scope is <see cref="_assignmentVariables"/> (== <c>comparators</c>,
    /// the shared backing set), which block-local signals are merged into so nested
    /// constructs see them.
    /// </summary>
    private void SyntetizeOneStatement(CancellationToken cancellationToken)
    {
        // Scope for every sub-construct. Set by SyntetizeBehaviour before the loop;
        // block-local signals are added to this same backing set as blocks open.
        HashSet<Variable> comparators = _assignmentVariables ?? [.. _ports, .. _generics, .. _signals];
        {
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
                                Process process = new(comparators, _streamer, CustomTypes);
                                process.Syntetize(cancellationToken);
                                Tasks.Add(process);
                                return;
                            case "block":
                                // VHDL block statement (guarded or plain, possibly nested).
                                HandleBlockStatement(label, cancellationToken);
                                return;
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
                                // A genuine validation rejection (missing ';', assign to an
                                // input port, type/width mismatch) must surface; only a
                                // synthesis-complexity failure is blackboxed.
                                catch (ValidationException) { throw; }
                                catch
                                {
                                    SkipGenerateBody(label);
                                }
                                return;
                            case "if":
                                // If-generate — same skip-on-failure policy as for-generate.
                                try
                                {
                                    GenerateBlock ifGen = new(comparators, _streamer, label);
                                    ifGen.Syntetize();
                                    Tasks.Add(ifGen);
                                }
                                // A genuine validation rejection must surface; only a
                                // synthesis-complexity failure is blackboxed.
                                catch (ValidationException) { throw; }
                                catch
                                {
                                    SkipGenerateBody(label);
                                }
                                return;
                            case "entity":
                                // Direct-entity instantiation with the VHDL-2008 form:
                                //   label : entity <lib>.<name>[(<arch>)] [generic map (...)] port map (...);
                                // Without this case, the `default:` swallowed the `entity`
                                // token; the streamer then landed on `work.<name>` and the
                                // subsequent SyntetizeAssingations call threw
                                // "Unexpected token 'generic' after 'work.X'" — audit F-G-05
                                // (session 15, 2026-07-12). ParseComponentInstantiation
                                // already handles the `entity` prefix (see else-if arm below
                                // that this switch used to bypass).
                                try
                                {
                                    var entityInstance = ParseComponentInstantiation(label);
                                    if (entityInstance != null)
                                    {
                                        EntityRegistry.Instance.RegisterComponentInstance(EntityName, entityInstance);
                                    }
                                }
                                catch
                                {
                                    SkipToSemicolon();
                                }
                                return;
                            default:
                                _streamer.MoveNext();
                                return;
                        }
                    }
                    else if (_streamer.Current?.Type == IDENTIFIERS)
                    {
                        // Traditional component instantiation:
                        //   label : ComponentName [generic map (...)] port map (...)
                        // The direct-entity form (`label : entity <lib>.<name> ...`) is
                        // handled by `case "entity":` in the KEYWORDS switch above.
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
                            SkipToSemicolon();
                        }
                        return;
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
                        Process process = new(comparators, _streamer, CustomTypes);
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
    /// Synthesizes a VHDL <c>block</c> statement at the current stream position
    /// (the cursor sits on the <c>block</c> keyword). The block is <em>flattened</em>
    /// into the enclosing architecture: its local signals are hoisted into the shared
    /// scope and its concurrent statements are appended to <see cref="Tasks"/>. A guard
    /// expression, if present, is captured and used to gate guarded assignments inside
    /// the block (see <see cref="SyntetizeAssingationsInner"/> and
    /// <see cref="BlockStatement.BuildGuardedRhs"/>). Nested blocks are handled by
    /// recursion through <see cref="SyntetizeOneStatement"/>.
    /// </summary>
    /// <remarks>
    /// On any failure the whole block body is skipped via <see cref="SkipBlockBody"/>
    /// so a block shape we can't fully synthesize degrades gracefully instead of
    /// aborting the architecture — mirroring the for/if-generate skip-on-failure policy.
    /// </remarks>
    private void HandleBlockStatement(string label, CancellationToken cancellationToken)
    {
        bool pushed = false;
        try
        {
            HashSet<Variable> scope = _assignmentVariables ?? [.. _ports, .. _generics, .. _signals];

            _streamer.Expect(KEYWORDS); // consume 'block'

            var block = new BlockStatement(label);

            // Optional guard expression: block (guard_expression)
            if (_streamer.Current?.Value == "(")
                block.GuardExpression = CaptureGuardExpression();

            // Local declarative part (signals/constants) before 'begin'. The
            // VariableSyntetizer stops at its default "begin" breaker. Hoist the
            // parsed signals into the architecture's signal set and the live scope
            // so the block body — and any nested constructs — resolve them.
            if (_streamer.Current != null && _streamer.Current.Value.ToLower() != "begin")
            {
                VariableSyntetizer blockSignals = new(_streamer, scope, IsSignal: true, TypeRegistry: CustomTypes);
                blockSignals.Syntetize(cancellationToken);
                foreach (var v in blockSignals.Variables)
                {
                    if (_signals.Add(v))
                        block.LocalSignals.Add(v);
                    scope.Add(v);
                }
                foreach (var typeDef in blockSignals.ParsedTypes.Types.Values)
                    CustomTypes.TryRegister(typeDef);
            }

            _streamer.Expect(KEYWORDS); // consume 'begin'

            _blockStack.Push(block);
            pushed = true;

            // Body: parse concurrent statements until the block's own 'end'.
            // Each sub-parser consumes its own 'end process/if/generate/block', so
            // the only bare 'end' left at this level is this block's 'end block'.
            int iterations = 0;
            const int MAX_ITERATIONS = 100000;
            while (_streamer.Current != null && _streamer.Current.Value.ToLower() != "end")
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++iterations > MAX_ITERATIONS)
                    throw new InvalidOperationException($"Block '{label}' parsing exceeded {MAX_ITERATIONS} iterations - possible infinite loop");
                SyntetizeOneStatement(cancellationToken);
            }

            _blockStack.Pop();
            pushed = false;

            ConsumeEndBlock(label);
        }
        catch (OperationCanceledException)
        {
            if (pushed) _blockStack.Pop();
            throw;
        }
        catch
        {
            if (pushed) _blockStack.Pop();
            SkipBlockBody(label);
        }
    }

    /// <summary>
    /// Captures a parenthesized guard expression (cursor on the opening <c>(</c>),
    /// returning the inner expression text and consuming through the matching
    /// <c>)</c>. Handles nested parentheses.
    /// </summary>
    private string CaptureGuardExpression()
    {
        var sb = new StringBuilder();
        int depth = 0;
        while (_streamer.Current != null)
        {
            string val = _streamer.Current.Value;
            if (val == "(")
            {
                depth++;
                _streamer.MoveNext();
                if (depth == 1) continue; // drop the outer '('
            }
            else if (val == ")")
            {
                depth--;
                _streamer.MoveNext();
                if (depth == 0) break;    // drop the outer ')'
                sb.Append(')').Append(' ');
                continue;
            }
            else
            {
                _streamer.MoveNext();
            }
            sb.Append(val).Append(' ');
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// Consumes <c>end block [label] ;</c> after a block body.
    /// </summary>
    private void ConsumeEndBlock(string label)
    {
        if (_streamer.Current?.Value.ToLower() == "end")
            _streamer.MoveNext();               // 'end'
        if (_streamer.Current?.Value.ToLower() == "block")
            _streamer.MoveNext();               // 'block'
        if (_streamer.Current?.Type == IDENTIFIERS && _streamer.Current?.Value == label)
            _streamer.MoveNext();               // optional trailing label
        if (_streamer.Current?.Value == ";")
            _streamer.MoveNext();               // ';'
    }

    /// <summary>
    /// Skips the body of a block whose synthesis failed, advancing the streamer past
    /// the matching <c>end block [label] ;</c>. Tracks nesting so a containing block
    /// isn't terminated by an inner block's end. Cursor may be anywhere inside the
    /// block header/body when this is called.
    /// </summary>
    private void SkipBlockBody(string label)
    {
        int depth = 1;
        while (_streamer.Current != null && depth > 0)
        {
            string val = _streamer.Current.Value.ToLower();
            // A nested `<id> : block ...` opens another block. Detect the `block`
            // keyword itself (its own `end block` will balance it below).
            if (val == "block" && _streamer.Previous?.Value?.ToLower() != "end")
            {
                depth++;
                _streamer.MoveNext();
                continue;
            }
            if (val == "end")
            {
                _streamer.MoveNext();
                if (_streamer.Current?.Value.ToLower() == "block")
                {
                    _streamer.MoveNext(); // 'block'
                    if (_streamer.Current?.Type == IDENTIFIERS)
                        _streamer.MoveNext(); // optional label
                    if (_streamer.Current?.Value == ";")
                        _streamer.MoveNext();
                    depth--;
                }
                continue;
            }
            _streamer.MoveNext();
        }
    }

    /// <summary>
    /// Returns the value a guarded driver drives while its guard is false
    /// (driver disconnected): <c>( others =&gt; 'Z' )</c> for a vector target,
    /// <c>'Z'</c> for a scalar. The target is looked up in the current scope by name.
    /// </summary>
    private string GuardedDisconnectValue(string targetName)
    {
        var target = (_assignmentVariables ?? (IEnumerable<Variable>)[.. _ports, .. _generics, .. _signals])
            .FirstOrDefault(v => v.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase));
        bool isVector = target is not null &&
                        (target.Type == Constants.DATATYPES.STD_LOGIC_VECTOR || target.Size > 1);
        return isVector ? "( others => 'Z' )" : "'Z'";
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
        // `end if ;` is required by VHDL — a missing terminator is a syntax
        // error, not something to tolerate silently.
        if (_streamer.Current?.Value?.ToLower() != "end")
            throw new ParserException(
                "'end if;' expected to close the if statement", _streamer.Current ?? _streamer.Previous);
        _streamer.MoveNext(); // consume 'end'
        if (_streamer.Current?.Value?.ToLower() != "if")
            throw new ParserException(
                "'if' expected after 'end'", _streamer.Current ?? _streamer.Previous);
        _streamer.MoveNext(); // consume 'if'
        if (_streamer.Current?.Value != ";")
            throw new ParserException(
                "';' expected after 'end if'", _streamer.Current ?? _streamer.Previous);
        _streamer.MoveNext(); // consume ';'
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
    /// Synthesizes the assignations.
    /// </summary>
    public void SyntetizeAssingations()
    {
        var _profileStopwatch = ParseProfile.Enabled ? System.Diagnostics.Stopwatch.StartNew() : null;
        try { SyntetizeAssingationsInner(); }
        finally
        {
            if (_profileStopwatch != null)
            {
                _profileStopwatch.Stop();
                ParseProfile.RecordSyntetizeAssignations(_profileStopwatch.ElapsedTicks);
            }
        }
    }

    private void SyntetizeAssingationsInner()
    {
        // Strict shape: a concurrent statement must look like
        //   <signal>{ ( <index> ) | .<field> } ( <= | := ) <expr> ;
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

        // Optional selectors: `bus(7 downto 0) <= ...`, `mem(i)(j) <= ...`,
        // `m(i).lo <= ...` — balanced parentheses and `.field` tokens.
        while (_streamer.Current?.Value == "(" || _streamer.Current?.IsFieldSelector == true)
        {
            if (_streamer.Current.IsFieldSelector)
            {
                rawDataBuilder.Append(_streamer.Current.Value).Append(' ');
                _streamer.MoveNext();
                continue;
            }
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

        // A concurrent statement's target is always a signal; ':=' (variable
        // assignment) is illegal here — VHDL requires '<='.
        if (isAssignment && sep.Value == ":=")
            throw new ParserException(
                $"':=' cannot assign to signal '{first.Value}' in a concurrent statement; use '<='",
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

        // Assignment. Snapshot the LHS (target + any index) before the operator so a
        // guarded RHS can be rewritten independently below. For non-guarded
        // assignments this reproduces the previous `"<lhs> <op> <rhs>"` string exactly.
        string lhs = rawDataBuilder.ToString().Trim();
        string assignOp = sep.Value; // "<=" or ":="
        _streamer.MoveNext();

        // VHDL guarded assignment: `target <= guarded <waveform>;`. The `guarded`
        // qualifier means the driver is active only while the enclosing guarded
        // block's guard is true. Strip the keyword here; the waveform is gated below.
        bool isGuarded = false;
        if (_streamer.Current?.Type == KEYWORDS &&
            _streamer.Current.Value.Equals("guarded", StringComparison.OrdinalIgnoreCase))
        {
            isGuarded = true;
            _streamer.MoveNext();
        }

        var rhsBuilder = new StringBuilder();
        int rhsParenDepth = 0;
        // True while inside a when…else CONDITION (between a depth-0 `when` and the
        // following depth-0 `else`/`;`), where a `<=` is a relational comparison and
        // must NOT be read as a missing-';' signal. Nested conditional expressions are
        // parenthesised (depth > 0), so they never toggle this.
        bool rhsInCondition = false;
        // Rolling RHS token history for the C11 stray-operator detector (lets it
        // walk back across an indexed/sliced next-target, e.g. `z(0) <=`).
        var rhsTokens = new List<Parser.Models.Token>();
        while (_streamer.Current?.Value != ";")
        {
            if (_streamer.Current == null)
                throw new ParserException(
                    $"Unexpected end of input in concurrent assignment for '{first.Value}' — missing ';'?",
                    _streamer.Previous);
            // Reject stray UNKNOWN tokens (e.g. `$`, mojibake from a paste)
            // inside the RHS — once they reach Operation.Evaluate they
            // become a confusing runtime error far from the source line.
            if (_streamer.Current.Type == UNKNOWN && !(_streamer.Current.IsFieldSelector && _streamer.Previous?.Value == ")"))
                throw new ParserException(
                    $"Unexpected token '{_streamer.Current.Value}' inside concurrent statement — not a valid VHDL identifier, keyword, operator, or literal",
                    _streamer.Current);

            var curTok = _streamer.Current;
            var cur = curTok.Value;
            if (cur == "(") rhsParenDepth++;
            else if (cur == ")") rhsParenDepth--;
            else if (rhsParenDepth == 0 && cur.Equals("when", StringComparison.OrdinalIgnoreCase)) rhsInCondition = true;
            else if (rhsParenDepth == 0 && cur.Equals("else", StringComparison.OrdinalIgnoreCase)) rhsInCondition = false;
            // C11 (v1.21.0, extended v1.23.0/v1.24.0): a stray assignment operator
            // at paren-depth 0 means the RHS ran past a missing ';' into the next
            // statement. The shared detector handles ':=' (always), a '<=' in a
            // waveform context, and — guarded — a '<=' that starts a new statement
            // inside a trailing when…else condition (operand-before-target).
            else if (ConcurrentAssignmentTermination.IsStrayAssignmentOperator(
                         curTok, rhsParenDepth, rhsInCondition, rhsTokens))
                throw new ValidationException(
                    $"';' expected to terminate the assignment to '{first.Value}' (found '{cur}' — missing ';'?)",
                    curTok);

            rhsBuilder.Append(cur).Append(' ');
            rhsTokens.Add(curTok);
            _streamer.MoveNext();
        }
        string rhs = rhsBuilder.ToString().Trim();

        // Gate a guarded assignment on its enclosing block's guard — genuine dynamic
        // guard simulation: the resulting conditional assignment re-evaluates the
        // guard on every delta cycle and disconnects the driver while it is false.
        if (isGuarded && assignOp == "<=" && _blockStack.Count > 0)
        {
            var guardCtx = _blockStack.Peek();
            if (guardCtx.IsGuarded)
                rhs = guardCtx.BuildGuardedRhs(rhs, GuardedDisconnectValue(first.Value));
        }

        string operation = $"{lhs} {assignOp} {rhs}".Trim();
        _streamer.MoveNext(); // ;
        var type = _detector.GetOperationType(operation);
        switch (type)
        {
            case OperationType.ASSIGNATION:
                // F-G-08 pass 5: reuse the HashSet stashed by SyntetizeBehaviour.
                // Fallback to on-demand build for any legacy caller that
                // invokes SyntetizeAssingations directly (defensive; the only
                // production caller is SyntetizeBehaviour, which sets the field).
                var variables = _assignmentVariables ?? [.. _ports, .. _signals, .. _generics];

                // Phase B+ Deep AST: capture the operation's 1-based source
                // line. Use the assignment-operator's line specifically — it
                // sits on the actual `<=` statement no matter how the RHS
                // wraps — falling back to the streamer cursor (post-`;`) only
                // when we couldn't find the `<=` token in the rawDataBuilder.
                var assignLine = _streamer.Current?.Line ?? 0;

                // Check for conditional assignment (when...else pattern)
                if (operation.Contains("when", StringComparison.OrdinalIgnoreCase))
                {
                    var condAssign = ConditionalAssignment.TryParse(operation, variables, assignLine);
                    if (condAssign != null)
                    {
                        Tasks.Add(condAssign);
                        break;
                    }
                }

                // Regular assignment
                Operation op = new(operation, variables, sourceLine: assignLine, isAssignmentStatement: true);
                Tasks.Add(op);
                break;
        }
    }

}