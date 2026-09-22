-- ============================================================================
-- VHDL Project: BitManipulator
-- Category: Digital Logic Fundamentals
-- Complexity: Low
-- Description: Bit manipulation operations - reverse, swap nibbles,
--              count ones (population count), parity calculation
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity BitManipulator is
    port (
        data_in     : in  bit_vector(7 downto 0);
        operation   : in  bit_vector(2 downto 0);
        data_out    : out bit_vector(7 downto 0);
        ones_count  : out bit_vector(3 downto 0);
        parity      : out bit
    );
end BitManipulator;

architecture Behavioral of BitManipulator is
    -- Operation codes
    -- "000" = Pass through
    -- "001" = Bit reverse
    -- "010" = Swap nibbles
    -- "011" = Rotate left
    -- "100" = Rotate right
    -- "101" = Invert all bits
    -- "110" = Set upper nibble to lower
    -- "111" = Set lower nibble to upper

    signal reversed     : bit_vector(7 downto 0);
    signal swapped      : bit_vector(7 downto 0);
    signal rotated_l    : bit_vector(7 downto 0);
    signal rotated_r    : bit_vector(7 downto 0);
    signal inverted     : bit_vector(7 downto 0);
    signal upper_to_both: bit_vector(7 downto 0);
    signal lower_to_both: bit_vector(7 downto 0);
    signal count_temp   : integer range 0 to 8;
    signal parity_temp  : bit;

begin
    -- Bit reverse operation
    reverse_gen: for i in 0 to 7 generate
        reversed(i) <= data_in(7 - i);
    end generate reverse_gen;

    -- Swap nibbles
    swapped(7 downto 4) <= data_in(3 downto 0);
    swapped(3 downto 0) <= data_in(7 downto 4);

    -- Rotate left by 1
    rotated_l(7 downto 1) <= data_in(6 downto 0);
    rotated_l(0) <= data_in(7);

    -- Rotate right by 1
    rotated_r(6 downto 0) <= data_in(7 downto 1);
    rotated_r(7) <= data_in(0);

    -- Invert all bits
    invert_gen: for i in 0 to 7 generate
        inverted(i) <= not data_in(i);
    end generate invert_gen;

    -- Upper nibble to both
    upper_to_both(7 downto 4) <= data_in(7 downto 4);
    upper_to_both(3 downto 0) <= data_in(7 downto 4);

    -- Lower nibble to both
    lower_to_both(7 downto 4) <= data_in(3 downto 0);
    lower_to_both(3 downto 0) <= data_in(3 downto 0);

    -- Population count (count ones)
    count_process: process(data_in)
        variable cnt : integer range 0 to 8;
    begin
        cnt := 0;
        for i in 0 to 7 loop
            if data_in(i) = '1' then
                cnt := cnt + 1;
            end if;
        end loop;
        count_temp <= cnt;
    end process count_process;

    -- Convert count to bit_vector
    ones_count(3) <= '1' when count_temp >= 8 else '0';
    ones_count(2) <= '1' when (count_temp = 4 or count_temp = 5 or
                               count_temp = 6 or count_temp = 7) else '0';
    ones_count(1) <= '1' when (count_temp = 2 or count_temp = 3 or
                               count_temp = 6 or count_temp = 7) else '0';
    ones_count(0) <= '1' when (count_temp = 1 or count_temp = 3 or
                               count_temp = 5 or count_temp = 7) else '0';

    -- Parity calculation (XOR of all bits)
    parity_temp <= data_in(0) xor data_in(1) xor data_in(2) xor data_in(3) xor
                   data_in(4) xor data_in(5) xor data_in(6) xor data_in(7);
    parity <= parity_temp;

    -- Output multiplexer based on operation
    output_mux: process(operation, data_in, reversed, swapped, rotated_l,
                        rotated_r, inverted, upper_to_both, lower_to_both)
    begin
        case operation is
            when "000" => data_out <= data_in;
            when "001" => data_out <= reversed;
            when "010" => data_out <= swapped;
            when "011" => data_out <= rotated_l;
            when "100" => data_out <= rotated_r;
            when "101" => data_out <= inverted;
            when "110" => data_out <= lower_to_both;
            when "111" => data_out <= upper_to_both;
            when others => data_out <= data_in;
        end case;
    end process output_mux;

end Behavioral;

-- ============================================================================
-- Testbench for BitManipulator
-- ============================================================================
entity BitManipulator_TB is
end BitManipulator_TB;

architecture Test of BitManipulator_TB is
    signal tb_data_in    : bit_vector(7 downto 0);
    signal tb_operation  : bit_vector(2 downto 0);
    signal tb_data_out   : bit_vector(7 downto 0);
    signal tb_ones_count : bit_vector(3 downto 0);
    signal tb_parity     : bit;
begin
    UUT: entity work.BitManipulator
        port map (
            data_in    => tb_data_in,
            operation  => tb_operation,
            data_out   => tb_data_out,
            ones_count => tb_ones_count,
            parity     => tb_parity
        );

    stimulus: process
    begin
        -- Test pattern: 10110100
        tb_data_in <= "10110100";

        -- Test all operations
        tb_operation <= "000"; wait for 10 ns;  -- Pass through
        tb_operation <= "001"; wait for 10 ns;  -- Reverse -> 00101101
        tb_operation <= "010"; wait for 10 ns;  -- Swap -> 01001011
        tb_operation <= "011"; wait for 10 ns;  -- Rotate L -> 01101001
        tb_operation <= "100"; wait for 10 ns;  -- Rotate R -> 01011010
        tb_operation <= "101"; wait for 10 ns;  -- Invert -> 01001011
        tb_operation <= "110"; wait for 10 ns;  -- Lower to both
        tb_operation <= "111"; wait for 10 ns;  -- Upper to both

        wait;
    end process stimulus;
end Test;
