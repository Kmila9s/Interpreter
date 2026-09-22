-- ============================================================================
-- VHDL Project: LogicUnit
-- Category: Digital Logic Fundamentals
-- Complexity: Low
-- Description: Implementation of all 16 boolean functions of 2 variables
--              Based on truth table enumeration
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity LogicUnit is
    port (
        A           : in  bit;
        B           : in  bit;
        func_sel    : in  bit_vector(3 downto 0);
        result      : out bit;
        all_funcs   : out bit_vector(15 downto 0)
    );
end LogicUnit;

architecture Behavioral of LogicUnit is
    -- All 16 boolean functions of 2 variables:
    -- F0  = 0           (FALSE)
    -- F1  = A AND B     (AND)
    -- F2  = A AND NOT B (A AND NOT B)
    -- F3  = A           (A)
    -- F4  = NOT A AND B (NOT A AND B)
    -- F5  = B           (B)
    -- F6  = A XOR B     (XOR)
    -- F7  = A OR B      (OR)
    -- F8  = A NOR B     (NOR)
    -- F9  = A XNOR B    (XNOR / Equivalence)
    -- F10 = NOT B       (NOT B)
    -- F11 = A OR NOT B  (A Implication)
    -- F12 = NOT A       (NOT A)
    -- F13 = NOT A OR B  (B Implication)
    -- F14 = A NAND B    (NAND)
    -- F15 = 1           (TRUE)

    signal f0, f1, f2, f3, f4, f5, f6, f7     : bit;
    signal f8, f9, f10, f11, f12, f13, f14, f15 : bit;
    signal selected_func : bit;

begin
    -- Compute all 16 functions
    f0  <= '0';                          -- FALSE
    f1  <= A and B;                      -- AND
    f2  <= A and (not B);                -- A AND NOT B
    f3  <= A;                            -- A
    f4  <= (not A) and B;                -- NOT A AND B
    f5  <= B;                            -- B
    f6  <= A xor B;                      -- XOR
    f7  <= A or B;                       -- OR
    f8  <= not (A or B);                 -- NOR
    f9  <= not (A xor B);                -- XNOR
    f10 <= not B;                        -- NOT B
    f11 <= A or (not B);                 -- A OR NOT B (A implies B complement)
    f12 <= not A;                        -- NOT A
    f13 <= (not A) or B;                 -- NOT A OR B (A implies B)
    f14 <= not (A and B);                -- NAND
    f15 <= '1';                          -- TRUE

    -- Output all functions as a vector
    all_funcs(0)  <= f0;
    all_funcs(1)  <= f1;
    all_funcs(2)  <= f2;
    all_funcs(3)  <= f3;
    all_funcs(4)  <= f4;
    all_funcs(5)  <= f5;
    all_funcs(6)  <= f6;
    all_funcs(7)  <= f7;
    all_funcs(8)  <= f8;
    all_funcs(9)  <= f9;
    all_funcs(10) <= f10;
    all_funcs(11) <= f11;
    all_funcs(12) <= f12;
    all_funcs(13) <= f13;
    all_funcs(14) <= f14;
    all_funcs(15) <= f15;

    -- Multiplexer for function selection
    select_func: process(func_sel, f0, f1, f2, f3, f4, f5, f6, f7,
                         f8, f9, f10, f11, f12, f13, f14, f15)
    begin
        case func_sel is
            when "0000" => selected_func <= f0;
            when "0001" => selected_func <= f1;
            when "0010" => selected_func <= f2;
            when "0011" => selected_func <= f3;
            when "0100" => selected_func <= f4;
            when "0101" => selected_func <= f5;
            when "0110" => selected_func <= f6;
            when "0111" => selected_func <= f7;
            when "1000" => selected_func <= f8;
            when "1001" => selected_func <= f9;
            when "1010" => selected_func <= f10;
            when "1011" => selected_func <= f11;
            when "1100" => selected_func <= f12;
            when "1101" => selected_func <= f13;
            when "1110" => selected_func <= f14;
            when "1111" => selected_func <= f15;
            when others => selected_func <= '0';
        end case;
    end process select_func;

    result <= selected_func;

end Behavioral;

-- ============================================================================
-- Extended Logic Unit with Multi-bit Support
-- ============================================================================
entity LogicUnit8Bit is
    port (
        A           : in  bit_vector(7 downto 0);
        B           : in  bit_vector(7 downto 0);
        func_sel    : in  bit_vector(3 downto 0);
        result      : out bit_vector(7 downto 0)
    );
end LogicUnit8Bit;

architecture Structural of LogicUnit8Bit is
begin
    -- Generate 8 instances of single-bit logic unit
    gen_bits: for i in 0 to 7 generate
        lu_inst: entity work.LogicUnit
            port map (
                A        => A(i),
                B        => B(i),
                func_sel => func_sel,
                result   => result(i),
                all_funcs => open
            );
    end generate gen_bits;
end Structural;

-- ============================================================================
-- Testbench
-- ============================================================================
entity LogicUnit_TB is
end LogicUnit_TB;

architecture Test of LogicUnit_TB is
    signal tb_A, tb_B    : bit;
    signal tb_func_sel   : bit_vector(3 downto 0);
    signal tb_result     : bit;
    signal tb_all_funcs  : bit_vector(15 downto 0);
begin
    UUT: entity work.LogicUnit
        port map (
            A         => tb_A,
            B         => tb_B,
            func_sel  => tb_func_sel,
            result    => tb_result,
            all_funcs => tb_all_funcs
        );

    stimulus: process
    begin
        -- Test all input combinations
        tb_A <= '0'; tb_B <= '0';
        for i in 0 to 15 loop
            tb_func_sel <= bit_vector'(
                '1' when i >= 8 else '0',
                '1' when (i mod 8) >= 4 else '0',
                '1' when (i mod 4) >= 2 else '0',
                '1' when (i mod 2) = 1 else '0'
            );
            wait for 5 ns;
        end loop;

        tb_A <= '0'; tb_B <= '1';
        for i in 0 to 15 loop
            tb_func_sel(3) <= '1' when i >= 8 else '0';
            tb_func_sel(2) <= '1' when (i mod 8) >= 4 else '0';
            tb_func_sel(1) <= '1' when (i mod 4) >= 2 else '0';
            tb_func_sel(0) <= '1' when (i mod 2) = 1 else '0';
            wait for 5 ns;
        end loop;

        tb_A <= '1'; tb_B <= '0';
        wait for 50 ns;

        tb_A <= '1'; tb_B <= '1';
        wait for 50 ns;

        wait;
    end process stimulus;
end Test;
