-- ============================================================================
-- VHDL Project: PulseGenerator
-- Category: Timing & Synchronization
-- Complexity: Low
-- Description: Single-shot and periodic pulse generator with configurable
--              pulse width, delay, and repetition modes
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity PulseGenerator is
    generic (
        MAX_COUNT : integer := 65536
    );
    port (
        clk         : in  bit;
        reset       : in  bit;
        -- Control
        trigger     : in  bit;      -- Start pulse generation
        mode        : in  bit;      -- '0' = single-shot, '1' = periodic
        abort       : in  bit;      -- Stop current pulse
        -- Configuration
        delay       : in  bit_vector(15 downto 0);  -- Delay before pulse
        width       : in  bit_vector(15 downto 0);  -- Pulse width
        period      : in  bit_vector(15 downto 0);  -- Period (for periodic mode)
        -- Outputs
        pulse_out   : out bit;      -- Pulse output
        pulse_out_n : out bit;      -- Inverted pulse output
        busy        : out bit;      -- Generator is active
        done        : out bit       -- Pulse sequence complete (single-shot)
    );
end PulseGenerator;

architecture Behavioral of PulseGenerator is
    type state_type is (IDLE, DELAY_PHASE, PULSE_PHASE, POST_PHASE);
    signal state : state_type;

    signal counter : integer range 0 to MAX_COUNT-1;
    signal trigger_edge : bit;
    signal trigger_prev : bit;
    signal pulse_internal : bit;

    function to_integer(bv : bit_vector) return integer is
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
    -- Edge detection for trigger
    trigger_edge <= trigger and (not trigger_prev);

    main_proc: process(clk, reset)
        variable delay_val, width_val, period_val : integer;
        variable post_delay : integer;
    begin
        if reset = '1' then
            state <= IDLE;
            counter <= 0;
            pulse_internal <= '0';
            trigger_prev <= '0';
            busy <= '0';
            done <= '0';
        elsif clk'event and clk = '1' then
            trigger_prev <= trigger;
            done <= '0';

            delay_val := to_integer(delay);
            width_val := to_integer(width);
            period_val := to_integer(period);

            -- Calculate post-pulse delay for periodic mode
            if period_val > (delay_val + width_val) then
                post_delay := period_val - delay_val - width_val;
            else
                post_delay := 1;
            end if;

            -- Abort handling
            if abort = '1' then
                state <= IDLE;
                pulse_internal <= '0';
                busy <= '0';
            else
                case state is
                    when IDLE =>
                        pulse_internal <= '0';
                        busy <= '0';
                        counter <= 0;
                        if trigger_edge = '1' then
                            busy <= '1';
                            if delay_val > 0 then
                                state <= DELAY_PHASE;
                            else
                                state <= PULSE_PHASE;
                                pulse_internal <= '1';
                            end if;
                        end if;

                    when DELAY_PHASE =>
                        if counter >= delay_val - 1 then
                            counter <= 0;
                            state <= PULSE_PHASE;
                            pulse_internal <= '1';
                        else
                            counter <= counter + 1;
                        end if;

                    when PULSE_PHASE =>
                        if counter >= width_val - 1 then
                            counter <= 0;
                            pulse_internal <= '0';
                            if mode = '1' then
                                -- Periodic mode: go to post delay
                                state <= POST_PHASE;
                            else
                                -- Single-shot: done
                                state <= IDLE;
                                done <= '1';
                                busy <= '0';
                            end if;
                        else
                            counter <= counter + 1;
                        end if;

                    when POST_PHASE =>
                        if counter >= post_delay - 1 then
                            counter <= 0;
                            -- Restart cycle
                            if delay_val > 0 then
                                state <= DELAY_PHASE;
                            else
                                state <= PULSE_PHASE;
                                pulse_internal <= '1';
                            end if;
                        else
                            counter <= counter + 1;
                        end if;
                end case;
            end if;
        end if;
    end process main_proc;

    pulse_out <= pulse_internal;
    pulse_out_n <= not pulse_internal;

end Behavioral;

