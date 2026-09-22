-- ============================================================================
-- VHDL Project: FloatingPointAdd
-- Category: Arithmetic & Mathematics
-- Complexity: High
-- Description: Simple 32-bit floating point adder (IEEE 754-like)
--              Handles normalization, rounding, special cases
-- Uses IEEE for std_logic (complex arithmetic)
-- ============================================================================

library IEEE;
use IEEE.std_logic_1164.all;

entity FloatingPointAdd is
    port (
        clk         : in  std_logic;
        reset       : in  std_logic;
        start       : in  std_logic;
        -- IEEE 754 single precision format
        -- [31] sign, [30:23] exponent, [22:0] mantissa
        a           : in  std_logic_vector(31 downto 0);
        b           : in  std_logic_vector(31 downto 0);
        result      : out std_logic_vector(31 downto 0);
        done        : out std_logic;
        -- Exceptions
        overflow    : out std_logic;
        underflow   : out std_logic;
        invalid     : out std_logic
    );
end FloatingPointAdd;

architecture Behavioral of FloatingPointAdd is
    type state_type is (IDLE, UNPACK, ALIGN, ADD, NORMALIZE, ROUND, PACK, OUTPUT);
    signal state : state_type;

    -- Unpacked operands
    signal sign_a, sign_b : std_logic;
    signal exp_a, exp_b : std_logic_vector(7 downto 0);
    signal mant_a, mant_b : std_logic_vector(23 downto 0);  -- With implicit 1

    -- Aligned operands
    signal exp_diff : integer range -255 to 255;
    signal mant_a_aligned, mant_b_aligned : std_logic_vector(26 downto 0);  -- Guard, round, sticky
    signal common_exp : std_logic_vector(7 downto 0);

    -- Result
    signal sign_r : std_logic;
    signal exp_r : std_logic_vector(8 downto 0);  -- Extended for overflow
    signal mant_r : std_logic_vector(27 downto 0);  -- Extra bits for carry

    -- Special value flags
    signal a_zero, b_zero : std_logic;
    signal a_inf, b_inf : std_logic;
    signal a_nan, b_nan : std_logic;

