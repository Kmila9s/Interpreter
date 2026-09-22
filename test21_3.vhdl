-- Test 21_3: Data Register Entity
-- This file is referenced by test21_1.vhdl via port map
-- Demonstrates another sub-entity for hierarchical design

library IEEE;
use IEEE.std_logic_1164.all;

-- =============================================================================
-- ENTITY: Data Register
-- A simple loadable register with synchronous reset
-- =============================================================================
entity data_register is
    generic (
        WIDTH : integer := 8
    );
    port (
        clk : in std_logic;
        rst : in std_logic;
        load : in std_logic;
        d : in std_logic_vector(WIDTH - 1 downto 0);
        q : out std_logic_vector(WIDTH - 1 downto 0)
    );
end data_register;

architecture behavioral of data_register is
    signal reg_data : std_logic_vector(WIDTH - 1 downto 0) := (others => '0');
begin

    -- Register process
    reg_proc : process(clk, rst)
    begin
        if rst = '1' then
            reg_data <= (others => '0');
        elsif rising_edge(clk) then
            if load = '1' then
                reg_data <= d;
            end if;
        end if;
    end process reg_proc;

    -- Output assignment
    q <= reg_data;

end behavioral;
