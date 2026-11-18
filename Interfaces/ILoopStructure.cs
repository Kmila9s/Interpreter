namespace Interpreter.Interfaces;

/// <summary>A sequential loop (<c>for</c>, <c>while</c>, plain <c>loop</c>): its label and body.</summary>
public interface ILoopStructure : ISControl
{
    /// <summary>The loop label (<c>lbl : for …</c>), or null; <c>exit</c>/<c>next</c> name it.</summary>
    string? LoopLabel { get; }

    /// <summary>The statements of the loop body.</summary>
    List<object> Tasks { get; }
}
