-- ============================================================================
-- VHDL Project: Interpolator
-- Category: Signal Processing
-- Complexity: Medium
-- Description: Sample rate interpolator with zero-stuffing and filtering
--              Linear interpolation and CIC interpolation
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity LinearInterpolator is
    generic (
        DATA_WIDTH   : integer := 16;
        INTERP_RATE  : integer := 4
    );
    port (
        clk       : in  bit;
        reset     : in  bit;
        data_in   : in  bit_vector(DATA_WIDTH-1 downto 0);
        valid_in  : in  bit;
        data_out  : out bit_vector(DATA_WIDTH-1 downto 0);
        valid_out : out bit
    );
end LinearInterpolator;

architecture Behavioral of LinearInterpolator is
    signal prev_sample : bit_vector(DATA_WIDTH-1 downto 0);
    signal curr_sample : bit_vector(DATA_WIDTH-1 downto 0);
    signal interp_cnt : integer range 0 to INTERP_RATE-1;
    signal running : bit;

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
        variable prev_val, curr_val : integer;
        variable interp_val : integer;
        variable delta : integer;
    begin
        if reset = '1' then
            prev_sample <= (others => '0');
            curr_sample <= (others => '0');
            interp_cnt <= 0;
            running <= '0';
            data_out <= (others => '0');
            valid_out <= '0';
        elsif clk'event and clk = '1' then
            valid_out <= '0';

            if valid_in = '1' then
                prev_sample <= curr_sample;
                curr_sample <= data_in;
                interp_cnt <= 0;
                running <= '1';
            end if;

            if running = '1' then
                prev_val := to_signed_int(prev_sample);
                curr_val := to_signed_int(curr_sample);
                delta := curr_val - prev_val;

                -- Linear interpolation: y = prev + (curr - prev) * n / INTERP_RATE
                interp_val := prev_val + (delta * interp_cnt) / INTERP_RATE;
                data_out <= to_bv(interp_val, DATA_WIDTH);
                valid_out <= '1';

                if interp_cnt = INTERP_RATE - 1 then
                    interp_cnt <= 0;
                    running <= '0';
                else
                    interp_cnt <= interp_cnt + 1;
                end if;
            end if;
        end if;
    end process;
end Behavioral;

-- ============================================================================
-- CIC Interpolator
-- ============================================================================
entity CICInterpolator is
    generic (
        DATA_WIDTH  : integer := 16;
        ORDER       : integer := 3;
        INTERP_RATE : integer := 8
    );
    port (
        clk       : in  bit;
        reset     : in  bit;
        data_in   : in  bit_vector(DATA_WIDTH-1 downto 0);
        valid_in  : in  bit;
        data_out  : out bit_vector(DATA_WIDTH-1 downto 0);
        valid_out : out bit
    );
end CICInterpolator;

