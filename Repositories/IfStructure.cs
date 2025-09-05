using System.Text;
using Interpreter.Interfaces;
using Interpreter.Models;
using Interpreter.Services;
using Parser.Models;
using Parser.Repositories;
using static Parser.Models.Constants.TYPE;

namespace Interpreter.Repositories;

public class IfStructure : ISControl
{
    private readonly HashSet<Variable> _variables = new();
    private readonly TokenStream _streamer;
    private readonly OperationDetector _detector = new();
    private Operation _baseCondition = default!;
    public List<object> Tasks { get; private set; } = new();
    public IfStructure(HashSet<Variable> Variables, TokenStream Tokenizer)
    {
        _variables = Variables;
        _streamer = Tokenizer;
    }
    public async Task ExecuteAsync()
    {
        var result = _baseCondition.Execute() as Variable;
        if (result?.Value is bool condition && condition)
        {
            Executer executer = new(Tasks);
            await executer.ExecuteAsync();
        }   
    }
    public void Syntetize()
    {
        _streamer.Expect(KEYWORDS);
        string rawData = string.Empty;
        while (_streamer.Current?.Value != "then")
        {
            rawData += _streamer.Current?.Value + " ";
            _streamer.MoveNext();
        }
        _streamer.Expect(KEYWORDS); // then
        _baseCondition = new(rawData, _variables);
        while (_streamer.Current?.Value.ToLower() != "end")
        {
            if (_streamer.Current?.Type == KEYWORDS)
            {
                // FOR COMPLEX BEHAVIOURS
                switch (_streamer.Current.Value.ToLower())
                {
                    case "if":
                    case "elsif":
                        IfStructure ifStructure = new(_variables, _streamer);
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
    public void SyntetizeAssingations()
    {
        var rawDataBuilder = new StringBuilder();
        while (_streamer.Current?.Value != ";")
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
                op = new(operation, _variables);
                Tasks.Add(op);
                break;
        }
    }
    public void Syntetize(string[] data) => throw new NotImplementedException();
}