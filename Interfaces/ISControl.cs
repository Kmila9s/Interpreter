namespace Interpreter.Interfaces;
public interface ISControl {
    Task ExecuteAsync();
    void Syntetize();
    void Syntetize(string[] RawData);
    
}