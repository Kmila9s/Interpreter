using Interpreter.Services;
using Parser.Models;
using Parser.Repositories;

namespace Interpreter;

class Program
{
    static void Main(string[] args)
    {
        string raw = @"
entity UART_TX is
    generic (
        CLK_FREQ : integer := 50_000_000;
        BAUD     : integer := 9600
    );
    port (
        Clk, Reset     : in std_logic;
        TxStart, asdf : in std_logic;
        Tx      : out std_logic;
        Busy    : out std_logic
    );
end entity UART_TX;
entity TrafficController is
    port (
        Clk   : in std_logic;
        Reset : in std_logic;
        Data    : in std_logic_vector(7 downto 0);
        R, Y, G : out std_logic
    );
end entity TrafficController;

architecture rtl of TrafficController is
begin
    Data <= '00000000';
    G <= '1';
    R <= '1';
    Reset <= '0';
    Y <= G and R XOr (R OR G AND (NOT Reset)) ;
end architecture rtl;


        ";
        Tokenizer tok = new("");
        var prev = raw.Split("\n").Where(p => !String.IsNullOrEmpty(p.Trim()) && p != " ").ToList().ToArray();
        List<Entity> Entities = new();
        List<string> rawEntity = new();
        int it = 0;
        string currentEntityArchName = string.Empty;
        foreach(var item in prev){
            string comparer = item.Trim().ToLower();
            if(comparer.StartsWith("entity")){
                it = 1;
            }else if(comparer.StartsWith("architecture")){
                currentEntityArchName = item.Split(' ')[3];
                it = 2;
            }
            if(it == 1){
                if(comparer.StartsWith("end entity")){
                    rawEntity.Add(item.Trim());
                    Entity entity = new(rawEntity.ToArray());
                    Entities.Add(entity);
                    rawEntity = new();
                    it = 0;
                }else
                    rawEntity.Add(item);
            }else if(it == 2){
                if(comparer.StartsWith("end architecture")){
                    rawEntity.Add(item);
                    Entity entity = Entities.FirstOrDefault(p => p.Name.Equals(currentEntityArchName)) ?? default!;
                    if(entity == null) throw new Exception("Entity not found");
                    Architecture arch = new(entity.Ports, entity.Generics);
                    arch.Syntetize(rawEntity.ToArray());
                    entity.SetArchitecture(arch);
                    it = 0;
                }else
                    rawEntity.Add(item);
            }
        }
        foreach(var entity in Entities){
            Console.WriteLine(entity);
            entity.Execute();
            Console.WriteLine(entity);
        }
    }
}
