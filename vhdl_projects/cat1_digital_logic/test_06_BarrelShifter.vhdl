-- ============================================================================
-- VHDL Project: BarrelShifter
-- Category: Digital Logic Fundamentals
-- Complexity: Medium
-- Description: 32-bit barrel shifter supporting:
--              - Logical shift left/right
--              - Arithmetic shift right
--              - Rotate left/right
--              Variable shift amount (0-31)
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity BarrelShifter is
    port (
        data_in     : in  bit_vector(31 downto 0);
        shift_amt   : in  bit_vector(4 downto 0);   -- 0 to 31
        operation   : in  bit_vector(2 downto 0);
        -- "000" = Logical shift left
        -- "001" = Logical shift right
        -- "010" = Arithmetic shift right
        -- "011" = Rotate left
        -- "100" = Rotate right
        data_out    : out bit_vector(31 downto 0);
        carry_out   : out bit  -- Last bit shifted out
    );
end BarrelShifter;

architecture Behavioral of BarrelShifter is
    -- Intermediate stages for logarithmic shifter
    signal stage0   : bit_vector(31 downto 0);  -- Shift by 1 or 0
    signal stage1   : bit_vector(31 downto 0);  -- Shift by 2 or 0
    signal stage2   : bit_vector(31 downto 0);  -- Shift by 4 or 0
    signal stage3   : bit_vector(31 downto 0);  -- Shift by 8 or 0
    signal stage4   : bit_vector(31 downto 0);  -- Shift by 16 or 0

    -- Fill bit for arithmetic shift
    signal fill_bit : bit;

    -- Direction signals
    signal shift_left  : bit;
    signal shift_right : bit;
    signal rotate      : bit;
    signal arithmetic  : bit;

begin
    -- Decode operation
    shift_left  <= '1' when operation = "000" or operation = "011" else '0';
    shift_right <= '1' when operation = "001" or operation = "010" or operation = "100" else '0';
    rotate      <= '1' when operation = "011" or operation = "100" else '0';
    arithmetic  <= '1' when operation = "010" else '0';

    -- Fill bit: sign bit for arithmetic, '0' for logical, wrap for rotate
    fill_bit <= data_in(31) when arithmetic = '1' else '0';

    -- Stage 0: Shift by 1 if shift_amt(0) = '1'
    stage0_proc: process(data_in, shift_amt, shift_left, rotate, fill_bit)
    begin
        if shift_amt(0) = '0' then
            stage0 <= data_in;
        elsif shift_left = '1' then
            -- Shift left by 1
            stage0(31 downto 1) <= data_in(30 downto 0);
            if rotate = '1' then
                stage0(0) <= data_in(31);
            else
                stage0(0) <= '0';
            end if;
        else
            -- Shift right by 1
            stage0(30 downto 0) <= data_in(31 downto 1);
            if rotate = '1' then
                stage0(31) <= data_in(0);
            else
                stage0(31) <= fill_bit;
            end if;
        end if;
    end process stage0_proc;

    -- Stage 1: Shift by 2 if shift_amt(1) = '1'
    stage1_proc: process(stage0, shift_amt, shift_left, rotate, fill_bit)
    begin
        if shift_amt(1) = '0' then
            stage1 <= stage0;
        elsif shift_left = '1' then
            stage1(31 downto 2) <= stage0(29 downto 0);
            if rotate = '1' then
                stage1(1 downto 0) <= stage0(31 downto 30);
            else
                stage1(1 downto 0) <= "00";
            end if;
        else
            stage1(29 downto 0) <= stage0(31 downto 2);
            if rotate = '1' then
                stage1(31 downto 30) <= stage0(1 downto 0);
            else
                stage1(31) <= fill_bit;
                stage1(30) <= fill_bit;
            end if;
        end if;
    end process stage1_proc;

    -- Stage 2: Shift by 4 if shift_amt(2) = '1'
    stage2_proc: process(stage1, shift_amt, shift_left, rotate, fill_bit)
    begin
        if shift_amt(2) = '0' then
            stage2 <= stage1;
        elsif shift_left = '1' then
            stage2(31 downto 4) <= stage1(27 downto 0);
            if rotate = '1' then
                stage2(3 downto 0) <= stage1(31 downto 28);
            else
                stage2(3 downto 0) <= "0000";
            end if;
        else
            stage2(27 downto 0) <= stage1(31 downto 4);
            if rotate = '1' then
                stage2(31 downto 28) <= stage1(3 downto 0);
            else
                stage2(31 downto 28) <= fill_bit & fill_bit & fill_bit & fill_bit;
            end if;
        end if;
    end process stage2_proc;

    -- Stage 3: Shift by 8 if shift_amt(3) = '1'
    stage3_proc: process(stage2, shift_amt, shift_left, rotate, fill_bit)
    begin
        if shift_amt(3) = '0' then
            stage3 <= stage2;
        elsif shift_left = '1' then
            stage3(31 downto 8) <= stage2(23 downto 0);
            if rotate = '1' then
                stage3(7 downto 0) <= stage2(31 downto 24);
            else
                stage3(7 downto 0) <= "00000000";
            end if;
        else
            stage3(23 downto 0) <= stage2(31 downto 8);
            if rotate = '1' then
                stage3(31 downto 24) <= stage2(7 downto 0);
            else
                stage3(31 downto 24) <= fill_bit & fill_bit & fill_bit & fill_bit &
                                        fill_bit & fill_bit & fill_bit & fill_bit;
            end if;
        end if;
    end process stage3_proc;

    -- Stage 4: Shift by 16 if shift_amt(4) = '1'
    stage4_proc: process(stage3, shift_amt, shift_left, rotate, fill_bit)
    begin
        if shift_amt(4) = '0' then
            stage4 <= stage3;
        elsif shift_left = '1' then
            stage4(31 downto 16) <= stage3(15 downto 0);
            if rotate = '1' then
                stage4(15 downto 0) <= stage3(31 downto 16);
            else
                stage4(15 downto 0) <= "0000000000000000";
            end if;
        else
            stage4(15 downto 0) <= stage3(31 downto 16);
            if rotate = '1' then
                stage4(31 downto 16) <= stage3(15 downto 0);
            else
                stage4(31 downto 16) <= fill_bit & fill_bit & fill_bit & fill_bit &
                                        fill_bit & fill_bit & fill_bit & fill_bit &
                                        fill_bit & fill_bit & fill_bit & fill_bit &
                                        fill_bit & fill_bit & fill_bit & fill_bit;
            end if;
        end if;
    end process stage4_proc;

    -- Final output
    data_out <= stage4;

    -- Carry out calculation (last bit shifted out)
    carry_proc: process(data_in, shift_amt, shift_left)
        variable amt_int : integer range 0 to 31;
    begin
        -- Convert shift amount to integer
        amt_int := 0;
        if shift_amt(0) = '1' then amt_int := amt_int + 1; end if;
        if shift_amt(1) = '1' then amt_int := amt_int + 2; end if;
        if shift_amt(2) = '1' then amt_int := amt_int + 4; end if;
        if shift_amt(3) = '1' then amt_int := amt_int + 8; end if;
        if shift_amt(4) = '1' then amt_int := amt_int + 16; end if;

        if amt_int = 0 then
            carry_out <= '0';
        elsif shift_left = '1' then
            -- For left shift, carry is bit (32 - amt)
            carry_out <= data_in(32 - amt_int);
        else
            -- For right shift, carry is bit (amt - 1)
            carry_out <= data_in(amt_int - 1);
        end if;
    end process carry_proc;

