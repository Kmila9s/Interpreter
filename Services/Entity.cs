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
        Architecture = architecture;

        // Calculate hardware resource estimates
        ResourceUsage = new Resources(this);
        ResourceUsage.Calculate();
    }

    /// <summary>
    /// Synthesizes the entity from raw data.
    /// </summary>
    /// <param name="RawEntity">The raw data of the entity.</param>
    public void Syntetize(string[] RawEntity)
    {
        RawEntity.ToList().ForEach(_tokenizer.TokenizeLine);
        _tokenizer.Evaluate();
        _streamer = new(_tokenizer.GetDictionaryType());
        _streamer.Expect(KEYWORDS);
        Name = _streamer.Current?.Value ?? string.Empty;
        _streamer.Expect(IDENTIFIERS);
        _streamer.Expect(KEYWORDS);
        SyntetizeBehaviour();
    }

    /// <summary>
    /// Synthesizes the behavior of the entity.
    /// </summary>
    public void SyntetizeBehaviour()
    {
        while (_streamer.Current?.Value.ToLower() != "end")
        {
            var type = _streamer.Current?.Value.ToLower();
            _streamer.Expect(KEYWORDS);
            _streamer.Expect(PARENTHESIS);
            VariableSyntetizer syntetizer;
            switch (type)
            {
                case "generic":
                    syntetizer = new(_streamer, Ports, false, true);
                    syntetizer.Syntetize();
                    Generics = syntetizer.Variables;
                    break;
                case "port":
                    syntetizer = new(_streamer, Generics, true, false);
                    syntetizer.Syntetize();
                    Ports = syntetizer.Variables;
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