-- ============================================================================
-- VHDL Project: Decimator
-- Category: Signal Processing
-- Complexity: Medium
-- Description: Sample rate decimator with anti-aliasing filter
--              CIC and FIR decimation implementations
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity CICDecimator is
    generic (
        DATA_WIDTH : integer := 16;
        ORDER      : integer := 3;   -- Number of stages
        DECIM_RATE : integer := 8    -- Decimation factor
    );
    port (
        clk       : in  bit;
        reset     : in  bit;
        data_in   : in  bit_vector(DATA_WIDTH-1 downto 0);
        valid_in  : in  bit;
        data_out  : out bit_vector(DATA_WIDTH-1 downto 0);
        valid_out : out bit
    );
end CICDecimator;

architecture Behavioral of CICDecimator is
    -- Extended width to prevent overflow
    constant EXT_WIDTH : integer := DATA_WIDTH + ORDER * 4;  -- log2(DECIM_RATE^ORDER)

    type integrator_array is array (0 to ORDER-1) of bit_vector(EXT_WIDTH-1 downto 0);
    type comb_array is array (0 to ORDER-1) of bit_vector(EXT_WIDTH-1 downto 0);
    type delay_array is array (0 to ORDER-1) of bit_vector(EXT_WIDTH-1 downto 0);

    signal integrators : integrator_array;
    signal combs : comb_array;
    signal comb_delay : delay_array;

    signal decim_cnt : integer range 0 to DECIM_RATE-1;
    signal decim_sample : bit_vector(EXT_WIDTH-1 downto 0);
    signal decim_valid : bit;

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

    function sign_extend(bv : bit_vector; new_width : integer) return bit_vector is
        variable result : bit_vector(new_width-1 downto 0);
    begin
        result(bv'length-1 downto 0) := bv;
        for i in bv'length to new_width-1 loop
            result(i) := bv(bv'length-1);  -- Sign extend
        end loop;
        return result;
    end function;

begin
    -- CIC Filter: Integrator -> Decimator -> Comb
    process(clk, reset)
        variable int_val, comb_val : integer;
    begin
        if reset = '1' then
            for i in 0 to ORDER-1 loop
                integrators(i) <= (others => '0');
                combs(i) <= (others => '0');
                comb_delay(i) <= (others => '0');
            end loop;
            decim_cnt <= 0;
            decim_sample <= (others => '0');
            decim_valid <= '0';
            data_out <= (others => '0');
            valid_out <= '0';
        elsif clk'event and clk = '1' then
            decim_valid <= '0';
            valid_out <= '0';

            if valid_in = '1' then
                -- Integrator stages (running at input rate)
                int_val := to_signed_int(sign_extend(data_in, EXT_WIDTH));
                for i in 0 to ORDER-1 loop
                    if i = 0 then
                        int_val := int_val + to_signed_int(integrators(i));
                    else
                        int_val := to_signed_int(integrators(i-1)) + to_signed_int(integrators(i));
                    end if;
                    integrators(i) <= to_bv(int_val, EXT_WIDTH);
                end loop;

                -- Decimation
                if decim_cnt = DECIM_RATE - 1 then
                    decim_cnt <= 0;
                    decim_sample <= integrators(ORDER-1);
                    decim_valid <= '1';
                else
                    decim_cnt <= decim_cnt + 1;
                end if;
            end if;

            -- Comb stages (running at output rate)
            if decim_valid = '1' then
                for i in 0 to ORDER-1 loop
                    if i = 0 then
                        comb_val := to_signed_int(decim_sample) - to_signed_int(comb_delay(i));
                        comb_delay(i) <= decim_sample;
                    else
                        comb_val := to_signed_int(combs(i-1)) - to_signed_int(comb_delay(i));
                        comb_delay(i) <= combs(i-1);
                    end if;
                    combs(i) <= to_bv(comb_val, EXT_WIDTH);
                end loop;

                -- Output (truncate/round to original width)
                data_out <= combs(ORDER-1)(EXT_WIDTH-1 downto EXT_WIDTH-DATA_WIDTH);
                valid_out <= '1';
            end if;
        end if;
    end process;
end Behavioral;

-- ============================================================================
-- FIR Decimator (polyphase structure)
-- ============================================================================
entity FIRDecimator is
    generic (
        DATA_WIDTH : integer := 16;
        NUM_TAPS   : integer := 16;
        DECIM_RATE : integer := 4
    );
    port (
        clk       : in  bit;
        reset     : in  bit;
        data_in   : in  bit_vector(DATA_WIDTH-1 downto 0);
        valid_in  : in  bit;
        data_out  : out bit_vector(DATA_WIDTH-1 downto 0);
        valid_out : out bit
    );
end FIRDecimator;

architecture Behavioral of FIRDecimator is
    type data_array is array (0 to NUM_TAPS-1) of bit_vector(DATA_WIDTH-1 downto 0);
    type coef_array is array (0 to NUM_TAPS-1) of integer;

    signal delay_line : data_array;
    signal input_cnt : integer range 0 to DECIM_RATE-1;

    -- Example lowpass filter coefficients (scaled by 256)
    constant COEFFICIENTS : coef_array := (
        4, 8, 16, 32, 48, 64, 72, 80,
        80, 72, 64, 48, 32, 16, 8, 4
    );

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
        variable acc : integer;
    begin
        if reset = '1' then
            for i in 0 to NUM_TAPS-1 loop
                delay_line(i) <= (others => '0');
            end loop;
            input_cnt <= 0;
            data_out <= (others => '0');
            valid_out <= '0';
        elsif clk'event and clk = '1' then
            valid_out <= '0';

            if valid_in = '1' then
                -- Shift delay line
                for i in NUM_TAPS-1 downto 1 loop
                    delay_line(i) <= delay_line(i-1);
                end loop;
                delay_line(0) <= data_in;

                input_cnt <= input_cnt + 1;

                -- Output every DECIM_RATE samples
                if input_cnt = DECIM_RATE - 1 then
                    input_cnt <= 0;

                    -- Compute filtered output
                    acc := 0;
                    for i in 0 to NUM_TAPS-1 loop
                        acc := acc + to_signed_int(delay_line(i)) * COEFFICIENTS(i);
                    end loop;

                    -- Scale and output
                    data_out <= to_bv(acc / 512, DATA_WIDTH);
                    valid_out <= '1';
                end if;
            end if;
        end if;
    end process;
end Behavioral;

-- ============================================================================
-- Simple Decimator (no filtering - just sample selection)
-- ============================================================================
entity SimpleDecimator is
    generic (
        DATA_WIDTH : integer := 16;
        DECIM_RATE : integer := 4
    );
    port (
        clk       : in  bit;
        reset     : in  bit;
        data_in   : in  bit_vector(DATA_WIDTH-1 downto 0);
        valid_in  : in  bit;
        data_out  : out bit_vector(DATA_WIDTH-1 downto 0);
        valid_out : out bit
    );
end SimpleDecimator;

architecture Behavioral of SimpleDecimator is
    signal cnt : integer range 0 to DECIM_RATE-1;
begin
    process(clk, reset)
    begin
        if reset = '1' then
            cnt <= 0;
            data_out <= (others => '0');
            valid_out <= '0';
        elsif clk'event and clk = '1' then
            valid_out <= '0';

            if valid_in = '1' then
                if cnt = 0 then
                    data_out <= data_in;
                    valid_out <= '1';
                end if;

                if cnt = DECIM_RATE - 1 then
                    cnt <= 0;
                else
                    cnt <= cnt + 1;
                end if;
            end if;
        end if;
    end process;
end Behavioral;
