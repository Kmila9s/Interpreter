using System.Text;
using Interpreter.Interfaces;
using Interpreter.Models;
using Interpreter.Repositories;
using Parser.Models;
using Parser.Repositories;
using static Parser.Models.Constants.TYPE;
namespace Interpreter.Services;

/// <summary>
/// Represents a VHDL architecture.
/// </summary>
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
        VariableSyntetizer syntetizer = new(_streamer, [.._ports, .._generics], IsSignal: true);
        syntetizer.Syntetize();
        _signals = syntetizer.Variables;
        _streamer.Expect(KEYWORDS); // begin
        SyntetizeBehaviour();
    }

    /// <summary>
    /// Synthesizes the behavior of the architecture.
    /// </summary>
    public void SyntetizeBehaviour()
    {
        while (_streamer.Current?.Value.ToLower() != "end")
        {
            if (_streamer.Current?.Type == KEYWORDS)
            {
                List<Variable> comparators = [.. _ports, .. _generics, .. _signals];
                // FOR COMPLEX BEHAVIOURS
                switch (_streamer.Current.Value.ToLower())
                {
                    case "process":
                        Process process = new(comparators.ToHashSet(), _streamer);
                        process.Syntetize();
                        Tasks.Add(process);
                    break;
                    case "if":
                        IfStructure ifStructure = new(comparators.ToHashSet(), _streamer);
                        ifStructure.Syntetize();
                        Tasks.Add(ifStructure);
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
        while (_streamer.Current?.Value != "then")
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