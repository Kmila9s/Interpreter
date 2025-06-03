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
    private readonly HashSet<Variable> _ports = new(), _generics = new(), _signals = new();    
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
        var item = _streamer.Current?.Value ?? string.Empty;
        while (!item.Equals("begin")) {
            switch (item.ToLower())
            {
                case "signal":
                    SyntetizeSignal();
                    break;
            }
        }
        _streamer.Expect(KEYWORDS); // begin
        SyntetizeBehaviour();
    }
    public void SyntetizeSignal()
    {
        List<Variable> localSignals = new();
        _streamer.Expect(KEYWORDS);
        string varName = _streamer.Current?.Value ?? string.Empty;
        while (varName != ":")
        {
            localSignals.Add(new()
            {
                Name = varName
            });
            _streamer.Expect(IDENTIFIERS);
            if (_streamer.Current?.Value == ",")
                _streamer.MoveNext();
            varName = _streamer.Current?.Value ?? string.Empty;
        }
        _streamer.Expect(OPERATORS); // :
        Constants.DATATYPES dataType = Variable.GetDatatype(_streamer.Current?.Value ?? string.Empty);
        _streamer.Expect(KEYWORDS);
        int VectorSize = 0;
        if (dataType == Constants.DATATYPES.STD_LOGIC_VECTOR || dataType == Constants.DATATYPES.RANGE)
        {
            if (dataType == Constants.DATATYPES.STD_LOGIC_VECTOR)
            {
                _streamer.Expect(PARENTHESIS);
                int initial = Convert.ToInt32(_streamer.Current?.Value);
                _streamer.Expect(LITERALS);
                string vecType = _streamer.Current?.Value ?? string.Empty;
                _streamer.Expect(KEYWORDS);
                int end = Convert.ToInt32(_streamer.Current?.Value);
                _streamer.Expect(LITERALS);
                _streamer.Expect(PARENTHESIS);
                VectorSize = vecType == "downto" ? initial - end : end - initial;
            }
            else
            {
                int initial = Convert.ToInt32(_streamer.Current?.Value);
                _streamer.Expect(LITERALS);
                _streamer.Expect(KEYWORDS);
                int end = Convert.ToInt32(_streamer.Current?.Value);
                _streamer.Expect(LITERALS);
                VectorSize = Math.Abs(end - initial);
            }
        }
        object value = new();
        if (_streamer.Current?.Value == ":=")
        {
            _streamer.Expect(OPERATORS);
            value = _streamer.Current?.Value ?? string.Empty;
            _streamer.Expect(LITERALS);
        }
        _streamer.Expect(OPERATORS); // ;
        localSignals.ForEach(p =>
        {
            _signals.Add(new()
            {
                Name = p.Name,
                Type = dataType,
                Size = VectorSize,
                IsPort = false,
                PortType = Constants.PortType.OUT,
                Value = value,
            });
        });
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
                        // NEEDS CHANGE UP FOR AVOIDING OR SILENT EXCEPTIONS
                        // LIKE OUT OF INDEX ON VECTORS
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