using System.Text.RegularExpressions;
using Interpreter.Interfaces;
using Parser.Models;
using Parser.Repositories;
using static Parser.Models.Constants.TYPE;

namespace Interpreter.Services;

/// <summary>
/// Represents a VHDL entity.
/// </summary>
public class Entity : ISControl, IControl{
    /// <summary>
    /// Gets the name of the entity.
    /// </summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the ports of the entity.
    /// </summary>
    public HashSet<Variable> Ports { get; private set; } = new();

    /// <summary>
    /// Gets the generics of the entity.
    /// </summary>
    public HashSet<Variable> Generics { get; private set; } = new();

    /// <summary>
    /// Gets the architecture of the entity.
    /// </summary>
    public Architecture Architecture { get; private set; } = default!;

    /// <summary>
    /// Gets the hardware resource estimation for this entity.
    /// Calculated after entity synthesis is complete.
    /// </summary>
    public Resources ResourceUsage { get; private set; } = default!;

    private Tokenizer _tokenizer = new(string.Empty);
    private TokenStream _streamer = default!;

    /// <summary>
    /// Initializes a new instance of the <see cref="Entity"/> class.
    /// </summary>
    public Entity() { }

    /// <summary>
    /// Initializes a new instance of the <see cref="Entity"/> class.
    /// </summary>
    /// <param name="RawEntity">The raw data of the entity.</param>
    public Entity(string[] RawEntity) { Syntetize(RawEntity); }

    /// <summary>
    /// Initializes a new instance of the <see cref="Entity"/> class.
    /// </summary>
    /// <param name="Name">The name of the entity.</param>
    public Entity(string Name) { this.Name = Name; }

