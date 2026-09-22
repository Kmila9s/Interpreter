-- ============================================================================
-- VHDL Project: IIRFilter
-- Category: Signal Processing
-- Complexity: High
-- Description: Second-order IIR filter (biquad) with cascaded sections
--              Direct Form II Transposed implementation
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity IIRBiquad is
    generic (
        DATA_WIDTH : integer := 16;
        COEF_WIDTH : integer := 18;
        FRAC_BITS  : integer := 15
    );
    port (
        clk       : in  bit;
        reset     : in  bit;
        data_in   : in  bit_vector(DATA_WIDTH-1 downto 0);
        valid_in  : in  bit;
        data_out  : out bit_vector(DATA_WIDTH-1 downto 0);
        valid_out : out bit;
        -- Coefficients (b0, b1, b2, a1, a2) - a0 normalized to 1
        coef_b0   : in  bit_vector(COEF_WIDTH-1 downto 0);
        coef_b1   : in  bit_vector(COEF_WIDTH-1 downto 0);
        coef_b2   : in  bit_vector(COEF_WIDTH-1 downto 0);
        coef_a1   : in  bit_vector(COEF_WIDTH-1 downto 0);
        coef_a2   : in  bit_vector(COEF_WIDTH-1 downto 0)
    );
end IIRBiquad;