-- ============================================================================
-- Pulse Train Generator (multiple pulses per trigger)
-- ============================================================================
entity PulseTrainGenerator is
    port (
        clk         : in  bit;
        reset       : in  bit;
        trigger     : in  bit;
        -- Configuration
        num_pulses  : in  bit_vector(7 downto 0);   -- Number of pulses
        pulse_width : in  bit_vector(15 downto 0);  -- Width of each pulse
        gap_width   : in  bit_vector(15 downto 0);  -- Gap between pulses
        -- Outputs
        pulse_out   : out bit;
        busy        : out bit;
        pulse_count : out bit_vector(7 downto 0);   -- Current pulse number
        done        : out bit
    );
end PulseTrainGenerator;

architecture Behavioral of PulseTrainGenerator is
    type state_type is (IDLE, PULSE_HIGH, PULSE_GAP);
    signal state : state_type;
    signal counter : integer range 0 to 65535;
    signal pulse_cnt : integer range 0 to 255;
    signal trigger_prev : bit;

    function to_integer(bv : bit_vector) return integer is
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
    process(clk, reset)
        variable num_p, p_width, g_width : integer;
    begin
        if reset = '1' then
            state <= IDLE;
            counter <= 0;
            pulse_cnt <= 0;
            pulse_out <= '0';
            busy <= '0';
            done <= '0';
            trigger_prev <= '0';
        elsif clk'event and clk = '1' then
            trigger_prev <= trigger;
            done <= '0';

            num_p := to_integer(num_pulses);
            p_width := to_integer(pulse_width);
            g_width := to_integer(gap_width);

            if num_p = 0 then num_p := 1; end if;
            if p_width = 0 then p_width := 1; end if;
            if g_width = 0 then g_width := 1; end if;

            case state is
                when IDLE =>
                    pulse_out <= '0';
                    busy <= '0';
                    pulse_cnt <= 0;
                    counter <= 0;
                    if trigger = '1' and trigger_prev = '0' then
                        state <= PULSE_HIGH;
                        pulse_out <= '1';
                        busy <= '1';
                        pulse_cnt <= 1;
                    end if;

                when PULSE_HIGH =>
                    if counter >= p_width - 1 then
                        counter <= 0;
                        pulse_out <= '0';
                        if pulse_cnt >= num_p then
                            state <= IDLE;
                            done <= '1';
                            busy <= '0';
                        else
                            state <= PULSE_GAP;
                        end if;
                    else
                        counter <= counter + 1;
                    end if;

                when PULSE_GAP =>
                    if counter >= g_width - 1 then
                        counter <= 0;
                        state <= PULSE_HIGH;
                        pulse_out <= '1';
                        pulse_cnt <= pulse_cnt + 1;
                    else
                        counter <= counter + 1;
                    end if;
            end case;
        end if;
    end process;

    -- Convert pulse_cnt to bit_vector
    pulse_count_conv: process(pulse_cnt)
        variable temp : integer;
    begin
        temp := pulse_cnt;
        for i in 0 to 7 loop
            if (temp mod 2) = 1 then
                pulse_count(i) <= '1';
            else
                pulse_count(i) <= '0';
            end if;
            temp := temp / 2;
        end loop;
    end process;

end Behavioral;

-- ============================================================================
-- Monostable Multivibrator (Retriggerable)
-- ============================================================================
entity Monostable is
    port (
        clk         : in  bit;
        reset       : in  bit;
        trigger     : in  bit;
        retrigger   : in  bit;  -- '1' allows retriggering during pulse
        -- Configuration
        pulse_width : in  bit_vector(15 downto 0);
        -- Output
        q           : out bit;
        q_n         : out bit
    );
end Monostable;

architecture Behavioral of Monostable is
    signal counter : integer range 0 to 65535;
    signal active : bit;
    signal trigger_prev : bit;

    function to_integer(bv : bit_vector) return integer is
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
    process(clk, reset)
        variable width_val : integer;
        variable trigger_edge : bit;
    begin
        if reset = '1' then
            counter <= 0;
            active <= '0';
            trigger_prev <= '0';
        elsif clk'event and clk = '1' then
            trigger_prev <= trigger;
            trigger_edge := trigger and (not trigger_prev);
            width_val := to_integer(pulse_width);
            if width_val = 0 then width_val := 1; end if;

            if active = '0' then
                -- Not active: wait for trigger
                if trigger_edge = '1' then
                    active <= '1';
                    counter <= 0;
                end if;
            else
                -- Active: counting
                if trigger_edge = '1' and retrigger = '1' then
                    -- Retrigger: restart counter
                    counter <= 0;
                elsif counter >= width_val - 1 then
                    -- Done
                    active <= '0';
                    counter <= 0;
                else
                    counter <= counter + 1;
                end if;
            end if;
        end if;
    end process;

    q <= active;
    q_n <= not active;

