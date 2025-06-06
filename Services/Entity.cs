using System.Text.RegularExpressions;
using Interpreter.Interfaces;
using Parser.Models;
using Parser.Repositories;
using static Parser.Models.Constants.TYPE;

namespace Interpreter.Services;
public class Entity : ISControl, IControl{
    public string Name { get; private set; } = string.Empty;
    public HashSet<Variable> Ports { get; private set; } = new();
    public HashSet<Variable> Generics { get; private set; } = new();
    public Architecture Architecture { get; private set; } = default!;
    private Tokenizer _tokenizer = new(string.Empty);
    private TokenStream _streamer = default!;
    public Entity() { }
    public Entity(string[] RawEntity) { Syntetize(RawEntity); }
    public Entity(string Name) { this.Name = Name; }
    public void Execute() {
        if(Architecture != null){
            Architecture.Execute();
        }
    }
    public void Syntetize(){
        
    }
    public void SetArchitecture(Architecture architecture) => Architecture = architecture;
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
                    syntetizer = new(_streamer, new(), false, true);
                    syntetizer.Syntetize();
                    Generics = syntetizer.Variables;
                    break;
                case "port":
                    syntetizer = new(_streamer, new(), true, false);
                    syntetizer.Syntetize();
                    Ports = syntetizer.Variables;
                    break;
                default:
                    throw new NotImplementedException();
            }
            _streamer.Expect(PARENTHESIS);
            _streamer.Expect(OPERATORS); // ;
        }

    }public void SyntetizeBehaviour(string[] Behaviour){

    }
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