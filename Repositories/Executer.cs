using Interpreter.Interfaces;
using Interpreter.Services;

namespace Interpreter.Repositories;

public class Executer : ISControl
{
    private readonly List<object> Tasks = new();
    public Executer(List<object> tasks) => Tasks = tasks;
    public void Execute()
    {
        foreach (var task in Tasks)
        {
            if (task is Operation operation) operation.Execute();
            else if (task is ISControl sControl) sControl.Execute();
        }
    }
    public void Syntetize(string[] data) => Syntetize();
    public void Syntetize() => throw new NotImplementedException();
}