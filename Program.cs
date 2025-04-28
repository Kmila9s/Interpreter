using Interpreter.Services;
using Parser.Models;

namespace Interpreter;

class Program
{
    static void Main(string[] args)
    {
        HashSet<Variable> Variables = new(){
            new("A", '1', Constants.DATATYPES.STD_LOGIC),
            new("Sum", "U", Constants.DATATYPES.STD_LOGIC),
            new("B", '1', Constants.DATATYPES.STD_LOGIC),
            new("Cin", '1', Constants.DATATYPES.STD_LOGIC),
            new("Cout", "U", Constants.DATATYPES.STD_LOGIC),
        };
        Operation operation= new("Cout <= (A and B) or (Cin and (A xor B));", Variables);
        var result = operation.Execute();
        if(result is Variable var){
            Console.WriteLine(var.Value.ToString());
        }
        
    }
}
