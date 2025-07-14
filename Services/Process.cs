using Interpreter.Interfaces;
using Interpreter.Repositories;
using Parser.Models;
using Parser.Repositories;
using static Parser.Models.Constants.TYPE;
namespace Interpreter.Services;

public class Process : ISControl, IControl
{
    public HashSet<Variable> SensitiveList { get; private set; } = new();
    public HashSet<Variable> SignalsPorts { get; private set; } = new();
    public List<object> Tasks { get; private set; } = new();
    private HashSet<Variable> _variables = new();
    private readonly TokenStream _streamer;
    public Process(HashSet<Variable> SignalsPorts, TokenStream Streamer)
    {
        _streamer = Streamer;
        this.SignalsPorts = SignalsPorts;
    }
    public void Execute()
    {
        Executer executer = new(Tasks);
        executer.Execute();
    }
    public void Syntetize()
    {
        VariableSyntetizer syntetizer = new(_streamer, SignalsPorts);
        SyntentizeSensitiveList();
        if (_streamer.Current?.Value != "begin")
        {
            syntetizer.Syntetize();
            _variables = syntetizer.Variables;
        }
        _streamer.Expect(KEYWORDS); // begin
        SyntetizeBehaviour();
    }
    public void Syntetize(string[] rawProcess) => throw new NotImplementedException();
    private void SyntentizeSensitiveList()
    {
        _streamer.Expect(KEYWORDS);
        if (_streamer.Current?.Value == "(")
        {
            _streamer.Expect(PARENTHESIS);
            while (_streamer.Current?.Value != ")")
            {
                string varName = _streamer.Current?.Value ?? string.Empty;
                _streamer.Expect(IDENTIFIERS);
                var variable = SignalsPorts.FirstOrDefault(p => p.Name.Equals(varName));
                if (variable is null)
                    throw new ParserException($"Variable: {varName} not declared in this scope");
                else
                {
                    SensitiveList.Add(variable);
                }
                if (_streamer.Current?.Value == ",")
                    _streamer.MoveNext();
            }
            _streamer.Expect(PARENTHESIS);
        }
    }
    public void SyntetizeBehaviour()
    {
        
    }
    public void SyntetizeBehaviour(string[] strings) => throw new NotImplementedException();
}