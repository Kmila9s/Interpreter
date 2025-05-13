using Interpreter.Services;
using Parser.Models;

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
        Clk     : in std_logic;
        TxStart : in std_logic;
        Data    : in std_logic_vector(7 downto 0);
        Tx      : out std_logic;
        Busy    : out std_logic
    );
end entity UART_TX;
entity TrafficController is
    port (
        Clk   : in std_logic;
        Reset : in std_logic;
        R, Y, G : out std_logic
    );
end entity TrafficController;

architecture rtl of TrafficController is
begin
end architecture rtl;


        ";
        var prev = raw.Split("\n").Where(p => !String.IsNullOrEmpty(p.Trim()) && p != " ").ToList().ToArray();
        List<Entity> Entities = new();
        List<string> rawEntity = new();
        int it = 0;
        foreach(var item in prev){
            string comparer = item.Trim().ToLower();
            if(comparer.StartsWith("entity")){
                it = 1;
            }else if(comparer.StartsWith("architecture")){
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
                    Architecture arch = new(rawEntity.ToArray());
                    it = 0;
                }else
                    rawEntity.Add(item);
            }
            
        }
    }
}
