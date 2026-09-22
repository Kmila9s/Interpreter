-- ============================================================================
-- VHDL Project: FixedPointMul
-- Category: Arithmetic & Mathematics
-- Complexity: Medium
-- Description: 16.16 fixed-point multiplier
--              Supports signed/unsigned, saturation, rounding modes
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity FixedPointMul is
    generic (
        INT_BITS  : integer := 16;
        FRAC_BITS : integer := 16
    );
    port (
        A           : in  bit_vector(INT_BITS+FRAC_BITS-1 downto 0);
        B           : in  bit_vector(INT_BITS+FRAC_BITS-1 downto 0);
        is_signed   : in  bit;
        round_mode  : in  bit_vector(1 downto 0);
        -- "00" = truncate (round toward zero)
        -- "01" = round to nearest
        -- "10" = round up (ceiling)
        -- "11" = round down (floor)
        saturate    : in  bit;
        result      : out bit_vector(INT_BITS+FRAC_BITS-1 downto 0);
        overflow    : out bit
    );
end FixedPointMul;

architecture Behavioral of FixedPointMul is
    constant TOTAL_BITS : integer := INT_BITS + FRAC_BITS;
    constant PRODUCT_BITS : integer := 2 * TOTAL_BITS;

    signal a_extended : bit_vector(PRODUCT_BITS-1 downto 0);
    signal b_extended : bit_vector(PRODUCT_BITS-1 downto 0);
    signal product : bit_vector(PRODUCT_BITS-1 downto 0);
    signal shifted : bit_vector(PRODUCT_BITS-1 downto 0);
    signal rounded : bit_vector(TOTAL_BITS-1 downto 0);

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

    function multiply_unsigned(a, b : bit_vector) return bit_vector is
        variable product : bit_vector(a'length + b'length - 1 downto 0);
        variable partial : bit_vector(a'length + b'length - 1 downto 0);
        variable carry : bit;
    begin
        product := (others => '0');
        for i in b'low to b'high loop
            if b(i) = '1' then
                -- Add shifted A to product
                partial := (others => '0');
                for j in a'low to a'high loop
                    partial(j + i - a'low) := a(j);
                end loop;

                -- Add partial to product
                carry := '0';
                for k in product'low to product'high loop
                    product(k) := product(k) xor partial(k) xor carry;
                    carry := (product(k - product'low) and partial(k)) or
                             (product(k - product'low) and carry) or
                             (partial(k) and carry);
                end loop;
            end if;
        end loop;
        return product;
    end function;

begin
    -- Sign extension and absolute value for signed multiply
    sign_process: process(A, B, is_signed)
        variable a_abs, b_abs : bit_vector(TOTAL_BITS-1 downto 0);
        variable a_neg, b_neg : bit;
        variable prod_neg : bit;
    begin
        if is_signed = '1' then
            a_neg := A(TOTAL_BITS-1);
            b_neg := B(TOTAL_BITS-1);
            prod_neg := a_neg xor b_neg;

            -- Get absolute values
            if a_neg = '1' then
                a_abs := negate(A);
            else
                a_abs := A;
            end if;

            if b_neg = '1' then
                b_abs := negate(B);
            else
                b_abs := B;
            end if;

            -- Multiply magnitudes
            product <= multiply_unsigned(a_abs, b_abs);

            -- Negate result if needed
            if prod_neg = '1' then
                shifted <= negate(product);
            else
                shifted <= product;
            end if;
        else
            product <= multiply_unsigned(A, B);
            shifted <= product;
        end if;
    end process;

    -- Shift and round
    round_process: process(shifted, round_mode)
        variable extract_start : integer;
        variable round_bit : bit;
        variable sticky_bit : bit;
        variable truncated : bit_vector(TOTAL_BITS-1 downto 0);
        variable rounded_up : bit_vector(TOTAL_BITS-1 downto 0);
        variable do_round : bit;
        variable carry : bit;
    begin
        -- Extract relevant bits (shift right by FRAC_BITS)
        extract_start := FRAC_BITS;
        for i in 0 to TOTAL_BITS-1 loop
            truncated(i) := shifted(i + extract_start);
        end loop;

        -- Get round and sticky bits
        if FRAC_BITS > 0 then
            round_bit := shifted(FRAC_BITS - 1);
            sticky_bit := '0';
            for i in 0 to FRAC_BITS-2 loop
                sticky_bit := sticky_bit or shifted(i);
            end loop;
        else
            round_bit := '0';
            sticky_bit := '0';
        end if;

        -- Determine if we should round up
        case round_mode is
            when "00" =>  -- Truncate
                do_round := '0';
            when "01" =>  -- Round to nearest (round half up)
                do_round := round_bit;
            when "10" =>  -- Ceiling
                do_round := round_bit or sticky_bit;
            when "11" =>  -- Floor
                do_round := '0';
            when others =>
                do_round := '0';
        end case;

        -- Add rounding
        if do_round = '1' then
            carry := '1';
            for i in 0 to TOTAL_BITS-1 loop
                rounded_up(i) := truncated(i) xor carry;
                carry := truncated(i) and carry;
            end loop;
            rounded <= rounded_up;
        else
            rounded <= truncated;
        end if;
    end process;

    -- Saturation and overflow detection
    saturate_process: process(shifted, rounded, saturate, is_signed)
        variable ovf : bit;
        variable max_pos : bit_vector(TOTAL_BITS-1 downto 0);
        variable max_neg : bit_vector(TOTAL_BITS-1 downto 0);
    begin
        -- Check for overflow (upper bits beyond result range)
        ovf := '0';
        for i in TOTAL_BITS + FRAC_BITS to PRODUCT_BITS-1 loop
            if is_signed = '1' then
                ovf := ovf or (shifted(i) xor shifted(TOTAL_BITS + FRAC_BITS - 1));
            else
                ovf := ovf or shifted(i);
            end if;
        end loop;

        overflow <= ovf;

        if saturate = '1' and ovf = '1' then
            if is_signed = '1' then
                -- Signed saturation
                if shifted(PRODUCT_BITS-1) = '1' then
                    -- Negative overflow
                    max_neg := (TOTAL_BITS-1 => '1', others => '0');
                    result <= max_neg;
                else
                    -- Positive overflow
                    max_pos := (TOTAL_BITS-1 => '0', others => '1');
                    result <= max_pos;
                end if;
            else
                -- Unsigned saturation
                result <= (others => '1');
            end if;
        else
            result <= rounded;
        end if;
    end process;

end Behavioral;

-- ============================================================================
-- Simple 8x8 Unsigned Multiplier (building block)
-- ============================================================================
entity Multiplier8x8 is
    port (
        A       : in  bit_vector(7 downto 0);
        B       : in  bit_vector(7 downto 0);
        P       : out bit_vector(15 downto 0)
    );
end Multiplier8x8;

architecture Behavioral of Multiplier8x8 is
begin
    mult_proc: process(A, B)
        variable product : bit_vector(15 downto 0);
        variable partial : bit_vector(15 downto 0);
        variable sum : bit_vector(15 downto 0);
        variable carry : bit;
    begin
        product := (others => '0');

        for i in 0 to 7 loop
            if B(i) = '1' then
                -- Create partial product (A shifted by i)
                partial := (others => '0');
                for j in 0 to 7 loop
                    partial(j + i) := A(j);
                end loop;

                -- Add to accumulated product
                sum := (others => '0');
                carry := '0';
                for k in 0 to 15 loop
                    sum(k) := product(k) xor partial(k) xor carry;
                    carry := (product(k) and partial(k)) or
                             (product(k) and carry) or
                             (partial(k) and carry);
                end loop;
                product := sum;
            end if;
        end loop;

        P <= product;
    end process;

end Behavioral;

-- ============================================================================
-- Pipelined Fixed-Point Multiplier
-- ============================================================================
entity FixedPointMulPipelined is
    generic (
        WIDTH : integer := 16;
        STAGES : integer := 4
    );
    port (
        clk     : in  bit;
        reset   : in  bit;
        A       : in  bit_vector(WIDTH-1 downto 0);
        B       : in  bit_vector(WIDTH-1 downto 0);
        valid_in: in  bit;
        P       : out bit_vector(2*WIDTH-1 downto 0);
        valid_out: out bit
    );
end FixedPointMulPipelined;

architecture Behavioral of FixedPointMulPipelined is
    type partial_array is array (0 to STAGES-1) of bit_vector(2*WIDTH-1 downto 0);
    signal partials : partial_array;
    signal valid_pipe : bit_vector(STAGES-1 downto 0);

    constant BITS_PER_STAGE : integer := WIDTH / STAGES;
begin
    pipeline_proc: process(clk, reset)
        variable partial : bit_vector(2*WIDTH-1 downto 0);
        variable shift_a : bit_vector(2*WIDTH-1 downto 0);
        variable carry : bit;
        variable start_bit, end_bit : integer;
    begin
        if reset = '1' then
            for i in 0 to STAGES-1 loop
                partials(i) <= (others => '0');
            end loop;
            valid_pipe <= (others => '0');
        elsif clk'event and clk = '1' then
            -- Stage 0: First partial products
            partials(0) <= (others => '0');
            shift_a := (others => '0');
            for j in 0 to WIDTH-1 loop
                shift_a(j) := A(j);
            end loop;

            for i in 0 to BITS_PER_STAGE-1 loop
                if B(i) = '1' then
                    -- Add shifted A
                    partial := partials(0);
                    carry := '0';
                    for k in 0 to 2*WIDTH-1 loop
                        partial(k) := partials(0)(k) xor shift_a(k) xor carry;
                        carry := (partials(0)(k) and shift_a(k)) or
                                 (partials(0)(k) and carry) or
                                 (shift_a(k) and carry);
                    end loop;
                    partials(0) <= partial;
                end if;
                -- Shift A left
                shift_a := shift_a(2*WIDTH-2 downto 0) & '0';
            end loop;

            valid_pipe(0) <= valid_in;

            -- Subsequent stages
            for stage in 1 to STAGES-1 loop
                partials(stage) <= partials(stage-1);
                start_bit := stage * BITS_PER_STAGE;
                end_bit := (stage + 1) * BITS_PER_STAGE - 1;

                -- Continue accumulating partial products
                -- (Simplified - full implementation would continue multiplication)

                valid_pipe(stage) <= valid_pipe(stage-1);
            end loop;
        end if;
    end process;

    P <= partials(STAGES-1);
    valid_out <= valid_pipe(STAGES-1);

end Behavioral;

-- ============================================================================
-- Testbench
-- ============================================================================
entity FixedPointMul_TB is
end FixedPointMul_TB;

architecture Test of FixedPointMul_TB is
    signal tb_A, tb_B : bit_vector(31 downto 0);
    signal tb_signed, tb_saturate : bit;
    signal tb_round_mode : bit_vector(1 downto 0);
    signal tb_result : bit_vector(31 downto 0);
    signal tb_overflow : bit;
begin
    UUT: entity work.FixedPointMul
        generic map (INT_BITS => 16, FRAC_BITS => 16)
        port map (
            A          => tb_A,
            B          => tb_B,
            is_signed  => tb_signed,
            round_mode => tb_round_mode,
            saturate   => tb_saturate,
            result     => tb_result,
            overflow   => tb_overflow
        );

    stimulus: process
    begin
        tb_signed <= '0';
        tb_round_mode <= "00";
        tb_saturate <= '0';

        -- Test: 1.0 * 1.0 = 1.0 (in 16.16 format)
        -- 1.0 = 0x00010000
        tb_A <= "00000000000000010000000000000000";
        tb_B <= "00000000000000010000000000000000";
        wait for 10 ns;

        -- Test: 2.0 * 0.5 = 1.0
        -- 2.0 = 0x00020000, 0.5 = 0x00008000
        tb_A <= "00000000000000100000000000000000";
        tb_B <= "00000000000000001000000000000000";
        wait for 10 ns;

        -- Test: 1.5 * 2.0 = 3.0
        -- 1.5 = 0x00018000, 2.0 = 0x00020000
        tb_A <= "00000000000000011000000000000000";
        tb_B <= "00000000000000100000000000000000";
        wait for 10 ns;

        -- Test signed: -1.0 * 2.0 = -2.0
        tb_signed <= '1';
        tb_A <= "11111111111111110000000000000000";  -- -1.0 in 2's comp
        tb_B <= "00000000000000100000000000000000";  -- 2.0
        wait for 10 ns;

        wait;
    end process;
end Test;
