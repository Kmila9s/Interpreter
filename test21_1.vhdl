-- Test 21_1: Top-Level Hierarchical Design
-- Tests: Multi-file entity resolution, port map handling, component instantiation
-- This is the MAIN file that references entities from other files

library IEEE;
use IEEE.std_logic_1164.all;
use IEEE.numeric_std.all;

-- =============================================================================
-- TOP-LEVEL ENTITY: System Controller
-- Instantiates sub-entities from other files via port maps
-- =============================================================================
entity system_controller is
    generic (
        DATA_WIDTH : integer := 8;
        COUNTER_BITS : integer := 4
    );
    port (
        -- System signals
        clk : in std_logic;
        rst : in std_logic;
        -- Control inputs
        enable : in std_logic;
        load : in std_logic;
        data_in : in std_logic_vector(DATA_WIDTH - 1 downto 0);
        -- Outputs
        data_out : out std_logic_vector(DATA_WIDTH - 1 downto 0);
        counter_out : out std_logic_vector(COUNTER_BITS - 1 downto 0);
        overflow : out std_logic
    );
end system_controller;

architecture structural of system_controller is
    -- Internal signals for connecting sub-modules
    signal counter_value : std_logic_vector(COUNTER_BITS - 1 downto 0);
    signal counter_overflow : std_logic;
    signal reg_output : std_logic_vector(DATA_WIDTH - 1 downto 0);
    signal counter_enable : std_logic;

begin
    -- Counter enable logic
    counter_enable <= enable and not rst;

    -- ==========================================================================
    -- Component Instantiation: Counter (from test21_2.vhdl)
    -- Uses positional and named port mapping
    -- ==========================================================================
    counter_inst : entity work.simple_counter
        generic map (
            WIDTH => COUNTER_BITS
        )
        port map (
            clk => clk,
            rst => rst,
            enable => counter_enable,
            count => counter_value,
            overflow => counter_overflow
        );

    -- ==========================================================================
    -- Component Instantiation: Data Register (from test21_3.vhdl)
    -- ==========================================================================
    data_reg_inst : entity work.data_register
        generic map (
            WIDTH => DATA_WIDTH
        )
        port map (
            clk => clk,
            rst => rst,
            load => load,
            d => data_in,
            q => reg_output
        );

    -- Output assignments
    counter_out <= counter_value;
    overflow <= counter_overflow;
    data_out <= reg_output;

end structural;
