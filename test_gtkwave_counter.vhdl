-- Test file for GTKWave waveform verification
-- Expected behavior: 4-bit counter that increments on rising clock edge
-- Reset sets counter to 0

library IEEE;
use IEEE.std_logic_1164.all;
use IEEE.numeric_std.all;

entity gtkwave_counter is
    port (
        clk     : in  std_logic;
        reset   : in  std_logic;
        enable  : in  std_logic;
        count   : out std_logic_vector(3 downto 0)
    );
end gtkwave_counter;

architecture Behavioral of gtkwave_counter is
    signal counter_reg : std_logic_vector(3 downto 0) := "0000";
begin
    process(clk)
    begin
        if rising_edge(clk) then
            if reset = '1' then
                counter_reg <= "0000";
            elsif enable = '1' then
                counter_reg <= std_logic_vector(unsigned(counter_reg) + 1);
            end if;
        end if;
    end process;

    count <= counter_reg;
end Behavioral;