architecture Behavioral of IIRBiquad is
    constant ACC_WIDTH : integer := DATA_WIDTH + COEF_WIDTH + 2;

    -- State registers for Direct Form II Transposed
    signal w1, w2 : bit_vector(ACC_WIDTH-1 downto 0);
    signal valid_pipe : bit_vector(1 downto 0);

    function to_signed_int(bv : bit_vector) return integer is
        variable result : integer := 0;
        variable weight : integer := 1;
    begin
        for i in 0 to bv'length-2 loop
            if bv(i) = '1' then result := result + weight; end if;
            weight := weight * 2;
        end loop;
        if bv(bv'length-1) = '1' then result := result - weight; end if;
        return result;
    end function;

    function to_bv(val : integer; width : integer) return bit_vector is
        variable result : bit_vector(width-1 downto 0);
        variable temp : integer;
    begin
        if val < 0 then temp := val + (2 ** width);
        else temp := val; end if;
        for i in 0 to width-1 loop
            if (temp mod 2) = 1 then result(i) := '1';
            else result(i) := '0'; end if;
            temp := temp / 2;
        end loop;
        return result;
    end function;

    function saturate(val : integer; bits : integer) return integer is
        variable max_val : integer := (2 ** (bits-1)) - 1;
        variable min_val : integer := -(2 ** (bits-1));
    begin
        if val > max_val then return max_val;
        elsif val < min_val then return min_val;
        else return val; end if;
    end function;

begin
    -- Direct Form II Transposed Biquad
    -- y[n] = b0*x[n] + w1[n-1]
    -- w1[n] = b1*x[n] - a1*y[n] + w2[n-1]
    -- w2[n] = b2*x[n] - a2*y[n]

    process(clk, reset)
        variable x_in, y_out : integer;
        variable b0_prod, b1_prod, b2_prod : integer;
        variable a1_prod, a2_prod : integer;
        variable w1_next, w2_next : integer;
    begin
        if reset = '1' then
            w1 <= (others => '0');
            w2 <= (others => '0');
            valid_pipe <= "00";
            data_out <= (others => '0');
            valid_out <= '0';
        elsif clk'event and clk = '1' then
            valid_pipe <= valid_pipe(0) & valid_in;
            valid_out <= valid_pipe(1);

            if valid_in = '1' then
                x_in := to_signed_int(data_in);

                -- Compute output: y = b0*x + w1
                b0_prod := (x_in * to_signed_int(coef_b0)) / (2 ** FRAC_BITS);
                y_out := b0_prod + to_signed_int(w1);
                y_out := saturate(y_out, DATA_WIDTH);

                -- Update w1: w1 = b1*x - a1*y + w2
                b1_prod := (x_in * to_signed_int(coef_b1)) / (2 ** FRAC_BITS);
                a1_prod := (y_out * to_signed_int(coef_a1)) / (2 ** FRAC_BITS);
                w1_next := b1_prod - a1_prod + to_signed_int(w2);

                -- Update w2: w2 = b2*x - a2*y
                b2_prod := (x_in * to_signed_int(coef_b2)) / (2 ** FRAC_BITS);
                a2_prod := (y_out * to_signed_int(coef_a2)) / (2 ** FRAC_BITS);
                w2_next := b2_prod - a2_prod;

                w1 <= to_bv(w1_next, ACC_WIDTH);
                w2 <= to_bv(w2_next, ACC_WIDTH);
                data_out <= to_bv(y_out, DATA_WIDTH);
            end if;
        end if;
    end process;
end Behavioral;

-- ============================================================================
-- Cascaded Biquad Filter (4th order = 2 biquads)
-- ============================================================================
entity IIRCascade is
    generic (
        DATA_WIDTH : integer := 16;
        NUM_STAGES : integer := 2
    );
    port (
        clk       : in  bit;
        reset     : in  bit;
        data_in   : in  bit_vector(DATA_WIDTH-1 downto 0);
        valid_in  : in  bit;
        data_out  : out bit_vector(DATA_WIDTH-1 downto 0);
        valid_out : out bit
    );
end IIRCascade;

architecture Behavioral of IIRCascade is
    type stage_data is array (0 to NUM_STAGES) of bit_vector(DATA_WIDTH-1 downto 0);
    type stage_valid is array (0 to NUM_STAGES) of bit;

    signal inter_data : stage_data;
    signal inter_valid : stage_valid;

    -- Fixed coefficients for lowpass Butterworth (example)
    type coef_set is record
        b0, b1, b2 : bit_vector(17 downto 0);
        a1, a2 : bit_vector(17 downto 0);
    end record;

    type coef_array is array (0 to NUM_STAGES-1) of coef_set;

    -- Pre-computed coefficients (example values for Fc=0.1*Fs)
    constant COEFFICIENTS : coef_array := (
        0 => (b0 => "000000001000000000",  -- ~0.03
              b1 => "000000010000000000",  -- ~0.06
              b2 => "000000001000000000",  -- ~0.03
              a1 => "111110100000000000",  -- ~-0.75
              a2 => "000000011000000000"), -- ~0.19
        1 => (b0 => "000000001000000000",
              b1 => "000000010000000000",
              b2 => "000000001000000000",
              a1 => "111110100000000000",
              a2 => "000000011000000000")
    );

    -- State for each stage
    type acc_array is array (0 to NUM_STAGES-1) of bit_vector(35 downto 0);
    signal w1_reg, w2_reg : acc_array;

    function to_signed_int(bv : bit_vector) return integer is
        variable result : integer := 0;
        variable weight : integer := 1;
    begin
        for i in 0 to bv'length-2 loop
            if bv(i) = '1' then result := result + weight; end if;
            weight := weight * 2;
        end loop;
        if bv(bv'length-1) = '1' then result := result - weight; end if;
        return result;
    end function;

    function to_bv(val : integer; width : integer) return bit_vector is
        variable result : bit_vector(width-1 downto 0);
        variable temp : integer;
    begin
        if val < 0 then temp := val + (2 ** width);
        else temp := val; end if;
        for i in 0 to width-1 loop
            if (temp mod 2) = 1 then result(i) := '1';
            else result(i) := '0'; end if;
            temp := temp / 2;
        end loop;
        return result;
    end function;

begin
    inter_data(0) <= data_in;
    inter_valid(0) <= valid_in;

    -- Generate cascade stages
    gen_stages: for s in 0 to NUM_STAGES-1 generate
        process(clk, reset)
            variable x_in, y_out : integer;
            variable prod : integer;
            variable w1_next, w2_next : integer;
        begin
            if reset = '1' then
                w1_reg(s) <= (others => '0');
                w2_reg(s) <= (others => '0');
                inter_data(s+1) <= (others => '0');
                inter_valid(s+1) <= '0';
            elsif clk'event and clk = '1' then
                inter_valid(s+1) <= inter_valid(s);

                if inter_valid(s) = '1' then
                    x_in := to_signed_int(inter_data(s));

                    -- y = b0*x + w1
                    prod := (x_in * to_signed_int(COEFFICIENTS(s).b0)) / 32768;
                    y_out := prod + to_signed_int(w1_reg(s)(35 downto 20));

                    -- Saturate
                    if y_out > 32767 then y_out := 32767;
                    elsif y_out < -32768 then y_out := -32768;
                    end if;

                    -- w1 = b1*x - a1*y + w2
                    w1_next := (x_in * to_signed_int(COEFFICIENTS(s).b1)) / 32768
                              - (y_out * to_signed_int(COEFFICIENTS(s).a1)) / 32768
                              + to_signed_int(w2_reg(s)(35 downto 20));

                    -- w2 = b2*x - a2*y
                    w2_next := (x_in * to_signed_int(COEFFICIENTS(s).b2)) / 32768
                              - (y_out * to_signed_int(COEFFICIENTS(s).a2)) / 32768;

                    w1_reg(s) <= to_bv(w1_next, 36);
                    w2_reg(s) <= to_bv(w2_next, 36);
                    inter_data(s+1) <= to_bv(y_out, DATA_WIDTH);
                end if;
            end if;
        end process;
    end generate;

    data_out <= inter_data(NUM_STAGES);
    valid_out <= inter_valid(NUM_STAGES);
end Behavioral;

-- ============================================================================
-- First-order IIR (simple lowpass/highpass)
-- ============================================================================
entity IIRFirstOrder is
    generic (DATA_WIDTH : integer := 16);
    port (
        clk       : in  bit;
        reset     : in  bit;
        data_in   : in  bit_vector(DATA_WIDTH-1 downto 0);
        valid_in  : in  bit;
        alpha     : in  bit_vector(7 downto 0);  -- Filter coefficient 0-255
        mode      : in  bit;  -- '0' = lowpass, '1' = highpass
        data_out  : out bit_vector(DATA_WIDTH-1 downto 0);
        valid_out : out bit
    );
end IIRFirstOrder;

architecture Behavioral of IIRFirstOrder is
    signal y_prev : bit_vector(DATA_WIDTH-1 downto 0);

    function to_signed_int(bv : bit_vector) return integer is
        variable result : integer := 0;
        variable weight : integer := 1;
    begin
        for i in 0 to bv'length-2 loop
            if bv(i) = '1' then result := result + weight; end if;
            weight := weight * 2;
        end loop;
        if bv(bv'length-1) = '1' then result := result - weight; end if;
        return result;
    end function;

    function to_int(bv : bit_vector) return integer is
        variable result : integer := 0;
        variable power : integer := 1;
    begin
        for i in bv'low to bv'high loop
            if bv(i) = '1' then result := result + power; end if;
            power := power * 2;
        end loop;
        return result;
    end function;

    function to_bv(val : integer; width : integer) return bit_vector is
        variable result : bit_vector(width-1 downto 0);
        variable temp : integer;
    begin
        if val < 0 then temp := val + (2 ** width);
        else temp := val; end if;
        for i in 0 to width-1 loop
            if (temp mod 2) = 1 then result(i) := '1';
            else result(i) := '0'; end if;
            temp := temp / 2;
        end loop;
        return result;
    end function;

begin
    process(clk, reset)
        variable x, y, y_lp : integer;
        variable a : integer;
    begin
        if reset = '1' then
            y_prev <= (others => '0');
            data_out <= (others => '0');
            valid_out <= '0';
        elsif clk'event and clk = '1' then
            valid_out <= valid_in;

            if valid_in = '1' then
                x := to_signed_int(data_in);
                a := to_int(alpha);

                -- Lowpass: y = alpha*x + (1-alpha)*y_prev
                y_lp := (a * x + (256 - a) * to_signed_int(y_prev)) / 256;

                if mode = '0' then
                    y := y_lp;
                else
                    -- Highpass: y = x - lowpass(x)
                    y := x - y_lp;
                end if;

                y_prev <= to_bv(y_lp, DATA_WIDTH);
                data_out <= to_bv(y, DATA_WIDTH);
            end if;
        end if;
    end process;
end Behavioral;
