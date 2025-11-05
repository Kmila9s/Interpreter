using System.Text;
using Interpreter.Interfaces;
using Interpreter.Models;
using Interpreter.Services;
using Parser.Models;
using Parser.Repositories;
using static Parser.Models.Constants.TYPE;

namespace Interpreter.Repositories;

/// <summary>
/// Represents and manages the logic for a VHDL 'if' statement.
/// It synthesizes the condition and the execution block, and executes it if the condition is met.
/// </summary>
public class IfStructure : ISControl
{
    private readonly HashSet<Variable> _variables = new();
    private readonly TokenStream _streamer;
    private readonly OperationDetector _detector = new();
    private Operation _baseCondition = default!;

    /// <summary>
    /// Gets the list of tasks or operations to be executed if the condition is true.
    /// </summary>
    public List<object> Tasks { get; private set; } = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="IfStructure"/> class.
    /// </summary>
    /// <param name="Variables">The set of variables available in the current scope.</param>
    /// <param name="Tokenizer">The token stream to read the if structure from.</param>
    public IfStructure(HashSet<Variable> Variables, TokenStream Tokenizer)
    {
        _variables = Variables;
        _streamer = Tokenizer;
    }

    /// <summary>
    /// Executes the if structure asynchronously. It evaluates the base condition and, if true,
    /// executes the tasks within the if block.
    /// </summary>
    public async Task ExecuteAsync()
    {
        var result = _baseCondition.Execute() as Variable;
        if (result?.Value is bool condition && condition)
        {
            Executer executer = new(Tasks);
            await executer.ExecuteAsync();
        }   
    }

    /// <summary>
    /// Synthesizes the if structure from the token stream. It parses the condition part (up to 'then')
    /// and then synthesizes the statements within the block until it finds an 'end' keyword.
    /// </summary>
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

    /// <summary>
    /// Synthesizes simple assignment statements within the if block. It reads tokens until a semicolon
    /// is found, determines the operation type, and adds it to the task list.
    /// </summary>
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

    /// <summary>
    /// Synthesizes the behavior from an array of strings. This method is not implemented.
    /// </summary>
    /// <param name="data">The raw data for synthesis.</param>
    public void Syntetize(string[] data) => throw new NotImplementedException();
}