    /// <summary>
    /// Executes the entity asynchronously.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task ExecuteAsync() {
        if(Architecture != null){
            await Architecture.ExecuteAsync();
        }
    }

    /// <summary>
    /// Synthesizes the entity.
    /// </summary>
    public void Syntetize(){
        
    }

    /// <summary>
    /// Sets the architecture of the entity and calculates resource usage.
    /// </summary>
    /// <param name="architecture">The architecture to set.</param>
    public void SetArchitecture(Architecture architecture)
    {
        var progress = ProgressReporter.Instance;
        Architecture = architecture;

        // Calculate hardware resource estimates
        progress.ReportPhase(0.5f, "Calculating resource usage", ProcessingPhase.CalculatingResources, Name);
        ResourceUsage = new Resources(this);
        ResourceUsage.Calculate();
        progress.ReportPhase(1f, $"Resources: FF={ResourceUsage.FlipFlops}, IO={ResourceUsage.IOPins}", ProcessingPhase.CalculatingResources, Name);
    }

    /// <summary>
    /// Synthesizes the entity from raw data.
    /// </summary>
    /// <param name="RawEntity">The raw data of the entity.</param>
    public void Syntetize(string[] RawEntity) => Syntetize(RawEntity, CancellationToken.None);

    /// <summary>
    /// Synthesizes the entity using lines that carry their original 1-based
    /// source-file line numbers. Diagnostics produced by the parser will
    /// reference those numbers instead of the chronological tokenize-call
    /// counter, which is what the user wanted to see.
    /// </summary>
    /// <param name="RawEntityWithLines">Pairs of (sourceLine, lineText).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public void Syntetize(IReadOnlyList<(int sourceLine, string text)> RawEntityWithLines,
                          CancellationToken cancellationToken = default)
    {
        var progress = ProgressReporter.Instance;
        progress.ReportPhase(0f, "Tokenizing entity", ProcessingPhase.Tokenizing);
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var (line, text) in RawEntityWithLines)
            _tokenizer.TokenizeLineAt(text, line);
        _tokenizer.Evaluate();
        _streamer = new(_tokenizer.GetDictionaryType());

        cancellationToken.ThrowIfCancellationRequested();
        progress.ReportPhase(0.3f, "Parsing entity declaration", ProcessingPhase.ParsingEntity);
        _streamer.Expect(KEYWORDS);
        Name = _streamer.Current?.Value ?? string.Empty;
        progress.ReportPhase(0.5f, "Found entity", ProcessingPhase.ParsingEntity, Name);
        _streamer.Expect(IDENTIFIERS);
        _streamer.Expect(KEYWORDS);
        SyntetizeBehaviour(cancellationToken);
        progress.ReportPhase(1f, "Entity parsed", ProcessingPhase.ParsingEntity, Name);
    }

    /// <summary>
    /// Synthesizes the entity from raw data.
    /// </summary>
    /// <param name="RawEntity">The raw data of the entity.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public void Syntetize(string[] RawEntity, CancellationToken cancellationToken)
    {
        var progress = ProgressReporter.Instance;
        progress.ReportPhase(0f, "Tokenizing entity", ProcessingPhase.Tokenizing);

        // Check for cancellation before starting
        cancellationToken.ThrowIfCancellationRequested();

        RawEntity.ToList().ForEach(_tokenizer.TokenizeLine);
        _tokenizer.Evaluate();
        _streamer = new(_tokenizer.GetDictionaryType());

        // Check for cancellation after tokenization
        cancellationToken.ThrowIfCancellationRequested();

        progress.ReportPhase(0.3f, "Parsing entity declaration", ProcessingPhase.ParsingEntity);
        _streamer.Expect(KEYWORDS);
        Name = _streamer.Current?.Value ?? string.Empty;

        progress.ReportPhase(0.5f, "Found entity", ProcessingPhase.ParsingEntity, Name);
        _streamer.Expect(IDENTIFIERS);
        _streamer.Expect(KEYWORDS);

        SyntetizeBehaviour(cancellationToken);
        progress.ReportPhase(1f, "Entity parsed", ProcessingPhase.ParsingEntity, Name);
    }

    /// <summary>
    /// Synthesizes the behavior of the entity.
    /// </summary>
    public void SyntetizeBehaviour() => SyntetizeBehaviour(CancellationToken.None);

    /// <summary>
    /// Synthesizes the behavior of the entity.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public void SyntetizeBehaviour(CancellationToken cancellationToken)
    {
        var progress = ProgressReporter.Instance;

        while (_streamer.Current?.Value.ToLower() != "end")
        {
            // Check for cancellation at each iteration
            cancellationToken.ThrowIfCancellationRequested();

            var type = _streamer.Current?.Value.ToLower();
            _streamer.Expect(KEYWORDS);
            _streamer.Expect(PARENTHESIS);
            VariableSyntetizer syntetizer;
            switch (type)
            {
                case "generic":
                    progress.ReportPhase(0.2f, "Parsing generics", ProcessingPhase.ParsingGenerics);
                    syntetizer = new(_streamer, Ports, false, true);
                    syntetizer.Syntetize(cancellationToken);
                    Generics = syntetizer.Variables;
                    progress.ReportPhase(1f, $"Parsed {Generics.Count} generic(s)", ProcessingPhase.ParsingGenerics);
                    break;
                case "port":
                    progress.ReportPhase(0.2f, "Parsing ports", ProcessingPhase.ParsingPorts);
                    syntetizer = new(_streamer, Generics, true, false);
                    syntetizer.Syntetize(cancellationToken);
                    Ports = syntetizer.Variables;
                    progress.ReportPhase(1f, $"Parsed {Ports.Count} port(s)", ProcessingPhase.ParsingPorts);
                    break;
                default:
                    throw new NotImplementedException();
            }
            _streamer.Expect(PARENTHESIS);
            _streamer.Expect(OPERATORS); // ;
        }

    }

    /// <summary>
    /// Synthesizes the behavior of the entity from raw data.
    /// </summary>
    /// <param name="Behaviour">The raw data of the behavior.</param>
    public void SyntetizeBehaviour(string[] Behaviour)
    {

    }

    /// <summary>
    /// Returns a string that represents the current object.
    /// </summary>
    /// <returns>A string that represents the current object.</returns>
    public override string ToString()
    {
        string info = $"Entity Name: {Name}\n";
        foreach (var variable in Ports)
        {
            info += $"Port: {variable.Name}\nPort Type: {variable.PortType}\nValue: {variable.Value}\nType: {variable.Type}\n";
        }
        foreach (var variable in Generics)
        {
            info += $"Generic: {variable.Name}\nGeneric Type: {variable.PortType}\nValue: {variable.Value}\nType: {variable.Type}\n";
        }
        return info;
    }
}