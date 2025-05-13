using Interpreter.Interfaces;
using Parser.Repositories;

namespace Interpreter.Services;
public class Architecture : ISControl, IControl{
    public string Name { get; private set; } = string.Empty;
    public string EntityName { get; private set; } = string.Empty;
    public List<object> Tasks { get; private set; } = new();
    private readonly Tokenizer _tokenizer = new(string.Empty);
    public Architecture() { }
    public Architecture(string Name) { this.Name = Name; }
    public Architecture(string EntityName, string Name) {
        this.EntityName = EntityName;
        this.Name = Name;
    }
    public Architecture(string[] RawArchitecture){ Syntetize(RawArchitecture); }

    public void Execute(){
        
    }
    public void Syntetize(){
        
    }
    public void Syntetize(string[] RawArchitecture){
        for(int lineOffset = 0; lineOffset < RawArchitecture.Length; lineOffset++){
            string raw = RawArchitecture[lineOffset];
            var line = _tokenizer.TokenizeLineReturn(raw);
            int offset = 0;
            if(line[offset++].ToLower().Equals("architecture")){
                Name = line[offset];
                offset += 2;
                EntityName = line[offset];
            }
            if(line.Last().Equals("begin")){
                raw = RawArchitecture[++lineOffset];
                List<string> rawBehaviour = new();
                while(!raw.ToLower().Contains("end architecture")){
                    raw = RawArchitecture[lineOffset++];
                    rawBehaviour.Add(raw);
                }
                SyntetizeBehaviour(rawBehaviour.ToArray());
            }
        }
    }
    public void SyntetizeBehaviour(string[] Behaviour){

    }
}