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
        /*string operations = @"
            Sum <= A and B;,1
            Sum <= A or B;,1
            Sum <= A nand B;,0
            Sum <= A nor B;,0
            Sum <= not A;,0
            Sum <= A xor B;,0
            Sum <= A xnor B;,1
            Sum <= Cin and (A xor B);,0
            Sum <= (A and B) or (Cin and (A xor B));,1
            Sum <= A xor B xor Cin;,1
            Sum <= (A nand B) xor Cin;,1
            Sum <= not (A and B);,0
            Sum <= not (A xor B);,1
            Sum <= (A nor B) and Cin;,0
            Sum <= A and not (B or Cin);,0
        ";*/
        string operations = @"
            Sum <= Cin and (A xor B);,0
            Sum <= (A and B) or (Cin and (A xor B));,1
            Sum <= A xor B xor Cin;,1
            Sum <= (A nand B) xor Cin;,1
            Sum <= not (A and B);,0
            Sum <= not (A xor B);,1
            Sum <= (A nor B) and Cin;,0
            Sum <= A and not (B or Cin);,0
        ";
        var items = operations.Split("\n")
                    .Where(p => !String.IsNullOrEmpty(p.Trim()))
                    .ToList();
        foreach(var item in items){
            var splitted = item.Trim().Split(",");
            Operation operation= new(splitted[0], Variables);
            var result = operation.Execute() as Variable;
            Console.WriteLine($"Ecuation: {splitted[0]}\nSyntetized Result: {result?.Value} - {splitted[1]}");
        }
        
        
    }
}
