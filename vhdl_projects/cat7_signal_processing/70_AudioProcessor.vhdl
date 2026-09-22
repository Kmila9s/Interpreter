-- ============================================================================
-- VHDL Project: AudioProcessor
-- Category: Signal Processing
-- Complexity: Very High
-- Description: Complete audio effects processor with multiple effects
--              Delay, reverb, chorus, compressor, EQ
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity AudioProcessor is
    generic (
        DATA_WIDTH : integer := 16;
        DELAY_SIZE : integer := 8192  -- Max delay samples
    );
    port (
        clk        : in  bit;
        reset      : in  bit;
        -- Audio I/O
        audio_in   : in  bit_vector(DATA_WIDTH-1 downto 0);
        valid_in   : in  bit;
        audio_out  : out bit_vector(DATA_WIDTH-1 downto 0);
        valid_out  : out bit;
        -- Effect selection
        effect_sel : in  bit_vector(2 downto 0);
        -- Parameters
        param1     : in  bit_vector(7 downto 0);
        param2     : in  bit_vector(7 downto 0);
        mix        : in  bit_vector(7 downto 0)  -- Dry/wet mix
    );
end AudioProcessor;

architecture Behavioral of AudioProcessor is
    -- Delay line memory
    type delay_mem_type is array (0 to DELAY_SIZE-1) of bit_vector(DATA_WIDTH-1 downto 0);
    signal delay_mem : delay_mem_type;
    signal write_ptr : integer range 0 to DELAY_SIZE-1;

    -- State for effects
    signal prev_sample : bit_vector(DATA_WIDTH-1 downto 0);
    signal lfo_phase : integer range 0 to 65535;

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

    function saturate(val : integer) return integer is
    begin
        if val > 32767 then return 32767;
        elsif val < -32768 then return -32768;
        else return val;
        end if;
    end function;

