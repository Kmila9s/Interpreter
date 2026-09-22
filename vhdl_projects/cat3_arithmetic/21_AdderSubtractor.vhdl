-- ============================================================================
-- VHDL Project: AdderSubtractor
-- Category: Arithmetic & Mathematics
-- Complexity: Low
-- Description: 32-bit adder/subtractor with overflow detection
--              Supports both signed and unsigned operations
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity AdderSubtractor is
    generic (
        WIDTH : integer := 32
    );
    port (
        A           : in  bit_vector(WIDTH-1 downto 0);
        B           : in  bit_vector(WIDTH-1 downto 0);
        subtract    : in  bit;  -- '0' = add, '1' = subtract
        signed_op   : in  bit;  -- '1' = signed operation
        result      : out bit_vector(WIDTH-1 downto 0);
        -- Flags
        carry_out   : out bit;  -- Carry/borrow out
        overflow    : out bit;  -- Signed overflow
        zero        : out bit;  -- Result is zero
        negative    : out bit   -- Result is negative (MSB)
    );
end AdderSubtractor;

architecture Behavioral of AdderSubtractor is
    signal b_operand : bit_vector(WIDTH-1 downto 0);
    signal carry_in : bit;
    signal sum : bit_vector(WIDTH-1 downto 0);
    signal carries : bit_vector(WIDTH downto 0);

begin
    -- For subtraction: B_effective = NOT(B), carry_in = 1
    -- This computes A + NOT(B) + 1 = A - B
    b_operand_gen: for i in 0 to WIDTH-1 generate
        b_operand(i) <= B(i) xor subtract;
    end generate;

    carry_in <= subtract;

    -- Ripple carry adder
    carries(0) <= carry_in;

    adder_gen: for i in 0 to WIDTH-1 generate
        sum(i) <= A(i) xor b_operand(i) xor carries(i);
        carries(i+1) <= (A(i) and b_operand(i)) or
                        (A(i) and carries(i)) or
                        (b_operand(i) and carries(i));
    end generate;

    result <= sum;

    -- Carry out
    carry_out <= carries(WIDTH);

    -- Overflow detection for signed operations
    -- Overflow occurs when:
    -- - Adding two positives gives negative
    -- - Adding two negatives gives positive
    -- - Subtracting positive from negative gives positive
    -- - Subtracting negative from positive gives negative
    -- Simplified: overflow = carry_in_to_MSB XOR carry_out_of_MSB
    overflow <= carries(WIDTH) xor carries(WIDTH-1);

    -- Zero flag
    zero_detect: process(sum)
        variable is_zero : bit;
    begin
        is_zero := '1';
        for i in 0 to WIDTH-1 loop
            is_zero := is_zero and (not sum(i));
        end loop;
        zero <= is_zero;
    end process;

    -- Negative flag (MSB of result)
    negative <= sum(WIDTH-1);

end Behavioral;

-- ============================================================================
-- Carry Look-Ahead Adder (4-bit block)
-- ============================================================================
entity CLA4 is
    port (
        A       : in  bit_vector(3 downto 0);
        B       : in  bit_vector(3 downto 0);
        Cin     : in  bit;
        S       : out bit_vector(3 downto 0);
        Cout    : out bit;
        PG      : out bit;  -- Group propagate
        GG      : out bit   -- Group generate
    );
end CLA4;

architecture Behavioral of CLA4 is
    signal P : bit_vector(3 downto 0);  -- Propagate
    signal G : bit_vector(3 downto 0);  -- Generate
    signal C : bit_vector(4 downto 0);  -- Carries
