-- ============================================================================
-- VHDL Project: FixedPointDiv
-- Category: Arithmetic & Mathematics
-- Complexity: Medium
-- Description: 16.16 fixed-point divider using non-restoring algorithm
--              Supports signed/unsigned operations
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity FixedPointDiv is
    generic (
        INT_BITS  : integer := 16;
        FRAC_BITS : integer := 16
    );
    port (
        clk         : in  bit;
        reset       : in  bit;
        start       : in  bit;
        dividend    : in  bit_vector(INT_BITS+FRAC_BITS-1 downto 0);
        divisor     : in  bit_vector(INT_BITS+FRAC_BITS-1 downto 0);
        is_signed   : in  bit;
        quotient    : out bit_vector(INT_BITS+FRAC_BITS-1 downto 0);
        remainder   : out bit_vector(INT_BITS+FRAC_BITS-1 downto 0);
        done        : out bit;
        div_by_zero : out bit;
        overflow    : out bit
    );
end FixedPointDiv;

architecture Behavioral of FixedPointDiv is
    constant TOTAL_BITS : integer := INT_BITS + FRAC_BITS;
    constant WORK_BITS : integer := 2 * TOTAL_BITS;

    type state_type is (IDLE, INIT, DIVIDING, CORRECT, OUTPUT);
    signal state : state_type;

    signal work_dividend : bit_vector(WORK_BITS-1 downto 0);
    signal work_divisor : bit_vector(WORK_BITS-1 downto 0);
    signal work_quotient : bit_vector(TOTAL_BITS-1 downto 0);
    signal work_remainder : bit_vector(WORK_BITS-1 downto 0);
    signal bit_counter : integer range 0 to TOTAL_BITS;
    signal result_sign : bit;

    function negate(val : bit_vector) return bit_vector is
        variable result : bit_vector(val'range);
        variable carry : bit := '1';
    begin
        for i in val'low to val'high loop
            result(i) := (not val(i)) xor carry;
            carry := (not val(i)) and carry;
        end loop;
        return result;
    end function;

    function is_zero(val : bit_vector) return bit is
        variable result : bit := '1';
    begin
        for i in val'range loop
            result := result and (not val(i));
        end loop;
        return result;
    end function;

    function subtract(a, b : bit_vector) return bit_vector is
        variable result : bit_vector(a'range);
        variable borrow : bit := '0';
        variable b_inv : bit;
    begin
        for i in a'low to a'high loop
            b_inv := not b(i);
            result(i) := a(i) xor b_inv xor borrow;
            borrow := (not a(i) and b_inv) or (not a(i) and borrow) or (b_inv and borrow);
        end loop;
        return result;
    end function;

    function add(a, b : bit_vector) return bit_vector is
        variable result : bit_vector(a'range);
        variable carry : bit := '0';
    begin
        for i in a'low to a'high loop
            result(i) := a(i) xor b(i) xor carry;
            carry := (a(i) and b(i)) or (a(i) and carry) or (b(i) and carry);
        end loop;
        return result;
    end function;

begin
    div_process: process(clk, reset)
        variable temp_remainder : bit_vector(WORK_BITS-1 downto 0);
        variable abs_dividend, abs_divisor : bit_vector(TOTAL_BITS-1 downto 0);
        variable sign_dividend, sign_divisor : bit;
    begin
        if reset = '1' then
            state <= IDLE;
            done <= '0';
            div_by_zero <= '0';
            overflow <= '0';
            work_dividend <= (others => '0');
            work_divisor <= (others => '0');
            work_quotient <= (others => '0');
            work_remainder <= (others => '0');
            bit_counter <= 0;
            result_sign <= '0';
        elsif clk'event and clk = '1' then
            case state is
                when IDLE =>
                    done <= '0';
                    div_by_zero <= '0';
                    overflow <= '0';
                    if start = '1' then
                        -- Check for divide by zero
                        if is_zero(divisor) = '1' then
                            div_by_zero <= '1';
                            done <= '1';
                        else
                            state <= INIT;
                        end if;
                    end if;

                when INIT =>
                    -- Handle signs
                    if is_signed = '1' then
                        sign_dividend := dividend(TOTAL_BITS-1);
                        sign_divisor := divisor(TOTAL_BITS-1);
                        result_sign <= sign_dividend xor sign_divisor;

                        if sign_dividend = '1' then
                            abs_dividend := negate(dividend);
                        else
                            abs_dividend := dividend;
                        end if;

                        if sign_divisor = '1' then
                            abs_divisor := negate(divisor);
                        else
                            abs_divisor := divisor;
                        end if;
                    else
                        result_sign <= '0';
                        abs_dividend := dividend;
                        abs_divisor := divisor;
                    end if;

                    -- Initialize working registers
                    -- Shift dividend left by FRAC_BITS for fixed-point
                    work_dividend <= (others => '0');
                    for i in 0 to TOTAL_BITS-1 loop
                        work_dividend(i + FRAC_BITS) <= abs_dividend(i);
                    end loop;

                    work_divisor <= (others => '0');
                    for i in 0 to TOTAL_BITS-1 loop
                        work_divisor(i) <= abs_divisor(i);
                    end loop;

                    work_remainder <= (others => '0');
                    work_quotient <= (others => '0');
                    bit_counter <= TOTAL_BITS;
                    state <= DIVIDING;

                when DIVIDING =>
                    -- Non-restoring division step
                    -- Shift remainder left, bring in next dividend bit
                    temp_remainder := work_remainder(WORK_BITS-2 downto 0) &
                                      work_dividend(WORK_BITS-1);

                    -- Shift dividend left
                    work_dividend <= work_dividend(WORK_BITS-2 downto 0) & '0';

                    -- Try subtraction
                    if temp_remainder(WORK_BITS-1) = '0' then
                        -- Remainder positive: subtract divisor
                        work_remainder <= subtract(temp_remainder, work_divisor);
                    else
                        -- Remainder negative: add divisor
                        work_remainder <= add(temp_remainder, work_divisor);
                    end if;

                    -- Shift quotient and insert bit
                    work_quotient <= work_quotient(TOTAL_BITS-2 downto 0) &
                                    (not temp_remainder(WORK_BITS-1));

                    bit_counter <= bit_counter - 1;
                    if bit_counter = 1 then
                        state <= CORRECT;
                    end if;

                when CORRECT =>
                    -- Correct remainder if negative
                    if work_remainder(WORK_BITS-1) = '1' then
                        work_remainder <= add(work_remainder, work_divisor);
                    end if;
                    state <= OUTPUT;

                when OUTPUT =>
                    -- Apply sign to result
                    if result_sign = '1' then
                        quotient <= negate(work_quotient);
                    else
                        quotient <= work_quotient;
                    end if;

                    -- Extract remainder
                    for i in 0 to TOTAL_BITS-1 loop
                        remainder(i) <= work_remainder(i);
                    end loop;

                    done <= '1';
                    state <= IDLE;

            end case;
        end if;
    end process;

end Behavioral;

-- ============================================================================
-- Simple Combinational Divider (for small widths)
-- ============================================================================
entity CombDivider8 is
    port (
        dividend    : in  bit_vector(7 downto 0);
        divisor     : in  bit_vector(7 downto 0);
        quotient    : out bit_vector(7 downto 0);
        remainder   : out bit_vector(7 downto 0);
        div_by_zero : out bit
    );
end CombDivider8;

architecture Behavioral of CombDivider8 is
    function is_zero(val : bit_vector) return bit is
        variable result : bit := '1';
    begin
        for i in val'range loop
            result := result and (not val(i));
        end loop;
        return result;
    end function;

    function compare_ge(a, b : bit_vector) return bit is
    begin
        for i in a'high downto a'low loop
            if a(i) = '1' and b(i) = '0' then return '1'; end if;
            if a(i) = '0' and b(i) = '1' then return '0'; end if;
        end loop;
        return '1';
    end function;

    function subtract(a, b : bit_vector) return bit_vector is
        variable result : bit_vector(a'range);
        variable borrow : bit := '0';
    begin
        for i in a'low to a'high loop
            result(i) := a(i) xor b(i) xor borrow;
            borrow := (not a(i) and b(i)) or (not a(i) and borrow) or (b(i) and borrow);
        end loop;
        return result;
    end function;

begin
    div_proc: process(dividend, divisor)
        variable rem : bit_vector(8 downto 0);
        variable quo : bit_vector(7 downto 0);
        variable div_ext : bit_vector(8 downto 0);
    begin
        if is_zero(divisor) = '1' then
            quotient <= (others => '1');
            remainder <= dividend;
            div_by_zero <= '1';
        else
            rem := '0' & dividend;
            quo := (others => '0');
            div_ext := '0' & divisor;

            for i in 7 downto 0 loop
                -- Shift remainder left
                rem := rem(7 downto 0) & '0';

                -- Compare and subtract
                if compare_ge(rem, div_ext) = '1' then
                    rem := subtract(rem, div_ext);
                    quo(i) := '1';
                else
                    quo(i) := '0';
                end if;
            end loop;

            quotient <= quo;
            remainder <= rem(7 downto 0);
            div_by_zero <= '0';
        end if;
    end process;

end Behavioral;

-- ============================================================================
-- Testbench
-- ============================================================================
entity FixedPointDiv_TB is
end FixedPointDiv_TB;

architecture Test of FixedPointDiv_TB is
    signal tb_clk, tb_reset, tb_start : bit;
    signal tb_dividend, tb_divisor : bit_vector(31 downto 0);
    signal tb_signed : bit;
    signal tb_quotient, tb_remainder : bit_vector(31 downto 0);
    signal tb_done, tb_div_zero, tb_overflow : bit;
begin
    UUT: entity work.FixedPointDiv
        generic map (INT_BITS => 16, FRAC_BITS => 16)
        port map (
            clk         => tb_clk,
            reset       => tb_reset,
            start       => tb_start,
            dividend    => tb_dividend,
            divisor     => tb_divisor,
            is_signed   => tb_signed,
            quotient    => tb_quotient,
            remainder   => tb_remainder,
            done        => tb_done,
            div_by_zero => tb_div_zero,
            overflow    => tb_overflow
        );

    clk_gen: process
    begin
        tb_clk <= '0'; wait for 5 ns;
        tb_clk <= '1'; wait for 5 ns;
    end process;

    stimulus: process
    begin
        tb_reset <= '1';
        tb_start <= '0';
        tb_signed <= '0';
        tb_dividend <= (others => '0');
        tb_divisor <= (others => '0');
        wait for 30 ns;

        tb_reset <= '0';

        -- Test: 4.0 / 2.0 = 2.0
        tb_dividend <= "00000000000001000000000000000000";  -- 4.0
        tb_divisor  <= "00000000000000100000000000000000";  -- 2.0
        tb_start <= '1';
        wait for 10 ns;
        tb_start <= '0';

        wait until tb_done = '1';
        wait for 20 ns;

        -- Test: 1.0 / 4.0 = 0.25
        tb_dividend <= "00000000000000010000000000000000";  -- 1.0
        tb_divisor  <= "00000000000001000000000000000000";  -- 4.0
        tb_start <= '1';
        wait for 10 ns;
        tb_start <= '0';

        wait until tb_done = '1';
        wait for 20 ns;

        wait;
    end process;
end Test;
