-- Test 21_2: Simple Counter Entity
-- This file is referenced by test21_1.vhdl via port map
-- Demonstrates sub-entity that gets discovered and linked

library IEEE;
use IEEE.std_logic_1164.all;
use IEEE.numeric_std.all;

-- =============================================================================
-- ENTITY: Simple Counter
-- A basic up-counter with enable and overflow detection
-- =============================================================================
entity simple_counter is
    generic (
        WIDTH : integer := 4
    );
    port (
        clk : in std_logic;
        rst : in std_logic;
        enable : in std_logic;
        count : out std_logic_vector(WIDTH - 1 downto 0);
        overflow : out std_logic
    );
end simple_counter;

architecture behavioral of simple_counter is
    signal count_reg : unsigned(WIDTH - 1 downto 0) := (others => '0');
    signal max_value : unsigned(WIDTH - 1 downto 0) := (others => '1');
begin

    -- Counter process
    counter_proc : process(clk, rst)
    begin
        if rst = '1' then
            count_reg <= (others => '0');
            overflow <= '0';
        elsif rising_edge(clk) then
            if enable = '1' then
                if count_reg = max_value then
                    count_reg <= (others => '0');
                    overflow <= '1';
                else
                    count_reg <= count_reg + 1;
                    overflow <= '0';
                end if;
            else
                overflow <= '0';
            end if;
        end if;
    end process counter_proc;

    -- Output assignment
    count <= std_logic_vector(count_reg);

end behavioral;