architecture Behavioral of CICInterpolator is
    constant EXT_WIDTH : integer := DATA_WIDTH + ORDER * 4;

    type comb_array is array (0 to ORDER-1) of bit_vector(EXT_WIDTH-1 downto 0);
    type integ_array is array (0 to ORDER-1) of bit_vector(EXT_WIDTH-1 downto 0);
    type delay_array is array (0 to ORDER-1) of bit_vector(EXT_WIDTH-1 downto 0);

    signal combs : comb_array;
    signal comb_delay : delay_array;
    signal integrators : integ_array;

    signal interp_cnt : integer range 0 to INTERP_RATE-1;
    signal interp_sample : bit_vector(EXT_WIDTH-1 downto 0);
    signal interp_running : bit;

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
            result(i) := bv(bv'length-1);
        end loop;
        return result;
    end function;

begin
    -- CIC Interpolator: Comb -> Interpolate -> Integrator
    process(clk, reset)
        variable comb_val, integ_val : integer;
        variable input_ext : bit_vector(EXT_WIDTH-1 downto 0);
    begin
        if reset = '1' then
            for i in 0 to ORDER-1 loop
                combs(i) <= (others => '0');
                comb_delay(i) <= (others => '0');
                integrators(i) <= (others => '0');
            end loop;
            interp_cnt <= 0;
            interp_sample <= (others => '0');
            interp_running <= '0';
            data_out <= (others => '0');
            valid_out <= '0';
        elsif clk'event and clk = '1' then
            valid_out <= '0';

            -- Comb stages (running at input rate)
            if valid_in = '1' then
                input_ext := sign_extend(data_in, EXT_WIDTH);

                for i in 0 to ORDER-1 loop
                    if i = 0 then
                        comb_val := to_signed_int(input_ext) - to_signed_int(comb_delay(i));
                        comb_delay(i) <= input_ext;
                    else
                        comb_val := to_signed_int(combs(i-1)) - to_signed_int(comb_delay(i));
                        comb_delay(i) <= combs(i-1);
                    end if;
                    combs(i) <= to_bv(comb_val, EXT_WIDTH);
                end loop;

                interp_sample <= combs(ORDER-1);
                interp_cnt <= 0;
                interp_running <= '1';
            end if;

            -- Zero-stuffing and integration (running at output rate)
            if interp_running = '1' then
                -- First sample is the comb output, rest are zeros
                if interp_cnt = 0 then
                    integ_val := to_signed_int(interp_sample);
                else
                    integ_val := 0;
                end if;

                -- Integrator stages
                for i in 0 to ORDER-1 loop
                    if i = 0 then
                        integ_val := integ_val + to_signed_int(integrators(i));
                    else
                        integ_val := to_signed_int(integrators(i-1)) + to_signed_int(integrators(i));
                    end if;
                    integrators(i) <= to_bv(integ_val, EXT_WIDTH);
                end loop;

                -- Output (scaled down)
                data_out <= integrators(ORDER-1)(EXT_WIDTH-1 downto EXT_WIDTH-DATA_WIDTH);
                valid_out <= '1';

                if interp_cnt = INTERP_RATE - 1 then
                    interp_running <= '0';
                else
                    interp_cnt <= interp_cnt + 1;
                end if;
            end if;
        end if;
    end process;
end Behavioral;

-- ============================================================================
-- Zero-Order Hold Interpolator
-- ============================================================================
entity ZeroOrderHold is
    generic (
        DATA_WIDTH  : integer := 16;
        INTERP_RATE : integer := 4
    );
    port (
        clk       : in  bit;
        reset     : in  bit;
        data_in   : in  bit_vector(DATA_WIDTH-1 downto 0);
        valid_in  : in  bit;
        data_out  : out bit_vector(DATA_WIDTH-1 downto 0);
        valid_out : out bit
    );
end ZeroOrderHold;

architecture Behavioral of ZeroOrderHold is
    signal hold_value : bit_vector(DATA_WIDTH-1 downto 0);
    signal cnt : integer range 0 to INTERP_RATE-1;
    signal running : bit;
begin
    process(clk, reset)
    begin
        if reset = '1' then
            hold_value <= (others => '0');
            cnt <= 0;
            running <= '0';
            data_out <= (others => '0');
            valid_out <= '0';
        elsif clk'event and clk = '1' then
            valid_out <= '0';

            if valid_in = '1' then
                hold_value <= data_in;
                cnt <= 0;
                running <= '1';
            end if;

            if running = '1' then
                data_out <= hold_value;
                valid_out <= '1';

                if cnt = INTERP_RATE - 1 then
                    running <= '0';
                else
                    cnt <= cnt + 1;
                end if;
            end if;
        end if;
    end process;
end Behavioral;

-- ============================================================================
-- Polyphase FIR Interpolator
-- ============================================================================
entity PolyphaseInterpolator is
    generic (
        DATA_WIDTH  : integer := 16;
        NUM_TAPS    : integer := 8;
        INTERP_RATE : integer := 4
    );
    port (
        clk       : in  bit;
        reset     : in  bit;
        data_in   : in  bit_vector(DATA_WIDTH-1 downto 0);
        valid_in  : in  bit;
        data_out  : out bit_vector(DATA_WIDTH-1 downto 0);
        valid_out : out bit
    );
end PolyphaseInterpolator;

architecture Behavioral of PolyphaseInterpolator is
    type data_array is array (0 to NUM_TAPS-1) of bit_vector(DATA_WIDTH-1 downto 0);
    type coef_2d is array (0 to INTERP_RATE-1, 0 to NUM_TAPS-1) of integer;

    signal delay_line : data_array;
    signal phase : integer range 0 to INTERP_RATE-1;
    signal running : bit;

    -- Polyphase filter coefficients (example half-band filter)
    constant COEF_POLY : coef_2d := (
        (64, 0, -16, 0, 128, 0, -16, 0),    -- Phase 0
        (48, 16, -12, 4, 96, 32, -12, 4),   -- Phase 1
        (32, 32, -8, 8, 64, 64, -8, 8),     -- Phase 2
        (16, 48, -4, 12, 32, 96, -4, 12)    -- Phase 3
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
            phase <= 0;
            running <= '0';
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
                phase <= 0;
                running <= '1';
            end if;

            if running = '1' then
                -- Compute polyphase filter output for current phase
                acc := 0;
                for i in 0 to NUM_TAPS-1 loop
                    acc := acc + to_signed_int(delay_line(i)) * COEF_POLY(phase, i);
                end loop;

                data_out <= to_bv(acc / 256, DATA_WIDTH);
                valid_out <= '1';

                if phase = INTERP_RATE - 1 then
                    running <= '0';
                else
                    phase <= phase + 1;
                end if;
            end if;
        end if;
    end process;
end Behavioral;