begin
    process(clk, reset)
        variable dry, wet, mixed : integer;
        variable delay_time : integer;
        variable feedback : integer;
        variable read_ptr : integer;
        variable delayed : integer;
        variable lfo_val : integer;
        variable mod_delay : integer;
        variable threshold, ratio : integer;
        variable gain : integer;
        variable mix_val : integer;
    begin
        if reset = '1' then
            for i in 0 to DELAY_SIZE-1 loop
                delay_mem(i) <= (others => '0');
            end loop;
            write_ptr <= 0;
            prev_sample <= (others => '0');
            lfo_phase <= 0;
            audio_out <= (others => '0');
            valid_out <= '0';
        elsif clk'event and clk = '1' then
            valid_out <= '0';

            if valid_in = '1' then
                dry := to_signed_int(audio_in);
                mix_val := to_int(mix);

                -- Update LFO for modulation effects
                lfo_phase <= (lfo_phase + to_int(param2) + 1) mod 65536;
                if lfo_phase < 32768 then
                    lfo_val := lfo_phase - 16384;
                else
                    lfo_val := 49152 - lfo_phase;
                end if;

                case effect_sel is
                    when "000" =>  -- Bypass
                        wet := dry;

                    when "001" =>  -- Simple Delay
                        delay_time := (to_int(param1) * DELAY_SIZE) / 256;
                        if delay_time < 1 then delay_time := 1; end if;
                        feedback := to_int(param2);

                        read_ptr := (write_ptr - delay_time + DELAY_SIZE) mod DELAY_SIZE;
                        delayed := to_signed_int(delay_mem(read_ptr));

                        wet := saturate(dry + (delayed * feedback) / 256);

                        -- Write to delay line (with feedback)
                        delay_mem(write_ptr) <= to_bv(saturate(dry + (delayed * feedback) / 512), DATA_WIDTH);

                    when "010" =>  -- Chorus
                        delay_time := 256 + (to_int(param1) * 128) / 256;  -- Base delay 256-384 samples
                        mod_delay := (lfo_val * to_int(param2)) / 32768;
                        read_ptr := (write_ptr - delay_time - mod_delay + DELAY_SIZE) mod DELAY_SIZE;
                        delayed := to_signed_int(delay_mem(read_ptr));

                        wet := saturate((dry + delayed) / 2);
                        delay_mem(write_ptr) <= audio_in;

                    when "011" =>  -- Flanger
                        delay_time := 4 + (to_int(param1) * 32) / 256;  -- Short delay 4-36 samples
                        mod_delay := (lfo_val * delay_time) / 32768;
                        read_ptr := (write_ptr - delay_time - mod_delay + DELAY_SIZE) mod DELAY_SIZE;
                        delayed := to_signed_int(delay_mem(read_ptr));
                        feedback := to_int(param2);

                        wet := saturate(dry + (delayed * feedback) / 256);
                        delay_mem(write_ptr) <= to_bv(saturate(dry + (delayed * feedback / 2) / 256), DATA_WIDTH);

                    when "100" =>  -- Simple Reverb (comb filter)
                        delay_time := 512 + (to_int(param1) * 1024) / 256;
                        feedback := 180 + (to_int(param2) * 60) / 256;  -- 70-95% feedback

                        read_ptr := (write_ptr - delay_time + DELAY_SIZE) mod DELAY_SIZE;
                        delayed := to_signed_int(delay_mem(read_ptr));

                        wet := saturate(dry + (delayed * 128) / 256);
                        delay_mem(write_ptr) <= to_bv(saturate((dry * 64 + delayed * feedback) / 256), DATA_WIDTH);

                    when "101" =>  -- Compressor/Limiter
                        threshold := to_int(param1) * 128;  -- Threshold level
                        ratio := to_int(param2);  -- 0=1:1, 255=inf:1

                        if dry > 0 then
                            if dry > threshold then
                                gain := threshold + ((dry - threshold) * (256 - ratio)) / 256;
                                wet := gain;
                            else
                                wet := dry;
                            end if;
                        else
                            if dry < -threshold then
                                gain := -threshold + ((dry + threshold) * (256 - ratio)) / 256;
                                wet := gain;
                            else
                                wet := dry;
                            end if;
                        end if;
                        delay_mem(write_ptr) <= audio_in;

                    when "110" =>  -- Distortion/Overdrive
                        gain := 128 + to_int(param1) * 4;  -- Gain 128-1152
                        wet := (dry * gain) / 128;

                        -- Soft clipping
                        if wet > 24000 then
                            wet := 24000 + (wet - 24000) / 4;
                        elsif wet < -24000 then
                            wet := -24000 + (wet + 24000) / 4;
                        end if;
                        wet := saturate(wet);
                        delay_mem(write_ptr) <= audio_in;

                    when others =>  -- Lowpass Filter
                        feedback := to_int(param1);  -- Filter cutoff
                        wet := (dry * feedback + to_signed_int(prev_sample) * (256 - feedback)) / 256;
                        delay_mem(write_ptr) <= audio_in;
                end case;

                -- Store for filter
                prev_sample <= to_bv(wet, DATA_WIDTH);

                -- Dry/Wet mix
                mixed := (dry * (255 - mix_val) + wet * mix_val) / 255;
                audio_out <= to_bv(saturate(mixed), DATA_WIDTH);
                valid_out <= '1';

                -- Advance write pointer
                write_ptr <= (write_ptr + 1) mod DELAY_SIZE;
            end if;
        end if;
    end process;
end Behavioral;

-- ============================================================================
-- Parametric EQ Band
-- ============================================================================
entity ParametricEQBand is
    generic (DATA_WIDTH : integer := 16);
    port (
        clk       : in  bit;
        reset     : in  bit;
        audio_in  : in  bit_vector(DATA_WIDTH-1 downto 0);
        valid_in  : in  bit;
        -- Band parameters
        frequency : in  bit_vector(7 downto 0);  -- Center frequency
        bandwidth : in  bit_vector(7 downto 0);  -- Q factor
        gain_db   : in  bit_vector(7 downto 0);  -- Boost/cut (-12 to +12 dB)
        -- Output
        audio_out : out bit_vector(DATA_WIDTH-1 downto 0);
        valid_out : out bit
    );
end ParametricEQBand;