end Behavioral;

-- ============================================================================
-- 8-bit Barrel Shifter (Simpler version)
-- ============================================================================
entity BarrelShifter8 is
    port (
        data_in     : in  bit_vector(7 downto 0);
        shift_amt   : in  bit_vector(2 downto 0);
        shift_left  : in  bit;
        rotate      : in  bit;
        data_out    : out bit_vector(7 downto 0)
    );
end BarrelShifter8;

architecture Behavioral of BarrelShifter8 is
    signal s0, s1, s2 : bit_vector(7 downto 0);
begin
    -- Stage 0: shift by 1
    process(data_in, shift_amt, shift_left, rotate)
    begin
        if shift_amt(0) = '0' then
            s0 <= data_in;
        elsif shift_left = '1' then
            s0(7 downto 1) <= data_in(6 downto 0);
            s0(0) <= data_in(7) when rotate = '1' else '0';
        else
            s0(6 downto 0) <= data_in(7 downto 1);
            s0(7) <= data_in(0) when rotate = '1' else '0';
        end if;
    end process;

    -- Stage 1: shift by 2
    process(s0, shift_amt, shift_left, rotate)
    begin
        if shift_amt(1) = '0' then
            s1 <= s0;
        elsif shift_left = '1' then
            s1(7 downto 2) <= s0(5 downto 0);
            s1(1 downto 0) <= s0(7 downto 6) when rotate = '1' else "00";
        else
            s1(5 downto 0) <= s0(7 downto 2);
            s1(7 downto 6) <= s0(1 downto 0) when rotate = '1' else "00";
        end if;
    end process;

    -- Stage 2: shift by 4
    process(s1, shift_amt, shift_left, rotate)
    begin
        if shift_amt(2) = '0' then
            s2 <= s1;
        elsif shift_left = '1' then
            s2(7 downto 4) <= s1(3 downto 0);
            s2(3 downto 0) <= s1(7 downto 4) when rotate = '1' else "0000";
        else
            s2(3 downto 0) <= s1(7 downto 4);
            s2(7 downto 4) <= s1(3 downto 0) when rotate = '1' else "0000";
        end if;
    end process;

    data_out <= s2;
end Behavioral;

-- ============================================================================
-- Testbench
-- ============================================================================
entity BarrelShifter_TB is
end BarrelShifter_TB;

architecture Test of BarrelShifter_TB is
    signal tb_data_in   : bit_vector(31 downto 0);
    signal tb_shift_amt : bit_vector(4 downto 0);
    signal tb_operation : bit_vector(2 downto 0);
    signal tb_data_out  : bit_vector(31 downto 0);
    signal tb_carry_out : bit;
begin
    UUT: entity work.BarrelShifter
        port map (
            data_in   => tb_data_in,
            shift_amt => tb_shift_amt,
            operation => tb_operation,
            data_out  => tb_data_out,
            carry_out => tb_carry_out
        );

    stimulus: process
    begin
        -- Test data: 0x80000001
        tb_data_in <= "10000000000000000000000000000001";

        -- Logical shift left by 4
        tb_operation <= "000";
        tb_shift_amt <= "00100";
        wait for 10 ns;

        -- Logical shift right by 4
        tb_operation <= "001";
        wait for 10 ns;

        -- Arithmetic shift right by 4 (sign extend)
        tb_operation <= "010";
        wait for 10 ns;

        -- Rotate left by 4
        tb_operation <= "011";
        wait for 10 ns;

        -- Rotate right by 4
        tb_operation <= "100";
        wait for 10 ns;

        -- Large shift amounts
        tb_shift_amt <= "10000";  -- Shift by 16
        tb_operation <= "000";
        wait for 10 ns;

        tb_operation <= "001";
        wait for 10 ns;

        wait;
    end process stimulus;
end Test;
