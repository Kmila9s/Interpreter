using Interpreter.Interfaces;
using Interpreter.Repositories;
using Parser.Models;
using Parser.Repositories;
using static Parser.Models.Constants.TYPE;
namespace Interpreter.Services;

/// <summary>
/// Represents a VHDL process.
/// </summary>
public class Process : ISControl, IControl
{
    /// <summary>
    /// Gets the sensitive list of the process.
    /// </summary>
    public HashSet<Variable> SensitiveList { get; private set; } = new();

    /// <summary>
    /// Gets the signals and ports of the process.
    /// </summary>
    public HashSet<Variable> SignalsPorts { get; private set; } = new();

    /// <summary>
    /// Gets the tasks of the process.
    /// </summary>
    public List<object> Tasks { get; private set; } = new();
    private HashSet<Variable> _variables = new();
    private readonly TokenStream _streamer;

    /// <summary>
    /// Initializes a new instance of the <see cref="Process"/> class.
    /// </summary>
    /// <param name="SignalsPorts">The signals and ports of the process.</param>
    /// <param name="Streamer">The token stream.</param>
    public Process(HashSet<Variable> SignalsPorts, TokenStream Streamer)
    {
        _streamer = Streamer;
        this.SignalsPorts = SignalsPorts;
    }

    /// <summary>
    /// Executes the process asynchronously.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task ExecuteAsync()
    {
        Executer executer = new(Tasks);
        await executer.ExecuteAsync();
    }

    /// <summary>
    /// Synthesizes the process.
    /// </summary>
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

    /// <summary>
    /// Synthesizes the process from raw data.
    /// </summary>
    /// <param name="rawProcess">The raw data of the process.</param>
    public void Syntetize(string[] rawProcess) => throw new NotImplementedException();

    /// <summary>
    /// Synthesizes the sensitive list of the process.
    /// </summary>
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
                    throw new ParserException($"Variable: {varName} not declared in this scope - {_streamer.Current?.Line + 1} : {_streamer.Current?.Col + 1}");
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

    /// <summary>
    /// Synthesizes the behavior of the process.
    /// </summary>
    public void SyntetizeBehaviour()
    {
        
    }

    /// <summary>
    /// Synthesizes the behavior of the process from raw data.
    /// </summary>
    /// <param name="strings">The raw data of the behavior.</param>
    public void SyntetizeBehaviour(string[] strings) => throw new NotImplementedException();
}