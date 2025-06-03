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
            switch (type)
            {
                case "generic":
                    Generics = SyntetizeGenerics().ToHashSet();
                    break;
                case "port":
                    Ports = SyntetizePorts().ToHashSet();
                    break;
                default:
                    _streamer.MoveNext();
                    break;
            }
        }

    }
    public List<Variable> SyntetizePorts()
    {
        List<Variable> ports = new();
        while (_streamer.Current?.Value != ")")
        {
            List<Variable> preType = new();
            while (_streamer.Current?.Value != ":")
            {
                string varName = _streamer.Current?.Value ?? string.Empty;
                _streamer.Expect(IDENTIFIERS);
                string item = _streamer.Current?.Value ?? string.Empty;
                if (item == ",")
                    _streamer.Expect(SEPARATORS);
                preType.Add(new(varName));

            }
            _streamer.Expect(OPERATORS);
            Constants.PortType type = _streamer.Current?.Value.ToLower() == "in" ?
                            Constants.PortType.IN : Constants.PortType.OUT;
            _streamer.Expect(KEYWORDS);
            var dataType = Variable.GetDatatype(_streamer.Current?.Value ?? string.Empty);
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
            
            ports.AddRange(
                preType.Select(
                    p => new Variable()
                    {
                        Name = p.Name,
                        Type = dataType,
                        IsPort = true,
                        Size = VectorSize,
                        PortType = type,
                        Value = null!
                    }
                )
            );
            var op = _streamer.Current?.Value;
            if (op != ")")
                _streamer.Expect(OPERATORS); // ;
        }
        _streamer.Expect(PARENTHESIS); // )
        _streamer.Expect(OPERATORS); // ;
        return ports;
    }
    public List<Variable> SyntetizeGenerics()
    {
        List<Variable> generics = new();
        while (_streamer.Current?.Value != ")")
        {
            List<Variable> preType = new();
            while (_streamer.Current?.Value != ":")
            {
                string varName = _streamer.Current?.Value ?? string.Empty;
                _streamer.Expect(IDENTIFIERS);
                string item = _streamer.Current?.Value ?? string.Empty;
                if (item == ",")
                    _streamer.Expect(SEPARATORS);
                preType.Add(new(varName));

            }
            _streamer.Expect(OPERATORS);
            var dataType = Variable.GetDatatype(_streamer.Current?.Value ?? string.Empty);
            if (dataType == Constants.DATATYPES.STD_LOGIC_VECTOR || dataType == Constants.DATATYPES.RANGE)
            {
                throw new NotImplementedException();
            }
            _streamer.Expect(KEYWORDS);
            _streamer.Expect(OPERATORS); // :=
            object value = dataType == Constants.DATATYPES.INTEGER || dataType == Constants.DATATYPES.NATURAL ?
                Convert.ToInt32(_streamer.Current?.Value.Replace("_", "") ?? "0") : _streamer.Current?.Value ?? "U";
            _streamer.Expect(LITERALS);
            generics.AddRange(
                preType.Select(
                    p => new Variable()
                    {
                        Name = p.Name,
                        Value = value,
                        Type = dataType,
                        IsPort = false
                    }
                )
            );
            var op = _streamer.Current?.Value;
            if (op != ")")
                _streamer.Expect(OPERATORS); // ;
        }
        _streamer.Expect(PARENTHESIS); // )
        _streamer.Expect(OPERATORS); // ;
        return generics;
    }
    public void SyntetizeBehaviour(string[] Behaviour){

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