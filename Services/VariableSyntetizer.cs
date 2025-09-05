using Interpreter.Interfaces;
using Parser.Models;
using Parser.Repositories;
using static Parser.Models.Constants.TYPE;
namespace Interpreter.Services;

public class VariableSyntetizer : ISControl
{
    public HashSet<Variable> Variables { get; private set; } = new();
    private HashSet<Variable> _comparator;
    private readonly string _breaker = "begin";
    private bool _isPort, _isGeneric, _isSignal;
    private TokenStream _streamer;
    public VariableSyntetizer(TokenStream Streamer, HashSet<Variable> Comparator, bool IsPort = false, bool IsGeneric = false, string Breaker = "begin", bool IsSignal = false)
    {
        _streamer = Streamer;
        _isPort = IsPort;
        _isSignal = IsSignal;
        _isGeneric = IsGeneric;
        _comparator = Comparator;
        _breaker = _isPort || _isGeneric ? ")" : Breaker.ToLower();
    }
    public async Task ExecuteAsync() => throw new NotImplementedException();
    public void Syntetize(string[] strings) => ExecuteAsync();
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
                    throw new ParserException(Constants.ParseExceptions.DUPLICATE_DECLARATION);
                }
                if (_comparator.Count > 0 && !_comparator.Contains(varName) && !_isPort && !_isSignal)
                {
                    throw new Exception($"Variable: {varName} not found in this scope");
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
                throw new Exception($"Variable not found at: {_streamer.Current.Line} - {_streamer.Current.Col}");
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