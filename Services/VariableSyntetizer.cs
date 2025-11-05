using Interpreter.Interfaces;
using Parser.Models;
using Parser.Repositories;
using static Parser.Models.Constants.TYPE;
namespace Interpreter.Services;

/// <summary>
/// Synthesizes variables from a token stream.
/// </summary>
public class VariableSyntetizer : ISControl
{
    /// <summary>
    /// Gets the synthesized variables.
    /// </summary>
    public HashSet<Variable> Variables { get; private set; } = new();
    private HashSet<Variable> _comparator;
    private readonly string _breaker = "begin";
    private bool _isPort, _isGeneric, _isSignal;
    private TokenStream _streamer;

    /// <summary>
    /// Initializes a new instance of the <see cref="VariableSyntetizer"/> class.
    /// </summary>
    /// <param name="Streamer">The token stream.</param>
    /// <param name="Comparator">The set of variables to compare against.</param>
    /// <param name="IsPort">Whether the variable is a port.</param>
    /// <param name="IsGeneric">Whether the variable is a generic.</param>
    /// <param name="Breaker">The token that breaks the synthesis.</param>
    /// <param name="IsSignal">Whether the variable is a signal.</param>
    public VariableSyntetizer(TokenStream Streamer, HashSet<Variable> Comparator, bool IsPort = false, bool IsGeneric = false, string Breaker = "begin", bool IsSignal = false)
    {
        _streamer = Streamer;
        _isPort = IsPort;
        _isSignal = IsSignal;
        _isGeneric = IsGeneric;
        _comparator = Comparator;
        _breaker = _isPort || _isGeneric ? ")" : Breaker.ToLower();
    }

    /// <summary>
    /// Executes the variable synthesizer asynchronously.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task ExecuteAsync() => throw new NotImplementedException();

    /// <summary>
    /// Synthesizes the variables from a token stream.
    /// </summary>
    /// <param name="strings">The token stream.</param>
    public void Syntetize(string[] strings) => ExecuteAsync();

    /// <summary>
    /// Synthesizes the variables from a token stream.
    /// </summary>
    public void Syntetize()
    {
        while (_breaker != _streamer.Current?.Value.ToLower())
        {
            List<Variable> variables = new();
            string varType = _streamer.Current?.Value ?? string.Empty;
            if (!_isPort && !_isGeneric)
                _streamer.Expect(KEYWORDS);
            while (_streamer.Current?.Value != ":")
            {
                string varName = _streamer.Current?.Value ?? string.Empty;
                _streamer.Expect(IDENTIFIERS);
                if (_streamer.Current?.Value == ",")
                    _streamer.MoveNext();
                if (Variables.Contains(varName))
                {
                    throw new ParserException(Constants.ParseExceptions.DUPLICATE_DECLARATION, (int)_streamer.Current?.Line + 1, (int)_streamer.Current?.Line + 1);
                }
                if (_comparator.Count > 0 && !_comparator.Contains(varName) && !_isPort && !_isSignal)
                {
                    throw new Exception($"Variable: {varName} not found in this scope - {_streamer.Current?.Line + 1} : {_streamer.Current?.Col + 1}");
                }
                variables.Add(new() { Name = varName });
            }
            _streamer.Expect(OPERATORS); // :
            Constants.PortType portType = _isPort || _isGeneric ? _streamer.Current?.Value.ToLower() == "in" ?
                Constants.PortType.IN : Constants.PortType.OUT
                    : varType.ToLower() == "constant" ?
                Constants.PortType.IN : Constants.PortType.OUT;
            if (_isPort)
                _streamer.Expect(KEYWORDS); // IN/OUT/INOUT Type
            var dataType = Variable.GetDatatype(_streamer.Current?.Value ?? string.Empty);
            _streamer.Expect(KEYWORDS);
            int VectorSize = 0;
            if (dataType == Constants.DATATYPES.STD_LOGIC_VECTOR || dataType == Constants.DATATYPES.RANGE)
            {
                if (dataType == Constants.DATATYPES.STD_LOGIC_VECTOR)
                {

                    // TODO
                    // Needs implementation for operation during vectors creations, like 'DATA_WIDTH - 1'
                    _streamer.Expect(PARENTHESIS);
                    object varValue = GetVariableValue();
                    int initial = Convert.ToInt32(varValue);
                    string vecType = _streamer.Current?.Value ?? string.Empty;
                    _streamer.Expect(KEYWORDS);
                    varValue = GetVariableValue();
                    int end = Convert.ToInt32(varValue);
                    _streamer.Expect(PARENTHESIS);
                    VectorSize = vecType == "downto" ? initial - end : end - initial;
                }
                else
                {
                    object varValue = GetVariableValue();
                    int initial = Convert.ToInt32(varValue);
                    _streamer.Expect(KEYWORDS);
                    varValue = GetVariableValue();
                    int end = Convert.ToInt32(varValue);
                    VectorSize = Math.Abs(end - initial);
                }
            }
            object value = new();
            if (!_isPort && _streamer.Current?.Value == ":=")
            {
                _streamer.Expect(OPERATORS);
                value = _streamer.Current?.Value ?? string.Empty;
                _streamer.Expect(LITERALS);
            }
            variables.ForEach(variable =>
            {
                Variables.Add(new()
                {
                    Name = variable.Name,
                    Type = dataType,
                    IsPort = _isPort,
                    PortType = portType,
                    Value = !_isPort ? value : null!,
                    Size = VectorSize,
                });
            });
            if ((_isPort || _isGeneric) && _streamer.Current?.Value != ";") break;
            _streamer.Expect(OPERATORS); // ;
        }
    }

    /// <summary>
    /// Gets the value of a variable.
    /// </summary>
    /// <returns>The value of the variable.</returns>
    private object GetVariableValue() {
        object varValue = null!;
        _streamer.SetRollBack();
        string rawOperation = string.Empty;
        int index = 0;
        while (_streamer.Current?.Value != "downto" && _streamer.Current?.Value != ")")
        {
            index++;
            rawOperation += $"{_streamer.Current?.Value} ";
            _streamer.MoveNext();
        }
        _streamer.RollBack();
        rawOperation = rawOperation.Trim();
        if (!String.IsNullOrEmpty(rawOperation))
        {
            string[] basicOps = ["*", "-", "+", "/"];
            if (basicOps.Any(rawOperation.Contains))
            {
                Operation op = new(rawOperation, _comparator);
                var outData = op.Execute();
                if (outData is Variable variable)
                {
                    outData = variable.Value;
                }
                varValue = outData;
                for (int i = 0; i < index; _streamer?.MoveNext(), i++) ;
                return varValue;
            }
        }
        if (_streamer.Current?.Type == IDENTIFIERS)
        {
            varValue = _comparator.FirstOrDefault(p => p.Name == _streamer.Current?.Value!)?.Value!;
            if (varValue == null)
            {
                throw new Exception($"Variable {_streamer.Current.Value} not found {_streamer.Current.Line + 1} : {_streamer.Current.Col + 1}");
            }
            _streamer.Expect(IDENTIFIERS);
        }
        else
        {
            varValue = _streamer.Current?.Value!;
            _streamer.Expect(LITERALS);
        }
        return varValue;
    }
}