begin
    -- Generate propagate and generate signals
    pg_gen: for i in 0 to 3 generate
        P(i) <= A(i) xor B(i);
        G(i) <= A(i) and B(i);
    end generate;

    -- Carry lookahead logic
    C(0) <= Cin;
    C(1) <= G(0) or (P(0) and C(0));
    C(2) <= G(1) or (P(1) and G(0)) or (P(1) and P(0) and C(0));
    C(3) <= G(2) or (P(2) and G(1)) or (P(2) and P(1) and G(0)) or
            (P(2) and P(1) and P(0) and C(0));
    C(4) <= G(3) or (P(3) and G(2)) or (P(3) and P(2) and G(1)) or
            (P(3) and P(2) and P(1) and G(0)) or
            (P(3) and P(2) and P(1) and P(0) and C(0));

    -- Sum outputs
    sum_gen: for i in 0 to 3 generate
        S(i) <= P(i) xor C(i);
    end generate;

    Cout <= C(4);

    -- Group propagate and generate for cascading
    PG <= P(3) and P(2) and P(1) and P(0);
    GG <= G(3) or (P(3) and G(2)) or (P(3) and P(2) and G(1)) or
          (P(3) and P(2) and P(1) and G(0));

end Behavioral;

-- ============================================================================
-- 16-bit Carry Look-Ahead Adder
-- ============================================================================
entity CLA16 is
    port (
        A       : in  bit_vector(15 downto 0);
        B       : in  bit_vector(15 downto 0);
        Cin     : in  bit;
        S       : out bit_vector(15 downto 0);
        Cout    : out bit;
        overflow: out bit
    );
end CLA16;

architecture Structural of CLA16 is
    signal C : bit_vector(4 downto 0);
    signal PG, GG : bit_vector(3 downto 0);
begin
    -- Instantiate four CLA4 blocks
    CLA0: entity work.CLA4
        port map (A => A(3 downto 0), B => B(3 downto 0),
                  Cin => Cin, S => S(3 downto 0),
                  Cout => open, PG => PG(0), GG => GG(0));

    CLA1: entity work.CLA4
        port map (A => A(7 downto 4), B => B(7 downto 4),
                  Cin => C(1), S => S(7 downto 4),
                  Cout => open, PG => PG(1), GG => GG(1));

    CLA2: entity work.CLA4
        port map (A => A(11 downto 8), B => B(11 downto 8),
                  Cin => C(2), S => S(11 downto 8),
                  Cout => open, PG => PG(2), GG => GG(2));

    CLA3: entity work.CLA4
        port map (A => A(15 downto 12), B => B(15 downto 12),
                  Cin => C(3), S => S(15 downto 12),
                  Cout => open, PG => PG(3), GG => GG(3));

    -- Second level lookahead
    C(0) <= Cin;
    C(1) <= GG(0) or (PG(0) and C(0));
    C(2) <= GG(1) or (PG(1) and GG(0)) or (PG(1) and PG(0) and C(0));
    C(3) <= GG(2) or (PG(2) and GG(1)) or (PG(2) and PG(1) and GG(0)) or
            (PG(2) and PG(1) and PG(0) and C(0));
    C(4) <= GG(3) or (PG(3) and GG(2)) or (PG(3) and PG(2) and GG(1)) or
            (PG(3) and PG(2) and PG(1) and GG(0)) or
            (PG(3) and PG(2) and PG(1) and PG(0) and C(0));

    Cout <= C(4);
    overflow <= C(4) xor C(3);

end Structural;

-- ============================================================================
-- Saturating Adder
-- ============================================================================
entity SaturatingAdder is
    generic (
        WIDTH : integer := 16
    );
    port (
        A           : in  bit_vector(WIDTH-1 downto 0);
        B           : in  bit_vector(WIDTH-1 downto 0);
        is_signed   : in  bit;
        result      : out bit_vector(WIDTH-1 downto 0);
        saturated   : out bit
    );
end SaturatingAdder;

architecture Behavioral of SaturatingAdder is
    signal sum : bit_vector(WIDTH-1 downto 0);
    signal carry_out : bit;
    signal ovf : bit;
    signal max_pos : bit_vector(WIDTH-1 downto 0);
    signal max_neg : bit_vector(WIDTH-1 downto 0);
    signal max_unsigned : bit_vector(WIDTH-1 downto 0);