begin
    fp_add_proc: process(clk, reset)
        variable effective_op : std_logic;  -- '0' = add, '1' = sub
        variable shift_amount : integer;
        variable mant_sum : std_logic_vector(27 downto 0);
        variable leading_zeros : integer;
        variable guard, round_bit, sticky : std_logic;
    begin
        if reset = '1' then
            state <= IDLE;
            done <= '0';
            overflow <= '0';
            underflow <= '0';
            invalid <= '0';
            result <= (others => '0');
        elsif rising_edge(clk) then
            case state is
                when IDLE =>
                    done <= '0';
                    overflow <= '0';
                    underflow <= '0';
                    invalid <= '0';
                    if start = '1' then
                        state <= UNPACK;
                    end if;

                when UNPACK =>
                    -- Extract fields
                    sign_a <= a(31);
                    sign_b <= b(31);
                    exp_a <= a(30 downto 23);
                    exp_b <= b(30 downto 23);

                    -- Add implicit 1 (unless denormal/zero)
                    if a(30 downto 23) = "00000000" then
                        mant_a <= '0' & a(22 downto 0);
                        a_zero <= '1' when a(22 downto 0) = "00000000000000000000000" else '0';
                    else
                        mant_a <= '1' & a(22 downto 0);
                        a_zero <= '0';
                    end if;

                    if b(30 downto 23) = "00000000" then
                        mant_b <= '0' & b(22 downto 0);
                        b_zero <= '1' when b(22 downto 0) = "00000000000000000000000" else '0';
                    else
                        mant_b <= '1' & b(22 downto 0);
                        b_zero <= '0';
                    end if;

                    -- Check for special values
                    a_inf <= '1' when a(30 downto 23) = "11111111" and a(22 downto 0) = "00000000000000000000000" else '0';
                    b_inf <= '1' when b(30 downto 23) = "11111111" and b(22 downto 0) = "00000000000000000000000" else '0';
                    a_nan <= '1' when a(30 downto 23) = "11111111" and a(22 downto 0) /= "00000000000000000000000" else '0';
                    b_nan <= '1' when b(30 downto 23) = "11111111" and b(22 downto 0) /= "00000000000000000000000" else '0';

                    state <= ALIGN;

                when ALIGN =>
                    -- Handle special cases first
                    if a_nan = '1' or b_nan = '1' then
                        -- NaN result
                        result <= "01111111110000000000000000000000";
                        invalid <= '1';
                        state <= OUTPUT;
                    elsif a_inf = '1' and b_inf = '1' and sign_a /= sign_b then
                        -- Inf - Inf = NaN
                        result <= "01111111110000000000000000000000";
                        invalid <= '1';
                        state <= OUTPUT;
                    elsif a_inf = '1' then
                        result <= a;
                        state <= OUTPUT;
                    elsif b_inf = '1' then
                        result <= b;
                        state <= OUTPUT;
                    elsif a_zero = '1' then
                        result <= b;
                        state <= OUTPUT;
                    elsif b_zero = '1' then
                        result <= a;
                        state <= OUTPUT;
                    else
                        -- Compute exponent difference
                        if exp_a >= exp_b then
                            exp_diff <= to_integer(unsigned(exp_a)) - to_integer(unsigned(exp_b));
                            common_exp <= exp_a;

                            -- Align mantissa B (shift right)
                            shift_amount := to_integer(unsigned(exp_a)) - to_integer(unsigned(exp_b));
                            if shift_amount > 26 then shift_amount := 26; end if;

                            mant_a_aligned <= mant_a & "000";

                            -- Shift B and collect sticky bits
                            mant_b_aligned <= (others => '0');
                            for i in 26 downto 0 loop
                                if i + shift_amount <= 23 then
                                    mant_b_aligned(i) <= mant_b(i + shift_amount);
                                end if;
                            end loop;
                        else
                            common_exp <= exp_b;

                            shift_amount := to_integer(unsigned(exp_b)) - to_integer(unsigned(exp_a));
                            if shift_amount > 26 then shift_amount := 26; end if;

                            mant_b_aligned <= mant_b & "000";

                            mant_a_aligned <= (others => '0');
                            for i in 26 downto 0 loop
                                if i + shift_amount <= 23 then
                                    mant_a_aligned(i) <= mant_a(i + shift_amount);
                                end if;
                            end loop;
                        end if;

                        state <= ADD;
                    end if;

                when ADD =>
                    -- Determine effective operation
                    effective_op := sign_a xor sign_b;

                    if effective_op = '0' then
                        -- True addition
                        mant_sum := ('0' & mant_a_aligned) + ('0' & mant_b_aligned);
                        sign_r <= sign_a;
                    else
                        -- Subtraction
                        if mant_a_aligned >= mant_b_aligned then
                            mant_sum := ('0' & mant_a_aligned) - ('0' & mant_b_aligned);
                            sign_r <= sign_a;
                        else
                            mant_sum := ('0' & mant_b_aligned) - ('0' & mant_a_aligned);
                            sign_r <= sign_b;
                        end if;
                    end if;

                    mant_r <= mant_sum;
                    exp_r <= '0' & common_exp;
                    state <= NORMALIZE;

                when NORMALIZE =>
                    -- Handle carry out
                    if mant_r(27) = '1' then
                        mant_r <= '0' & mant_r(27 downto 1);
                        exp_r <= exp_r + 1;
                    else
                        -- Count leading zeros and shift left
                        leading_zeros := 0;
                        for i in 26 downto 0 loop
                            if mant_r(i) = '0' and leading_zeros = 26 - i then
                                leading_zeros := leading_zeros + 1;
                            end if;
                        end loop;

                        if leading_zeros > 0 and to_integer(signed(exp_r)) > leading_zeros then
                            -- Shift mantissa left
                            for i in 0 to 26 loop
                                if i + leading_zeros <= 26 then
                                    mant_r(i + leading_zeros) <= mant_r(i);
                                end if;
                            end loop;
                            for i in 0 to leading_zeros-1 loop
                                mant_r(i) <= '0';
                            end loop;
                            exp_r <= exp_r - leading_zeros;
                        end if;
                    end if;

                    state <= ROUND;

                when ROUND =>
                    -- Round to nearest even
                    guard := mant_r(3);
                    round_bit := mant_r(2);
                    sticky := mant_r(1) or mant_r(0);

                    if guard = '1' and (round_bit = '1' or sticky = '1' or mant_r(4) = '1') then
                        -- Round up
                        mant_r <= mant_r + 16;  -- Add 1 to bit 4
                    end if;

                    state <= PACK;

                when PACK =>
                    -- Check for overflow/underflow
                    if exp_r >= 255 then
                        -- Overflow to infinity
                        result <= sign_r & "11111111" & "00000000000000000000000";
                        overflow <= '1';
                    elsif exp_r <= 0 then
                        -- Underflow to zero
                        result <= sign_r & "00000000" & "00000000000000000000000";
                        underflow <= '1';
                    else
                        -- Normal result
                        result <= sign_r & exp_r(7 downto 0) & mant_r(25 downto 3);
                    end if;

                    state <= OUTPUT;

                when OUTPUT =>
                    done <= '1';
                    state <= IDLE;

            end case;
        end if;
    end process;

end Behavioral;
