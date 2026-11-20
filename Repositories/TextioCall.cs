using System.Runtime.CompilerServices;
using System.Text;
using Interpreter.Interfaces;
using Interpreter.Models;
using Interpreter.Services;
using Parser.Models;

namespace Interpreter.Repositories;

/// <summary>
/// <c>std.textio</c> (VHDL-2008 §16.4). Output: <c>write</c>, <c>hwrite</c> append to a
/// <c>line</c> variable and <c>writeline(output, l)</c> prints it on the same channel as
/// <c>report</c> (<see cref="ReportBus"/>), so the CLI, API and editor show it. Input
/// (G80): <c>readline(f, l)</c> takes the next line of a file opened for reading
/// (<see cref="TextioFiles"/>) and <c>read(l, v)</c> consumes the next value of
/// <c>v</c>'s type from it. The text of a <c>line</c> lives beside its variable.
/// </summary>
public sealed class TextioCall : ISControl
{
    /// <summary>Text accumulated in each <c>line</c> variable.</summary>
    private static readonly ConditionalWeakTable<Variable, StringBuilder> Lines = new();

    private readonly string _procedure;
    private readonly Variable _line;
    /// <summary>Text of a literal value (rendered once at analysis); null for an expression.</summary>
    private readonly string? _literal;
    /// <summary>The value expression (built once at analysis); null for a literal or <c>writeline</c>.</summary>
    private readonly Operation? _value;

    /// <summary>The file of <c>readline</c>; null otherwise.</summary>
    private readonly Variable? _file;
    /// <summary>The object <c>read</c> fills; null otherwise.</summary>
    private readonly AssignmentTarget? _target;
    private readonly int _sourceLine;

    private TextioCall(string procedure, Variable line, string? literal, Operation? value,
        Variable? file = null, AssignmentTarget? target = null, int sourceLine = 0)
    {
        _procedure = procedure;
        _line = line;
        _literal = literal;
        _value = value;
        _file = file;
        _target = target;
        _sourceLine = sourceLine;
    }

    /// <summary>Procedures handled here.</summary>
    public static bool Handles(string name) => name is "write" or "hwrite" or "writeline" or "readline" or "read";

    /// <summary>std.textio procedures Kmila does not simulate yet (reported, never dropped).</summary>
    public static bool IsUnsupported(string name) =>
        name is "hread" or "oread" or "owrite" or "bwrite" or "file_open" or "file_close";

    /// <summary>Builds the call from its arguments.</summary>
    /// <exception cref="ParserException">Unsupported file, missing or undeclared arguments.</exception>
    public static TextioCall Create(string name, SimpleStatement call, HashSet<Variable> scope)
    {
        var args = call.CallArguments();
        if (args.Count < 2 || args[0].Count == 0 || args[1].Count == 0)
            throw new ParserException($"'{name}' expects a line and a value", call.First);
        int line = call.First.SourceLine;
        if (name == "readline")
            return new TextioCall(name, LineVariable(args[1][0], scope), null, null, file: LineVariable(args[0][0], scope), sourceLine: line);
        if (name == "read")
        {
            string targetText = string.Join(' ', args[1].Select(t => t.Value));
            var target = AssignmentTarget.Resolve(targetText, scope) ?? throw ParserException.UndeclaredVariable(targetText, args[1][0]);
            return new TextioCall(name, LineVariable(args[0][0], scope), null, null, target: target, sourceLine: line);
        }
        if (name == "writeline")
        {
            if (!args[0][0].Value.Equals("output", StringComparison.OrdinalIgnoreCase))
                throw new ParserException("writeline is only supported on the standard 'output' file", args[0][0]);
            return new TextioCall(name, LineVariable(args[1][0], scope), null, null);
        }
        var value = args[1];
        if (value.Any(t => t.IsStringLiteral))
        {
            // string'("…") or "…" [& …]: rendered once by the shared report renderer.
            var literal = value.SkipWhile(t => !t.IsStringLiteral).Select(t => t.Value).ToList();
            if (literal.Count > 0 && literal[^1] == ")") literal.RemoveAt(literal.Count - 1);
            return new TextioCall(name, LineVariable(args[0][0], scope), ParseTrace.RenderReportTokens(literal, scope), null);
        }
        if (value.Count == 1 && value[0].Value is { Length: 3 } c && c[0] == '\'' && c[2] == '\'')
            return new TextioCall(name, LineVariable(args[0][0], scope), c[1].ToString(), null);
        return new TextioCall(name, LineVariable(args[0][0], scope), null,
            new Operation(string.Join(' ', value.Select(t => t.Value)), scope));
    }

    private static Variable LineVariable(Token name, HashSet<Variable> scope) =>
        scope.FirstOrDefault(v => v.Name.Equals(name.Value, StringComparison.OrdinalIgnoreCase))
        ?? throw ParserException.UndeclaredVariable(name.Value, name);

    /// <summary>Nothing to read: the call was parsed by <see cref="SimpleStatement"/>.</summary>
    public void Syntetize() { }

    /// <inheritdoc/>
    public Task ExecuteAsync()
    {
        var text = Lines.GetOrCreateValue(_line);
        if (_procedure == "readline")
        {
            text.Clear().Append(TextioFiles.ReadLine(_file!, _sourceLine));
            return Task.CompletedTask;
        }
        if (_procedure == "read")
        {
            string rest = text.ToString();
            object value = TextioFiles.Read(ref rest, _target!.Base, _sourceLine);
            text.Clear().Append(rest);
            _target.Assign(null, value, _sourceLine, immediate: true);
            return Task.CompletedTask;
        }
        if (_procedure == "writeline")
        {
            ReportBus.Instance.Emit(text.ToString());
            text.Clear();
        }
        else
        {
            text.Append(_literal ?? ParseTrace.FormatValue(_value!.Evaluate(), null,
                _procedure == "hwrite" ? "to_hstring" : "to_string"));
        }
        return Task.CompletedTask;
    }
}