begin
    -- Instantiate basic adder
    adder: entity work.AdderSubtractor
        generic map (WIDTH => WIDTH)
        port map (
            A         => A,
            B         => B,
            subtract  => '0',
            signed_op => is_signed,
            result    => sum,
            carry_out => carry_out,
            overflow  => ovf,
            zero      => open,
            negative  => open
        );

    -- Maximum values
    max_gen: for i in 0 to WIDTH-1 generate
        max_pos(i) <= '1' when i < WIDTH-1 else '0';  -- 0111...1
        max_neg(i) <= '0' when i < WIDTH-1 else '1';  -- 1000...0
        max_unsigned(i) <= '1';  -- 1111...1
    end generate;

    -- Saturation logic
    sat_proc: process(sum, ovf, carry_out, is_signed, A, B, max_pos, max_neg, max_unsigned)
    begin
        saturated <= '0';

        if is_signed = '1' then
            -- Signed saturation
            if ovf = '1' then
                saturated <= '1';
                if A(WIDTH-1) = '0' and B(WIDTH-1) = '0' then
                    -- Positive overflow
                    result <= max_pos;
                else
                    -- Negative overflow
                    result <= max_neg;
                end if;
            else
                result <= sum;
            end if;
        else
            -- Unsigned saturation
            if carry_out = '1' then
                saturated <= '1';
                result <= max_unsigned;
            else
                result <= sum;
            end if;
        end if;
    end process;

end Behavioral;

-- ============================================================================
-- Increment/Decrement Unit
-- ============================================================================
entity IncDec is
    generic (
        WIDTH : integer := 16
    );
    port (
        data_in     : in  bit_vector(WIDTH-1 downto 0);
        increment   : in  bit;  -- '1' = increment, '0' = decrement
        data_out    : out bit_vector(WIDTH-1 downto 0);
        carry       : out bit;
        borrow      : out bit
    );
end IncDec;

architecture Behavioral of IncDec is
    signal one : bit_vector(WIDTH-1 downto 0);
begin
    -- Create constant 1
    one(0) <= '1';
    one_gen: for i in 1 to WIDTH-1 generate
        one(i) <= '0';
    end generate;

    -- Use adder/subtractor
    inc_dec: entity work.AdderSubtractor
        generic map (WIDTH => WIDTH)
        port map (
            A         => data_in,
            B         => one,
            subtract  => not increment,
            signed_op => '0',
            result    => data_out,
            carry_out => carry,
            overflow  => open,
            zero      => open,
            negative  => open
        );

    -- Borrow is carry for subtraction
    borrow <= (not increment) and (not data_in(0));  -- Simplified

end Behavioral;

-- ============================================================================
-- Testbench
-- ============================================================================
entity AdderSubtractor_TB is
end AdderSubtractor_TB;

architecture Test of AdderSubtractor_TB is
    signal tb_A, tb_B : bit_vector(31 downto 0);
    signal tb_subtract, tb_signed_op : bit;
    signal tb_result : bit_vector(31 downto 0);
    signal tb_carry, tb_overflow, tb_zero, tb_negative : bit;
begin
    UUT: entity work.AdderSubtractor
        generic map (WIDTH => 32)
        port map (
            A         => tb_A,
            B         => tb_B,
            subtract  => tb_subtract,
            signed_op => tb_signed_op,
            result    => tb_result,
            carry_out => tb_carry,
            overflow  => tb_overflow,
            zero      => tb_zero,
            negative  => tb_negative
        );

    stimulus: process
    begin
        tb_signed_op <= '0';

        -- Test addition: 5 + 3 = 8
        tb_A <= "00000000000000000000000000000101";
        tb_B <= "00000000000000000000000000000011";
        tb_subtract <= '0';
        wait for 10 ns;

        -- Test subtraction: 5 - 3 = 2
        tb_subtract <= '1';
        wait for 10 ns;

        -- Test subtraction: 3 - 5 = -2 (unsigned: large number)
        tb_A <= "00000000000000000000000000000011";
        tb_B <= "00000000000000000000000000000101";
        wait for 10 ns;

        -- Test signed overflow
        tb_signed_op <= '1';
        tb_A <= "01111111111111111111111111111111";  -- Max positive
        tb_B <= "00000000000000000000000000000001";
        tb_subtract <= '0';
        wait for 10 ns;

        -- Test zero result
        tb_A <= "00000000000000000000000000000101";
        tb_B <= "00000000000000000000000000000101";
        tb_subtract <= '1';
        wait for 10 ns;

        wait;
    end process;
end Test;
