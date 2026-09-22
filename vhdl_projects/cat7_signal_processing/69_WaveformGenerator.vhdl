-- ============================================================================
-- VHDL Project: WaveformGenerator
-- Category: Signal Processing
-- Complexity: Medium-High
-- Description: Multi-function waveform generator with envelope control
--              ADSR envelope, LFO, and various wave shapes
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity WaveformGenerator is
    generic (
        DATA_WIDTH  : integer := 16;
        PHASE_WIDTH : integer := 24
    );
    port (
        clk        : in  bit;
        reset      : in  bit;
        enable     : in  bit;
        -- Frequency control
        freq_word  : in  bit_vector(PHASE_WIDTH-1 downto 0);
        -- Waveform selection
        wave_sel   : in  bit_vector(2 downto 0);
        -- Pulse width for PWM (0-255)
        pulse_width : in bit_vector(7 downto 0);
        -- Output
        wave_out   : out bit_vector(DATA_WIDTH-1 downto 0);
        sync_out   : out bit;  -- Rising edge at phase zero
        valid      : out bit
    );
end WaveformGenerator;

architecture Behavioral of WaveformGenerator is
    signal phase_acc : bit_vector(PHASE_WIDTH-1 downto 0);
    signal prev_msb : bit;

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
        variable phase : integer;
        variable half_max : integer;
        variable quarter_max : integer;
        variable output_val : integer;
        variable pw : integer;
        variable pw_threshold : integer;
    begin
        if reset = '1' then
            phase_acc <= (others => '0');
            prev_msb <= '0';
            wave_out <= (others => '0');
            sync_out <= '0';
            valid <= '0';
        elsif clk'event and clk = '1' then
            valid <= '0';
            sync_out <= '0';

            if enable = '1' then
                prev_msb <= phase_acc(PHASE_WIDTH-1);
                phase_acc <= to_bv(to_int(phase_acc) + to_int(freq_word), PHASE_WIDTH);
                phase := to_int(phase_acc);

                half_max := 2 ** (PHASE_WIDTH - 1);
                quarter_max := 2 ** (PHASE_WIDTH - 2);

                -- Sync output (phase overflow)
                if prev_msb = '1' and phase_acc(PHASE_WIDTH-1) = '0' then
                    sync_out <= '1';
                end if;

                pw := to_int(pulse_width);
                pw_threshold := (pw * (2 ** PHASE_WIDTH)) / 256;

                case wave_sel is
                    when "000" =>  -- Sine (parabolic approximation)
                        if phase < half_max then
                            output_val := 4 * phase * (half_max - phase) / half_max;
                            output_val := (output_val * 65536 / half_max) - 32768;
                        else
                            output_val := 4 * (phase - half_max) * (2 * half_max - phase) / half_max;
                            output_val := 32768 - (output_val * 65536 / half_max);
                        end if;

                    when "001" =>  -- Square
                        if phase < half_max then
                            output_val := 32767;
                        else
                            output_val := -32768;
                        end if;

                    when "010" =>  -- Sawtooth (rising)
                        output_val := (phase * 65536 / (2 ** PHASE_WIDTH)) - 32768;

                    when "011" =>  -- Sawtooth (falling)
                        output_val := 32767 - (phase * 65536 / (2 ** PHASE_WIDTH));

                    when "100" =>  -- Triangle
                        if phase < half_max then
                            output_val := (phase * 65536 / half_max) - 32768;
                        else
                            output_val := 32767 - ((phase - half_max) * 65536 / half_max);
                        end if;

                    when "101" =>  -- Pulse Width Modulation
                        if phase < pw_threshold then
                            output_val := 32767;
                        else
                            output_val := -32768;
                        end if;

                    when "110" =>  -- Staircase (8 levels)
                        output_val := ((phase / (2 ** (PHASE_WIDTH - 3))) * 65536 / 8) - 32768;

                    when others =>  -- Noise-like (LFSR pattern from phase)
                        output_val := ((phase * 31421 + 6927) mod 65536) - 32768;
                end case;

                wave_out <= to_bv(output_val, DATA_WIDTH);
                valid <= '1';
            end if;
        end if;
    end process;
end Behavioral;

-- ============================================================================
-- ADSR Envelope Generator
-- ============================================================================
entity ADSREnvelope is
    generic (DATA_WIDTH : integer := 16);
    port (
        clk       : in  bit;
        reset     : in  bit;
        gate      : in  bit;  -- Key on/off
        trigger   : in  bit;  -- Re-trigger
        -- Time parameters (exponential, smaller = faster)
        attack    : in  bit_vector(7 downto 0);
        decay     : in  bit_vector(7 downto 0);
        sustain   : in  bit_vector(7 downto 0);  -- Level, not time
        release_t : in  bit_vector(7 downto 0);
        -- Output
        envelope  : out bit_vector(DATA_WIDTH-1 downto 0);
        stage     : out bit_vector(1 downto 0)  -- 00=idle, 01=A, 10=D, 11=R
    );
end ADSREnvelope;

