-- test_delta_chain.vhdl
-- Tests VHDL delta cycle semantics for chain of assignments
--
-- This test verifies that a chain of signal assignments (a<=b, b<=c, c<=a)
-- performs a proper circular shift using OLD values.
--
-- Expected behavior:
--   Initial: a = "00", b = "01", c = "10"
--   After process executes (all use OLD values):
--     a <= b uses OLD b = "01", so a becomes "01"
--     b <= c uses OLD c = "10", so b becomes "10"
--     c <= a uses OLD a = "00", so c becomes "00"
--   Result: a = "01", b = "10", c = "00" (circular shift right)

library IEEE;
use IEEE.STD_LOGIC_1164.ALL;

entity test_delta_chain is
    Port (
        clk     : in  STD_LOGIC;
        rotate  : in  STD_LOGIC;
        a_out   : out STD_LOGIC_VECTOR(1 downto 0);
        b_out   : out STD_LOGIC_VECTOR(1 downto 0);
        c_out   : out STD_LOGIC_VECTOR(1 downto 0)
    );
end test_delta_chain;

architecture Behavioral of test_delta_chain is
    signal a : STD_LOGIC_VECTOR(1 downto 0) := "00";
    signal b : STD_LOGIC_VECTOR(1 downto 0) := "01";
    signal c : STD_LOGIC_VECTOR(1 downto 0) := "10";
begin
    -- Concurrent outputs
    a_out <= a;
    b_out <= b;
    c_out <= c;

    -- Chain assignment process
    process(clk)
    begin
        if rising_edge(clk) then
            if rotate = '1' then
                -- Circular chain: a <- b <- c <- a
                -- All should use OLD values simultaneously
                a <= b;
                b <= c;
                c <= a;
            end if;
        end if;
    end process;

end Behavioral;
