-- ============================================================================
-- VHDL Project: FIRFilter
-- Category: Signal Processing
-- Complexity: Medium-High
-- Description: Configurable FIR filter with symmetric coefficients
--              Direct form implementation, parallel multiply-accumulate
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity FIRFilter is
    generic (
        DATA_WIDTH : integer := 16;
        COEF_WIDTH : integer := 16;
        NUM_TAPS   : integer := 8
    );
    port (
        clk         : in  bit;
        reset       : in  bit;
        data_in     : in  bit_vector(DATA_WIDTH-1 downto 0);
        data_valid  : in  bit;
        data_out    : out bit_vector(DATA_WIDTH-1 downto 0);
        out_valid   : out bit;
        -- Coefficient loading
        coef_addr   : in  bit_vector(3 downto 0);
        coef_data   : in  bit_vector(COEF_WIDTH-1 downto 0);
        coef_we     : in  bit
    );
end FIRFilter;

architecture Behavioral of FIRFilter is
    type data_array is array (0 to NUM_TAPS-1) of bit_vector(DATA_WIDTH-1 downto 0);
    type coef_array is array (0 to NUM_TAPS-1) of bit_vector(COEF_WIDTH-1 downto 0);

    signal delay_line : data_array;
    signal coefficients : coef_array;
    signal accumulator : bit_vector(DATA_WIDTH+COEF_WIDTH+3 downto 0);
    signal valid_pipe : bit_vector(2 downto 0);

    -- Convert bit_vector to integer (signed)
    function to_signed_int(bv : bit_vector) return integer is
        variable result : integer := 0;
        variable weight : integer := 1;
    begin
        for i in 0 to bv'length-2 loop
            if bv(i) = '1' then
                result := result + weight;
            end if;
            weight := weight * 2;
        end loop;
        if bv(bv'length-1) = '1' then
            result := result - weight;
        end if;
        return result;
    end function;

    -- Convert integer to bit_vector
    function to_bv(val : integer; width : integer) return bit_vector is
        variable result : bit_vector(width-1 downto 0);
        variable temp : integer;
    begin
        if val < 0 then
            temp := val + (2 ** width);
        else
            temp := val;
        end if;
        for i in 0 to width-1 loop
            if (temp mod 2) = 1 then
                result(i) := '1';
            else
                result(i) := '0';
            end if;
            temp := temp / 2;
        end loop;
        return result;
    end function;

    function to_int(bv : bit_vector) return integer is
        variable result : integer := 0;
        variable power : integer := 1;
    begin
        for i in bv'low to bv'high loop
            if bv(i) = '1' then
                result := result + power;
            end if;
            power := power * 2;
        end loop;
        return result;
    end function;

begin
    -- Coefficient loading
    coef_load: process(clk, reset)
    begin
        if reset = '1' then
            for i in 0 to NUM_TAPS-1 loop
                coefficients(i) <= (others => '0');
            end loop;
        elsif clk'event and clk = '1' then
            if coef_we = '1' then
                coefficients(to_int(coef_addr) mod NUM_TAPS) <= coef_data;
            end if;
        end if;
    end process;

    -- Main FIR filter process
    fir_proc: process(clk, reset)
        variable sum : integer;
        variable prod : integer;
    begin
        if reset = '1' then
            for i in 0 to NUM_TAPS-1 loop
                delay_line(i) <= (others => '0');
            end loop;
            accumulator <= (others => '0');
            valid_pipe <= "000";
            data_out <= (others => '0');
            out_valid <= '0';
        elsif clk'event and clk = '1' then
            valid_pipe <= valid_pipe(1 downto 0) & data_valid;
            out_valid <= valid_pipe(2);

            if data_valid = '1' then
                -- Shift delay line
                for i in NUM_TAPS-1 downto 1 loop
                    delay_line(i) <= delay_line(i-1);
                end loop;
                delay_line(0) <= data_in;

                -- Compute FIR output (MAC operation)
                sum := 0;
                for i in 0 to NUM_TAPS-1 loop
                    prod := to_signed_int(delay_line(i)) * to_signed_int(coefficients(i));
                    sum := sum + prod;
                end loop;

                -- Scale output (divide by 2^(COEF_WIDTH-1) for fixed-point)
                sum := sum / (2 ** (COEF_WIDTH - 1));

                -- Saturate to output range
                if sum > (2 ** (DATA_WIDTH-1)) - 1 then
                    sum := (2 ** (DATA_WIDTH-1)) - 1;
                elsif sum < -(2 ** (DATA_WIDTH-1)) then
                    sum := -(2 ** (DATA_WIDTH-1));
                end if;

                data_out <= to_bv(sum, DATA_WIDTH);
            end if;
        end if;
    end process;
end Behavioral;

-- ============================================================================
-- Symmetric FIR Filter (reduced multipliers)
-- ============================================================================
entity FIRSymmetric is
    generic (
        DATA_WIDTH : integer := 16;
        NUM_TAPS   : integer := 16  -- Must be even
    );
    port (
        clk       : in  bit;
        reset     : in  bit;
        data_in   : in  bit_vector(DATA_WIDTH-1 downto 0);
        valid_in  : in  bit;
        data_out  : out bit_vector(DATA_WIDTH-1 downto 0);
        valid_out : out bit
    );
end FIRSymmetric;

architecture Behavioral of FIRSymmetric is
    type data_array is array (0 to NUM_TAPS-1) of bit_vector(DATA_WIDTH-1 downto 0);
    signal delay_line : data_array;

    -- Symmetric pre-add results
    type preadd_array is array (0 to NUM_TAPS/2-1) of bit_vector(DATA_WIDTH downto 0);
    signal pre_add : preadd_array;

    signal valid_d1, valid_d2 : bit;

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
    process(clk, reset)
        variable sum_a, sum_b : integer;
    begin
        if reset = '1' then
            for i in 0 to NUM_TAPS-1 loop
                delay_line(i) <= (others => '0');
            end loop;
            for i in 0 to NUM_TAPS/2-1 loop
                pre_add(i) <= (others => '0');
            end loop;
            valid_d1 <= '0';
            valid_d2 <= '0';
            data_out <= (others => '0');
            valid_out <= '0';
        elsif clk'event and clk = '1' then
            valid_d1 <= valid_in;
            valid_d2 <= valid_d1;
            valid_out <= valid_d2;

            if valid_in = '1' then
                -- Shift delay line
                for i in NUM_TAPS-1 downto 1 loop
                    delay_line(i) <= delay_line(i-1);
                end loop;
                delay_line(0) <= data_in;
            end if;

            if valid_d1 = '1' then
                -- Symmetric pre-addition (reduces multipliers by half)
                for i in 0 to NUM_TAPS/2-1 loop
                    sum_a := to_signed_int(delay_line(i));
                    sum_b := to_signed_int(delay_line(NUM_TAPS-1-i));
                    pre_add(i) <= to_bv(sum_a + sum_b, DATA_WIDTH+1);
                end loop;
            end if;

            -- Final accumulation would happen here with coefficients
            -- Output is delayed version for demonstration
            if valid_d2 = '1' then
                data_out <= delay_line(NUM_TAPS/2);
            end if;
        end if;
    end process;
end Behavioral;
