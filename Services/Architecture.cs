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
        foreach (var task in Tasks)
        {
            if (task is Operation operation)
            {
                operation.Execute();
            }
        }
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
        string item = _streamer.Current?.Value ?? string.Empty;
        if (!item.ToLower().Equals("begin"))
        {
            // SIGNALS, ETC...
        }
        else
        {
            _streamer.Expect(KEYWORDS); // begin
            SyntetizeBehaviour();
        }
    }
    public void SyntetizeBehaviour()
    {
        while (_streamer.Current?.Value.ToLower() != "end")
        {
            if (_streamer.Current?.Type == KEYWORDS && _streamer.Current?.Value.ToLower() == "process")
            {

            }
            else
            {
                // FOR SIMPLE ASSIGNATIONS
                string operation = "";
                while (_streamer.Current?.Value.ToLower() != ";")
                {
                    operation += _streamer.Current?.Value + " ";
                    _streamer.MoveNext();
                }
                _streamer.MoveNext(); // ;
                operation = operation.Trim();
                var type = _detector.GetOperationType(operation);
                switch (type)
                {
                    case OperationType.ASSIGNATION:
                        Operation op;
                        try
                        {
                            op = new(operation, _ports);
                        }
                        catch
                        {
                            op = new(operation, _generics);
                        }
                        Tasks.Add(op);
                        break;
                }
            }
        }
    }
    public void SyntetizeBehaviour(string[] Behaviour) => throw new NotImplementedException();
}