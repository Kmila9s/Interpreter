using System.Text;
using Interpreter.Interfaces;
using Interpreter.Models;
using Interpreter.Repositories;
using Parser.Models;
using Parser.Repositories;
using static Parser.Models.Constants.TYPE;
namespace Interpreter.Services;

public class Architecture : ISControl, IControl
{
    public string Name { get; private set; } = string.Empty;
    public string EntityName { get; private set; } = string.Empty;
    private readonly HashSet<Variable> _ports = new(), _generics = new();
    private HashSet<Variable> _signals = new();
    public List<object> Tasks { get; private set; } = new();
    private Tokenizer _tokenizer = new(string.Empty);
    private TokenStream _streamer = default!;
    private readonly OperationDetector _detector = new();
    public Architecture(HashSet<Variable> Ports, HashSet<Variable> Generics)
    {
        _ports = Ports;
        _generics = Generics;
    }
    public Architecture(string Name, HashSet<Variable> Ports, HashSet<Variable> Generics)
    {
        this.Name = Name;
        _ports = Ports;
        _generics = Generics;
    }
    public Architecture(string EntityName, string Name, HashSet<Variable> Ports, HashSet<Variable> Generics)
    {
        this.EntityName = EntityName;
        this.Name = Name;
        _ports = Ports;
        _generics = Generics;
    }

    public void Execute()
    {
        Executer executer = new(Tasks);
        executer.Execute();
    }
    public void Syntetize() => throw new NotImplementedException();
    public void Syntetize(string[] RawArchitecture)
    {
        RawArchitecture.ToList().ForEach(p => _tokenizer.TokenizeLine(p));
        _tokenizer.Evaluate();
        _streamer = new(_tokenizer.GetDictionaryType());
        var tokens = _tokenizer.GetDictionaryType();
        _streamer.Expect(KEYWORDS);
        Name = _streamer.Current?.Value ?? string.Empty;
        _streamer.Expect(IDENTIFIERS);
        _streamer.Expect(KEYWORDS); // off
        EntityName = _streamer.Current?.Value ?? string.Empty;
        _streamer.Expect(IDENTIFIERS);
        _streamer.Expect(KEYWORDS); // is
        VariableSyntetizer syntetizer = new(_streamer, new());
        syntetizer.Syntetize();
        _signals = syntetizer.Variables;
        _streamer.Expect(KEYWORDS); // begin
        SyntetizeBehaviour();
    }
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
    public void SyntetizeProcess()
    {
        _streamer.Expect(KEYWORDS); // process
        if (_streamer.Current?.Value == "(")
        {
            
        }
    }
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
    public void SyntetizeBehaviour(string[] Behaviour) => throw new NotImplementedException();
}