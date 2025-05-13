using System.Text.RegularExpressions;
using Interpreter.Interfaces;
using Parser.Models;
using Parser.Repositories;

namespace Interpreter.Services;
public class Entity : ISControl, IControl{
    public string Name { get; private set; } = string.Empty;
    public HashSet<Variable> Ports { get; private set; } = new();
    public HashSet<Variable> Generics { get; private set; } = new();
    public Architecture Architecture { get; private set; } = default!;
    private readonly Tokenizer _tokenizer = new(string.Empty);
    private readonly Regex _variableRegex = new(Constants.IDENTIFIERS, RegexOptions.Compiled);
    public Entity() { }
    public Entity(string[] RawEntity) { Syntetize(RawEntity); }
    public Entity(string Name) { this.Name = Name; }
    public void Execute(){
        
    }
    public void Syntetize(){
        
    }
    public void Syntetize(string[] RawEntity){
        int offset = 0;
        Name = RawEntity[offset++].Split(" ")[1];
        while(offset < RawEntity.Length){
            List<Variable> variables = new();
            string data = RawEntity[offset++].ToLower();
            if(data.Contains("port")){
                // PORTS SYNTETIZING
                string line = RawEntity[offset++];
                while(line.EndsWith(";")){
                    variables.AddRange(SyntetizePorts(_tokenizer.TokenizeLineReturn(line)));
                    line = RawEntity[offset++];
                }
                offset++;
                variables.AddRange(SyntetizePorts(_tokenizer.TokenizeLineReturn(line)));
                Ports = new(variables);
            }else if(data.Contains("generic")){
                string line = RawEntity[offset++];
                while(line.EndsWith(";")){
                    variables.AddRange(SyntetizeGenerics(_tokenizer.TokenizeLineReturn(line)));
                    line = RawEntity[offset++];
                }
                offset++;
                variables.AddRange(SyntetizeGenerics(_tokenizer.TokenizeLineReturn(line)));
                Generics = new(variables);
            }
        }
        
    }
    public List<Variable> SyntetizePorts(string[] items){
        List<Variable> variables = new();
        int portTypeIndex = items.ToList().IndexOf(":") + 1;
        int datatypeIndex = portTypeIndex + 1;
        var PortType = items[portTypeIndex].ToLower().Equals("in") ? Constants.PortType.IN : Constants.PortType.OUT;
        var DataType = Variable.GetDatatype(items[datatypeIndex]);
        items.TakeWhile(p => p != ":").Where(p => p != ",").ToList().ForEach(p => {
            Variable newVar = new(){
                Name = p,
                IsPort = true,
                PortType = PortType,
                Type = DataType
            };
            variables.Add(newVar);
        });
        return variables;
    }
    public List<Variable> SyntetizeGenerics(string[] items){
        List<Variable> variables = new();
        int datatypeIndex = items.ToList().IndexOf(":") + 1;        
        var DataType = Variable.GetDatatype(items[datatypeIndex]);
        int ValueIndex = items.ToList().IndexOf(":=") + 1;
        object Value = DataType == Constants.DATATYPES.INTEGER || DataType == Constants.DATATYPES.NATURAL ?
            Convert.ToInt32(items[ValueIndex].Replace("_", "")) : items[ValueIndex];
        items.TakeWhile(p => p != ":").Where(p => p != ",").ToList().ForEach(p => {
            Variable newVar = new(){
                Name = p,
                IsPort = false,
                Value = Value,
                Type = DataType
            };
            newVar.Size = DataType == Constants.DATATYPES.STD_LOGIC_VECTOR ? 
                items[datatypeIndex + 3].ToLower() == "downto" ? 
                    Convert.ToInt32(items[datatypeIndex + 2]) 
                    : Convert.ToInt32(items[datatypeIndex + 4]) 
                : 0;
            variables.Add(newVar);
        });
        return variables;
    }
    public void SyntetizeBehaviour(string[] Behaviour){

    }
    public override string ToString()
    {
        string info = $"Entity Name: {Name}\n";
        foreach(var variable in Ports){
            info += $"Port: {variable.Name}\nPort Type: {variable.PortType}\nValue: {variable.Value}\nType: {variable.Type}\n";
        }
        foreach(var variable in Generics){
            info += $"Generic: {variable.Name}\nGeneric Type: {variable.PortType}\nValue: {variable.Value}\nType: {variable.Type}\n";
        }
        return info;
    }
}