end Behavioral;

-- ============================================================================
-- Delay Line (programmable delay)
-- ============================================================================
entity DelayLine is
    generic (
        MAX_DELAY : integer := 256
    );
    port (
        clk         : in  bit;
        reset       : in  bit;
        data_in     : in  bit;
        delay_taps  : in  bit_vector(7 downto 0);  -- Number of clock cycles
        data_out    : out bit;
        tap_0       : out bit;  -- Immediate (no delay)
        tap_quarter : out bit;  -- 1/4 delay
        tap_half    : out bit;  -- 1/2 delay
        tap_3quarter: out bit   -- 3/4 delay
    );
end DelayLine;

architecture Behavioral of DelayLine is
    signal shift_reg : bit_vector(MAX_DELAY-1 downto 0);

    function to_integer(bv : bit_vector) return integer is
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
    process(clk, reset)
    begin
        if reset = '1' then
            shift_reg <= (others => '0');
        elsif clk'event and clk = '1' then
            shift_reg <= shift_reg(MAX_DELAY-2 downto 0) & data_in;
        end if;
    end process;

    -- Output selection
    output_proc: process(shift_reg, delay_taps)
        variable delay_val : integer;
        variable q, h, tq : integer;
    begin
        delay_val := to_integer(delay_taps);
        if delay_val = 0 then delay_val := 1; end if;
        if delay_val > MAX_DELAY then delay_val := MAX_DELAY; end if;

        q := delay_val / 4;
        h := delay_val / 2;
        tq := (delay_val * 3) / 4;

        if q = 0 then q := 1; end if;
        if h = 0 then h := 1; end if;
        if tq = 0 then tq := 1; end if;

        data_out <= shift_reg(delay_val - 1);
        tap_0 <= shift_reg(0);
        tap_quarter <= shift_reg(q - 1);
        tap_half <= shift_reg(h - 1);
        tap_3quarter <= shift_reg(tq - 1);
    end process;

end Behavioral;

-- ============================================================================
-- Testbench
-- ============================================================================
entity PulseGenerator_TB is
end PulseGenerator_TB;

architecture Test of PulseGenerator_TB is
    signal tb_clk, tb_reset : bit;
    signal tb_trigger, tb_mode, tb_abort : bit;
    signal tb_delay, tb_width, tb_period : bit_vector(15 downto 0);
    signal tb_pulse_out, tb_busy, tb_done : bit;
begin
    UUT: entity work.PulseGenerator
        port map (
            clk       => tb_clk,
            reset     => tb_reset,
            trigger   => tb_trigger,
            mode      => tb_mode,
            abort     => tb_abort,
            delay     => tb_delay,
            width     => tb_width,
            period    => tb_period,
            pulse_out => tb_pulse_out,
            pulse_out_n => open,
            busy      => tb_busy,
            done      => tb_done
        );

    clk_gen: process
    begin
        tb_clk <= '0'; wait for 5 ns;
        tb_clk <= '1'; wait for 5 ns;
    end process;

    stimulus: process
    begin
        tb_reset <= '1';
        tb_trigger <= '0';
        tb_mode <= '0';
        tb_abort <= '0';
        tb_delay <= "0000000000000100";   -- 4 cycles delay
        tb_width <= "0000000000001000";   -- 8 cycles width
        tb_period <= "0000000000100000";  -- 32 cycles period
        wait for 30 ns;

        tb_reset <= '0';
        wait for 20 ns;

        -- Single-shot pulse
        tb_trigger <= '1';
        wait for 10 ns;
        tb_trigger <= '0';
        wait for 200 ns;

        -- Periodic mode
        tb_mode <= '1';
        tb_trigger <= '1';
        wait for 10 ns;
        tb_trigger <= '0';
        wait for 500 ns;

        -- Abort
        tb_abort <= '1';
        wait for 10 ns;
        tb_abort <= '0';
        wait for 100 ns;

        wait;
    end process;
end Test;
