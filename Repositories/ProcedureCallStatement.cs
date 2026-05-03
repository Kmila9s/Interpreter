using System.Text;
using Interpreter.Interfaces;
using Interpreter.Models;
using Interpreter.Services;
using Parser.Models;
using Parser.Repositories;
using static Parser.Models.Constants.TYPE;

namespace Interpreter.Repositories;

/// <summary>
/// Represents and executes a VHDL procedure call statement.
/// </summary>
/// <remarks>
/// <para><strong>Supported VHDL Syntax:</strong></para>
/// <code>
/// procedure_name;                     -- No arguments
/// procedure_name(arg1, arg2);         -- Positional arguments
/// procedure_name(param1 => val1);     -- Named arguments
/// </code>
/// <para><strong>Execution:</strong></para>
/// <para>
/// Evaluates arguments, then calls FunctionExecutor.ExecuteProcedure() to execute
/// the procedure body. Out/InOut parameters are written back to caller's variables.
/// </para>
/// </remarks>
public class ProcedureCallStatement : ISControl
{
    /// <summary>The variables in scope during procedure argument evaluation.</summary>
    private readonly HashSet<Variable> _variables;
    /// <summary>The token stream used for parsing the procedure call, or null if constructed explicitly.</summary>
    private readonly TokenStream? _streamer;

    /// <summary>
    /// Gets the procedure name being called.
    /// </summary>
    public string ProcedureName { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the raw argument strings.
    /// </summary>
    public List<string> Arguments { get; private set; } = new();

    /// <summary>
    /// Gets whether this is a valid procedure call.
    /// </summary>
    public bool IsValid { get; private set; } = false;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProcedureCallStatement"/> class.
    /// </summary>
    /// <param name="variables">The variables in scope.</param>
    /// <param name="streamer">The token stream for parsing.</param>
    public ProcedureCallStatement(HashSet<Variable> variables, TokenStream streamer)
    {
        _variables = variables;
        _streamer = streamer;
    }

    /// <summary>
    /// Initializes a new instance with explicit values.
    /// </summary>
    /// <param name="variables">The variables in scope.</param>
    /// <param name="procedureName">The procedure name.</param>
    /// <param name="arguments">The argument strings.</param>
    public ProcedureCallStatement(HashSet<Variable> variables, string procedureName, List<string> arguments)
    {
        _variables = variables;
        _streamer = null;
        ProcedureName = procedureName;
        Arguments = arguments;
        IsValid = true;
    }

    /// <summary>
    /// Executes the procedure call asynchronously.
    /// </summary>
    public Task ExecuteAsync()
    {
        if (!IsValid || string.IsNullOrEmpty(ProcedureName))
            return Task.CompletedTask;

        // Evaluate arguments
        List<object> evaluatedArgs = new();
        foreach (var argStr in Arguments)
        {
            // Try to find the variable
            var variable = _variables.FirstOrDefault(v => v.Name.Equals(argStr.Trim(), StringComparison.OrdinalIgnoreCase));
            if (variable != null)
            {
                evaluatedArgs.Add(variable);
            }
            else if (int.TryParse(argStr.Trim(), out int intVal))
            {
                evaluatedArgs.Add(intVal);
            }
            else
            {
                // Try to evaluate as expression
                try
                {
                    Operation op = new(argStr.Trim(), _variables);
                    var result = op.Execute();
                    evaluatedArgs.Add(result);
                }
                catch
                {
                    evaluatedArgs.Add(argStr.Trim());
                }
            }
        }

        // Execute the procedure
        var executor = FunctionExecutor.Instance;
        executor.ExecuteProcedure(ProcedureName, evaluatedArgs.ToArray(), _variables);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Synthesizes the procedure call from the token stream.
    /// </summary>
    public void Syntetize()
    {
        if (_streamer == null) return;

        // Get procedure name
        if (_streamer.Current?.Type == IDENTIFIERS)
        {
            ProcedureName = _streamer.Current.Value;
            _streamer.MoveNext();
        }
        else
        {
            IsValid = false;
            return;
        }

        // Check for arguments in parentheses
        if (_streamer.Current?.Value == "(")
        {
            _streamer.MoveNext(); // consume '('

            var currentArg = new StringBuilder();
            int parenDepth = 1;

            while (_streamer.Current != null && parenDepth > 0)
            {
                string token = _streamer.Current.Value;

                if (token == "(")
                {
                    parenDepth++;
                    currentArg.Append(token);
                }
                else if (token == ")")
                {
                    parenDepth--;
                    if (parenDepth > 0)
                    {
                        currentArg.Append(token);
                    }
                }
                else if (token == "," && parenDepth == 1)
                {
                    // End of current argument
                    string arg = currentArg.ToString().Trim();
                    if (!string.IsNullOrEmpty(arg))
                    {
                        // Handle named association (param => value)
                        if (arg.Contains("=>"))
                        {
                            var parts = arg.Split("=>");
                            if (parts.Length == 2)
                            {
                                arg = parts[1].Trim();
                            }
                        }
                        Arguments.Add(arg);
                    }
                    currentArg.Clear();
                }
                else
                {
                    currentArg.Append(token + " ");
                }

                _streamer.MoveNext();
            }

            // Add last argument
            string lastArg = currentArg.ToString().Trim();
            if (!string.IsNullOrEmpty(lastArg))
            {
                if (lastArg.Contains("=>"))
                {
                    var parts = lastArg.Split("=>");
                    if (parts.Length == 2)
                    {
                        lastArg = parts[1].Trim();
                    }
                }
                Arguments.Add(lastArg);
            }
        }

        // Consume ';'
        if (_streamer.Current?.Value == ";")
        {
            _streamer.MoveNext();
        }

        // Validate - check if procedure exists
        var registry = PackageRegistry.Instance;
        IsValid = registry.ContainsProcedure(ProcedureName);

#if DEBUG
        if (!IsValid)
        {
            Console.WriteLine($"[ProcedureCallStatement] Warning: Procedure '{ProcedureName}' not found in registry");
        }
#endif
    }

    /// <summary>
    /// Not implemented - use Syntetize() instead.
    /// </summary>
    /// <param name="data">The raw data for synthesis (unused).</param>
    public void Syntetize(string[] data) => throw new NotImplementedException();

    /// <summary>
    /// Checks if a statement is a procedure call (identifier followed by '(' or ';').
    /// </summary>
    /// <param name="streamer">The token stream.</param>
    /// <param name="variables">The variables in scope.</param>
    /// <returns>True if this looks like a procedure call.</returns>
    public static bool IsProcedureCall(TokenStream streamer, HashSet<Variable> variables)
    {
        if (streamer.Current?.Type != IDENTIFIERS)
            return false;

        string name = streamer.Current.Value;

        // Check if it's a known procedure
        var registry = PackageRegistry.Instance;
        if (registry.ContainsProcedure(name))
            return true;

        // Check if it's NOT a known variable (then might be procedure)
        bool isVariable = variables.Any(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (isVariable)
            return false;

        // Look ahead for '(' to determine if it's a call
        // This is a heuristic - we can't be 100% sure without context
        return false; // Conservative - require procedure registration
    }
}