architecture Behavioral of ParametricEQBand is
    -- Biquad filter state
    signal x1, x2 : bit_vector(DATA_WIDTH-1 downto 0);
    signal y1, y2 : bit_vector(DATA_WIDTH-1 downto 0);

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
        variable x0, y0 : integer;
        variable b0, b1, b2, a1, a2 : integer;
        variable freq_val, q_val, gain_val : integer;
        variable alpha, A : integer;
    begin
        if reset = '1' then
            x1 <= (others => '0');
            x2 <= (others => '0');
            y1 <= (others => '0');
            y2 <= (others => '0');
            audio_out <= (others => '0');
            valid_out <= '0';
        elsif clk'event and clk = '1' then
            valid_out <= '0';

            if valid_in = '1' then
                x0 := to_signed_int(audio_in);
                freq_val := to_int(frequency);
                q_val := to_int(bandwidth) + 16;  -- Minimum Q
                gain_val := to_int(gain_db);

                -- Simplified coefficient calculation
                -- A = 10^(gain/40), approximated as 128 + gain_val for small gains
                if gain_val > 128 then
                    A := 128 + (gain_val - 128) / 2;  -- Boost
                else
                    A := 128 - (128 - gain_val) / 2;  -- Cut
                end if;

                alpha := 256 / q_val;
                if alpha < 1 then alpha := 1; end if;

                -- Peaking EQ coefficients (simplified)
                b0 := 128 + (alpha * (A - 128)) / 128;
                b1 := 0;  -- Simplified
                b2 := 128 - (alpha * (A - 128)) / 128;
                a1 := 0;  -- Simplified
                a2 := 128 - alpha;

                -- Biquad filter difference equation
                y0 := (b0 * x0 + b1 * to_signed_int(x1) + b2 * to_signed_int(x2)
                       - a1 * to_signed_int(y1) - a2 * to_signed_int(y2)) / 128;

                -- Saturate
                if y0 > 32767 then y0 := 32767;
                elsif y0 < -32768 then y0 := -32768;
                end if;

                -- Update state
                x2 <= x1;
                x1 <= audio_in;
                y2 <= y1;
                y1 <= to_bv(y0, DATA_WIDTH);

                audio_out <= to_bv(y0, DATA_WIDTH);
                valid_out <= '1';
            end if;
        end if;
    end process;
end Behavioral;

-- ============================================================================
-- Noise Gate
-- ============================================================================
entity NoiseGate is
    generic (DATA_WIDTH : integer := 16);
    port (
        clk        : in  bit;
        reset      : in  bit;
        audio_in   : in  bit_vector(DATA_WIDTH-1 downto 0);
        valid_in   : in  bit;
        threshold  : in  bit_vector(7 downto 0);  -- Gate threshold
        attack     : in  bit_vector(7 downto 0);  -- Attack time
        release_t  : in  bit_vector(7 downto 0);  -- Release time
        audio_out  : out bit_vector(DATA_WIDTH-1 downto 0);
        gate_open  : out bit;
        valid_out  : out bit
    );
end NoiseGate;

architecture Behavioral of NoiseGate is
    signal envelope : integer range 0 to 65535;
    signal gate_gain : integer range 0 to 256;

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
        variable input_abs : integer;
        variable thresh_val : integer;
        variable attack_val, release_val : integer;
        variable output_val : integer;
    begin
        if reset = '1' then
            envelope <= 0;
            gate_gain <= 0;
            audio_out <= (others => '0');
            gate_open <= '0';
            valid_out <= '0';
        elsif clk'event and clk = '1' then
            valid_out <= '0';

            if valid_in = '1' then
                -- Get absolute value for envelope
                input_abs := to_signed_int(audio_in);
                if input_abs < 0 then
                    input_abs := -input_abs;
                end if;

                -- Envelope follower
                if input_abs > envelope then
                    envelope <= envelope + (input_abs - envelope) / 4;
                else
                    envelope <= envelope - envelope / 64;
                end if;

                thresh_val := to_int(threshold) * 128;
                attack_val := 256 - to_int(attack);
                release_val := 256 - to_int(release_t);
                if attack_val < 1 then attack_val := 1; end if;
                if release_val < 1 then release_val := 1; end if;

                -- Gate control
                if envelope > thresh_val then
                    -- Open gate
                    if gate_gain < 256 then
                        gate_gain <= gate_gain + attack_val / 4;
                        if gate_gain > 256 then gate_gain <= 256; end if;
                    end if;
                    gate_open <= '1';
                else
                    -- Close gate
                    if gate_gain > 0 then
                        gate_gain <= gate_gain - release_val / 8;
                        if gate_gain < 0 then gate_gain <= 0; end if;
                    end if;
                    if gate_gain < 16 then
                        gate_open <= '0';
                    end if;
                end if;

                -- Apply gate gain
                output_val := (to_signed_int(audio_in) * gate_gain) / 256;
                audio_out <= to_bv(output_val, DATA_WIDTH);
                valid_out <= '1';
            end if;
        end if;
    end process;
end Behavioral;