architecture Behavioral of ADSREnvelope is
    type state_type is (IDLE, ATTACK, DECAY_ST, SUSTAIN_ST, RELEASE_ST);
    signal state : state_type;
    signal env_level : integer range 0 to 65535;
    signal timer : integer range 0 to 65535;
    signal prev_gate : bit;

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
        if val < 0 then temp := 0;
        elsif val > (2**width - 1) then temp := 2**width - 1;
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
        variable rate : integer;
        variable sustain_level : integer;
        variable delta : integer;
    begin
        if reset = '1' then
            state <= IDLE;
            env_level <= 0;
            timer <= 0;
            prev_gate <= '0';
            envelope <= (others => '0');
            stage <= "00";
        elsif clk'event and clk = '1' then
            prev_gate <= gate;

            -- Trigger detection
            if trigger = '1' or (gate = '1' and prev_gate = '0') then
                state <= ATTACK;
                timer <= 0;
            end if;

            -- Gate release
            if gate = '0' and prev_gate = '1' then
                state <= RELEASE_ST;
                timer <= 0;
            end if;

            sustain_level := to_int(sustain) * 256;  -- Scale to 16-bit

            case state is
                when IDLE =>
                    env_level <= 0;
                    stage <= "00";

                when ATTACK =>
                    stage <= "01";
                    rate := 256 - to_int(attack);  -- Invert so higher value = faster
                    if rate < 1 then rate := 1; end if;

                    timer <= timer + 1;
                    if timer >= rate then
                        timer <= 0;
                        delta := (65535 - env_level) / 16;
                        if delta < 1 then delta := 1; end if;
                        env_level <= env_level + delta;

                        if env_level >= 65000 then
                            env_level <= 65535;
                            state <= DECAY_ST;
                            timer <= 0;
                        end if;
                    end if;

                when DECAY_ST =>
                    stage <= "10";
                    rate := 256 - to_int(decay);
                    if rate < 1 then rate := 1; end if;

                    timer <= timer + 1;
                    if timer >= rate then
                        timer <= 0;
                        delta := (env_level - sustain_level) / 16;
                        if delta < 1 then delta := 1; end if;
                        if env_level > sustain_level then
                            env_level <= env_level - delta;
                        end if;

                        if env_level <= sustain_level + 256 then
                            env_level <= sustain_level;
                            state <= SUSTAIN_ST;
                        end if;
                    end if;

                when SUSTAIN_ST =>
                    stage <= "10";
                    env_level <= sustain_level;
                    if gate = '0' then
                        state <= RELEASE_ST;
                        timer <= 0;
                    end if;

                when RELEASE_ST =>
                    stage <= "11";
                    rate := 256 - to_int(release_t);
                    if rate < 1 then rate := 1; end if;

                    timer <= timer + 1;
                    if timer >= rate then
                        timer <= 0;
                        delta := env_level / 16;
                        if delta < 1 then delta := 1; end if;
                        if env_level > 0 then
                            env_level <= env_level - delta;
                        end if;

                        if env_level < 256 then
                            env_level <= 0;
                            state <= IDLE;
                        end if;
                    end if;
            end case;

            envelope <= to_bv(env_level, DATA_WIDTH);
        end if;
    end process;
end Behavioral;

-- ============================================================================
-- LFO (Low Frequency Oscillator)
-- ============================================================================
entity LFO is
    generic (
        DATA_WIDTH : integer := 16;
        PHASE_WIDTH : integer := 20
    );
    port (
        clk       : in  bit;
        reset     : in  bit;
        enable    : in  bit;
        rate      : in  bit_vector(7 downto 0);  -- Frequency control
        waveform  : in  bit_vector(1 downto 0);  -- 00=sine, 01=triangle, 10=square, 11=random
        depth     : in  bit_vector(7 downto 0);  -- Output amplitude
        lfo_out   : out bit_vector(DATA_WIDTH-1 downto 0)
    );
end LFO;

architecture Behavioral of LFO is
    signal phase : bit_vector(PHASE_WIDTH-1 downto 0);
    signal lfsr : bit_vector(15 downto 0);
    signal random_hold : bit_vector(DATA_WIDTH-1 downto 0);

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
        variable phase_int : integer;
        variable half_max : integer;
        variable output_val : integer;
        variable rate_int : integer;
        variable depth_int : integer;
        variable fb : bit;
        variable prev_msb : bit;
    begin
        if reset = '1' then
            phase <= (others => '0');
            lfsr <= (0 => '1', others => '0');
            random_hold <= (others => '0');
            lfo_out <= (others => '0');
        elsif clk'event and clk = '1' then
            if enable = '1' then
                prev_msb := phase(PHASE_WIDTH-1);
                rate_int := to_int(rate) + 1;
                phase <= to_bv(to_int(phase) + rate_int, PHASE_WIDTH);

                phase_int := to_int(phase);
                half_max := 2 ** (PHASE_WIDTH - 1);
                depth_int := to_int(depth);

                -- Update LFSR
                fb := lfsr(15) xor lfsr(14) xor lfsr(12) xor lfsr(3);
                lfsr <= lfsr(14 downto 0) & fb;

                -- Sample-and-hold random at phase wrap
                if prev_msb = '1' and phase(PHASE_WIDTH-1) = '0' then
                    random_hold <= lfsr(DATA_WIDTH-1 downto 0);
                end if;

                case waveform is
                    when "00" =>  -- Sine (parabolic approximation)
                        if phase_int < half_max then
                            output_val := 4 * phase_int * (half_max - phase_int) / half_max;
                            output_val := (output_val * 65536 / half_max) - 32768;
                        else
                            output_val := 4 * (phase_int - half_max) * (2 * half_max - phase_int) / half_max;
                            output_val := 32768 - (output_val * 65536 / half_max);
                        end if;

                    when "01" =>  -- Triangle
                        if phase_int < half_max then
                            output_val := (phase_int * 65536 / half_max) - 32768;
                        else
                            output_val := 32767 - ((phase_int - half_max) * 65536 / half_max);
                        end if;

                    when "10" =>  -- Square
                        if phase_int < half_max then
                            output_val := 32767;
                        else
                            output_val := -32768;
                        end if;

                    when others =>  -- Sample & Hold Random
                        output_val := to_int(random_hold) - 32768;
                end case;

                -- Apply depth
                output_val := (output_val * depth_int) / 256;
                lfo_out <= to_bv(output_val, DATA_WIDTH);
            end if;
        end if;
    end process;
end Behavioral;
