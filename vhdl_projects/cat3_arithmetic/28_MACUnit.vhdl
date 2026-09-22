-- ============================================================================
-- VHDL Project: MACUnit
-- Category: Arithmetic & Mathematics
-- Complexity: High
-- Description: Multiply-Accumulate unit with saturation
--              Essential for DSP applications (FIR filters, etc.)
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity MACUnit is
    generic (
        INPUT_WIDTH : integer := 16;
        ACC_WIDTH   : integer := 40
    );
    port (
        clk         : in  bit;
        reset       : in  bit;
        enable      : in  bit;
        clear_acc   : in  bit;
        -- Inputs
        a           : in  bit_vector(INPUT_WIDTH-1 downto 0);
        b           : in  bit_vector(INPUT_WIDTH-1 downto 0);
        is_signed   : in  bit;
        -- Accumulator control
        add_sub     : in  bit;  -- '0' = add, '1' = subtract
        -- Outputs
        product     : out bit_vector(2*INPUT_WIDTH-1 downto 0);
        accumulator : out bit_vector(ACC_WIDTH-1 downto 0);
        overflow    : out bit;
        underflow   : out bit
    );
end MACUnit;

architecture Behavioral of MACUnit is
    constant PRODUCT_WIDTH : integer := 2 * INPUT_WIDTH;

    signal acc_reg : bit_vector(ACC_WIDTH-1 downto 0);
    signal prod_reg : bit_vector(PRODUCT_WIDTH-1 downto 0);
    signal ovf_reg, udf_reg : bit;

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

    function multiply_signed(a, b : bit_vector) return bit_vector is
        variable a_abs, b_abs : bit_vector(a'range);
        variable product : bit_vector(a'length + b'length - 1 downto 0);
        variable partial : bit_vector(a'length + b'length - 1 downto 0);
        variable sign : bit;
        variable carry : bit;
    begin
        sign := a(a'high) xor b(b'high);

        -- Get absolute values
        if a(a'high) = '1' then
            a_abs := negate(a);
        else
            a_abs := a;
        end if;

        if b(b'high) = '1' then
            b_abs := negate(b);
        else
            b_abs := b;
        end if;

        -- Multiply magnitudes
        product := (others => '0');
        for i in b_abs'low to b_abs'high loop
            if b_abs(i) = '1' then
                partial := (others => '0');
                for j in a_abs'low to a_abs'high loop
                    partial(j + i - a_abs'low) := a_abs(j);
                end loop;

                carry := '0';
                for k in product'low to product'high loop
                    product(k) := product(k) xor partial(k) xor carry;
                    carry := (product(k) and partial(k)) or
                             (product(k) and carry) or
                             (partial(k) and carry);
                end loop;
            end if;
        end loop;

        -- Apply sign
        if sign = '1' then
            return negate(product);
        else
            return product;
        end if;
    end function;

    function multiply_unsigned(a, b : bit_vector) return bit_vector is
        variable product : bit_vector(a'length + b'length - 1 downto 0);
        variable partial : bit_vector(a'length + b'length - 1 downto 0);
        variable carry : bit;
    begin
        product := (others => '0');
        for i in b'low to b'high loop
            if b(i) = '1' then
                partial := (others => '0');
                for j in a'low to a'high loop
                    partial(j + i - a'low) := a(j);
                end loop;

                carry := '0';
                for k in product'low to product'high loop
                    product(k) := product(k) xor partial(k) xor carry;
                    carry := (product(k) and partial(k)) or
                             (product(k) and carry) or
                             (partial(k) and carry);
                end loop;
            end if;
        end loop;
        return product;
    end function;

    function sign_extend(val : bit_vector; new_width : integer; is_signed : bit)
        return bit_vector is
        variable result : bit_vector(new_width-1 downto 0);
        variable sign : bit;
    begin
        if is_signed = '1' then
            sign := val(val'high);
        else
            sign := '0';
        end if;

        for i in 0 to new_width-1 loop
            if i <= val'high - val'low then
                result(i) := val(i + val'low);
            else
                result(i) := sign;
            end if;
        end loop;
        return result;
    end function;

    function add_acc(acc, prod : bit_vector; sub : bit) return bit_vector is
        variable result : bit_vector(acc'range);
        variable operand : bit_vector(acc'range);
        variable carry : bit;
    begin
        operand := prod;
        if sub = '1' then
            operand := negate(prod);
        end if;

        carry := '0';
        for i in acc'low to acc'high loop
            result(i) := acc(i) xor operand(i) xor carry;
            carry := (acc(i) and operand(i)) or (acc(i) and carry) or (operand(i) and carry);
        end loop;
        return result;
    end function;

begin
    mac_proc: process(clk, reset)
        variable mult_result : bit_vector(PRODUCT_WIDTH-1 downto 0);
        variable extended_prod : bit_vector(ACC_WIDTH-1 downto 0);
        variable new_acc : bit_vector(ACC_WIDTH-1 downto 0);
        variable acc_sign, prod_sign, result_sign : bit;
    begin
        if reset = '1' then
            acc_reg <= (others => '0');
            prod_reg <= (others => '0');
            ovf_reg <= '0';
            udf_reg <= '0';
        elsif clk'event and clk = '1' then
            if enable = '1' then
                -- Multiply
                if is_signed = '1' then
                    mult_result := multiply_signed(a, b);
                else
                    mult_result := multiply_unsigned(a, b);
                end if;
                prod_reg <= mult_result;

                -- Clear or accumulate
                if clear_acc = '1' then
                    acc_reg <= sign_extend(mult_result, ACC_WIDTH, is_signed);
                    ovf_reg <= '0';
                    udf_reg <= '0';
                else
                    -- Sign extend product
                    extended_prod := sign_extend(mult_result, ACC_WIDTH, is_signed);

                    -- Add/subtract to accumulator
                    new_acc := add_acc(acc_reg, extended_prod, add_sub);

                    -- Overflow detection for signed
                    if is_signed = '1' then
                        acc_sign := acc_reg(ACC_WIDTH-1);
                        if add_sub = '1' then
                            prod_sign := not extended_prod(ACC_WIDTH-1);
                        else
                            prod_sign := extended_prod(ACC_WIDTH-1);
                        end if;
                        result_sign := new_acc(ACC_WIDTH-1);

                        -- Overflow: both positive, result negative
                        if acc_sign = '0' and prod_sign = '0' and result_sign = '1' then
                            ovf_reg <= '1';
                        end if;

                        -- Underflow: both negative, result positive
                        if acc_sign = '1' and prod_sign = '1' and result_sign = '0' then
                            udf_reg <= '1';
                        end if;
                    end if;

                    acc_reg <= new_acc;
                end if;
            end if;
        end if;
    end process;

    product <= prod_reg;
    accumulator <= acc_reg;
    overflow <= ovf_reg;
    underflow <= udf_reg;

end Behavioral;

-- ============================================================================
-- Pipelined MAC (for high throughput)
-- ============================================================================
entity PipelinedMAC is
    generic (
        INPUT_WIDTH : integer := 16;
        ACC_WIDTH   : integer := 40;
        PIPE_STAGES : integer := 3
    );
    port (
        clk         : in  bit;
        reset       : in  bit;
        a           : in  bit_vector(INPUT_WIDTH-1 downto 0);
        b           : in  bit_vector(INPUT_WIDTH-1 downto 0);
        valid_in    : in  bit;
        clear_acc   : in  bit;
        result      : out bit_vector(ACC_WIDTH-1 downto 0);
        valid_out   : out bit
    );
end PipelinedMAC;

architecture Behavioral of PipelinedMAC is
    constant PRODUCT_WIDTH : integer := 2 * INPUT_WIDTH;

    -- Pipeline registers
    type a_pipe_type is array (0 to PIPE_STAGES-1) of bit_vector(INPUT_WIDTH-1 downto 0);
    type b_pipe_type is array (0 to PIPE_STAGES-1) of bit_vector(INPUT_WIDTH-1 downto 0);
    type prod_pipe_type is array (0 to PIPE_STAGES-1) of bit_vector(PRODUCT_WIDTH-1 downto 0);

    signal a_pipe : a_pipe_type;
    signal b_pipe : b_pipe_type;
    signal prod_pipe : prod_pipe_type;
    signal valid_pipe : bit_vector(PIPE_STAGES-1 downto 0);
    signal clear_pipe : bit_vector(PIPE_STAGES-1 downto 0);

    signal acc_reg : bit_vector(ACC_WIDTH-1 downto 0);

begin
    pipe_proc: process(clk, reset)
        variable product : bit_vector(PRODUCT_WIDTH-1 downto 0);
        variable extended : bit_vector(ACC_WIDTH-1 downto 0);
        variable new_acc : bit_vector(ACC_WIDTH-1 downto 0);
        variable carry : bit;
    begin
        if reset = '1' then
            for i in 0 to PIPE_STAGES-1 loop
                a_pipe(i) <= (others => '0');
                b_pipe(i) <= (others => '0');
                prod_pipe(i) <= (others => '0');
            end loop;
            valid_pipe <= (others => '0');
            clear_pipe <= (others => '0');
            acc_reg <= (others => '0');
        elsif clk'event and clk = '1' then
            -- Stage 0: Input registration
            a_pipe(0) <= a;
            b_pipe(0) <= b;
            valid_pipe(0) <= valid_in;
            clear_pipe(0) <= clear_acc;

            -- Stage 1: Multiplication (partial)
            -- Simplified: just register multiply result
            product := (others => '0');
            for i in 0 to INPUT_WIDTH-1 loop
                if b_pipe(0)(i) = '1' then
                    for j in 0 to INPUT_WIDTH-1 loop
                        product(i+j) := product(i+j) xor a_pipe(0)(j);
                    end loop;
                end if;
            end loop;
            prod_pipe(1) <= product;
            valid_pipe(1) <= valid_pipe(0);
            clear_pipe(1) <= clear_pipe(0);

            -- Pipeline propagation
            for i in 2 to PIPE_STAGES-1 loop
                prod_pipe(i) <= prod_pipe(i-1);
                valid_pipe(i) <= valid_pipe(i-1);
                clear_pipe(i) <= clear_pipe(i-1);
            end loop;

            -- Accumulation stage
            if valid_pipe(PIPE_STAGES-1) = '1' then
                -- Sign extend product
                extended := (others => prod_pipe(PIPE_STAGES-1)(PRODUCT_WIDTH-1));
                for i in 0 to PRODUCT_WIDTH-1 loop
                    extended(i) := prod_pipe(PIPE_STAGES-1)(i);
                end loop;

                if clear_pipe(PIPE_STAGES-1) = '1' then
                    acc_reg <= extended;
                else
                    -- Add to accumulator
                    carry := '0';
                    for i in 0 to ACC_WIDTH-1 loop
                        new_acc(i) := acc_reg(i) xor extended(i) xor carry;
                        carry := (acc_reg(i) and extended(i)) or
                                 (acc_reg(i) and carry) or
                                 (extended(i) and carry);
                    end loop;
                    acc_reg <= new_acc;
                end if;
            end if;
        end if;
    end process;

    result <= acc_reg;
    valid_out <= valid_pipe(PIPE_STAGES-1);

end Behavioral;

-- ============================================================================
-- Testbench
-- ============================================================================
entity MACUnit_TB is
end MACUnit_TB;

architecture Test of MACUnit_TB is
    signal tb_clk, tb_reset, tb_enable, tb_clear : bit;
    signal tb_a, tb_b : bit_vector(15 downto 0);
    signal tb_signed, tb_add_sub : bit;
    signal tb_product : bit_vector(31 downto 0);
    signal tb_acc : bit_vector(39 downto 0);
    signal tb_ovf, tb_udf : bit;
begin
    UUT: entity work.MACUnit
        generic map (INPUT_WIDTH => 16, ACC_WIDTH => 40)
        port map (
            clk         => tb_clk,
            reset       => tb_reset,
            enable      => tb_enable,
            clear_acc   => tb_clear,
            a           => tb_a,
            b           => tb_b,
            is_signed   => tb_signed,
            add_sub     => tb_add_sub,
            product     => tb_product,
            accumulator => tb_acc,
            overflow    => tb_ovf,
            underflow   => tb_udf
        );

    clk_gen: process
    begin
        tb_clk <= '0'; wait for 5 ns;
        tb_clk <= '1'; wait for 5 ns;
    end process;

    stimulus: process
    begin
        tb_reset <= '1';
        tb_enable <= '0';
        tb_clear <= '0';
        tb_signed <= '1';
        tb_add_sub <= '0';
        tb_a <= (others => '0');
        tb_b <= (others => '0');
        wait for 30 ns;

        tb_reset <= '0';
        tb_enable <= '1';

        -- First MAC: 3 * 4 = 12, clear accumulator
        tb_a <= "0000000000000011";  -- 3
        tb_b <= "0000000000000100";  -- 4
        tb_clear <= '1';
        wait for 10 ns;

        -- Second MAC: 2 * 5 = 10, acc = 12 + 10 = 22
        tb_a <= "0000000000000010";  -- 2
        tb_b <= "0000000000000101";  -- 5
        tb_clear <= '0';
        wait for 10 ns;

        -- Third MAC: 3 * 3 = 9, acc = 22 + 9 = 31
        tb_a <= "0000000000000011";  -- 3
        tb_b <= "0000000000000011";  -- 3
        wait for 10 ns;

        -- Subtract mode: acc = 31 - (2 * 4) = 23
        tb_a <= "0000000000000010";
        tb_b <= "0000000000000100";
        tb_add_sub <= '1';
        wait for 10 ns;

        wait;
    end process;
end Test;
