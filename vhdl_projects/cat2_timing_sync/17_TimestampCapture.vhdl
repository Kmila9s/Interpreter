-- ============================================================================
-- VHDL Project: TimestampCapture
-- Category: Timing & Synchronization
-- Complexity: Medium
-- Description: Input capture with 32-bit timestamp
--              Captures time on input edges, measures period/frequency
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity TimestampCapture is
    generic (
        NUM_CHANNELS : integer := 4;
        TIMER_WIDTH  : integer := 32
    );
    port (
        clk             : in  bit;
        reset           : in  bit;
        enable          : in  bit;
        -- Input signals
        capture_inputs  : in  bit_vector(NUM_CHANNELS-1 downto 0);
        -- Configuration per channel
        capture_rising  : in  bit_vector(NUM_CHANNELS-1 downto 0);  -- Capture on rising
        capture_falling : in  bit_vector(NUM_CHANNELS-1 downto 0);  -- Capture on falling
        -- Timer value (free running)
        timer_value     : out bit_vector(TIMER_WIDTH-1 downto 0);
        -- Captured values (directly wired, select via channel)
        capture_ch0     : out bit_vector(TIMER_WIDTH-1 downto 0);
        capture_ch1     : out bit_vector(TIMER_WIDTH-1 downto 0);
        capture_ch2     : out bit_vector(TIMER_WIDTH-1 downto 0);
        capture_ch3     : out bit_vector(TIMER_WIDTH-1 downto 0);
        -- Capture event flags
        capture_flags   : out bit_vector(NUM_CHANNELS-1 downto 0);
        -- Clear flags
        clear_flags     : in  bit_vector(NUM_CHANNELS-1 downto 0)
    );
end TimestampCapture;

architecture Behavioral of TimestampCapture is
    -- Timer
    signal timer_reg : bit_vector(TIMER_WIDTH-1 downto 0);

    -- Capture registers
    type capture_array is array (0 to NUM_CHANNELS-1) of bit_vector(TIMER_WIDTH-1 downto 0);
    signal captures : capture_array;

    -- Edge detection
    signal input_sync : bit_vector(NUM_CHANNELS-1 downto 0);
    signal input_prev : bit_vector(NUM_CHANNELS-1 downto 0);
    signal flags_reg : bit_vector(NUM_CHANNELS-1 downto 0);

    function increment(val : bit_vector) return bit_vector is
        variable result : bit_vector(val'range);
        variable carry : bit := '1';
    begin
        for i in val'low to val'high loop
            result(i) := val(i) xor carry;
            carry := val(i) and carry;
        end loop;
        return result;
    end function;

begin
    -- Free-running timer
    timer_proc: process(clk, reset)
    begin
        if reset = '1' then
            timer_reg <= (others => '0');
        elsif clk'event and clk = '1' then
            if enable = '1' then
                timer_reg <= increment(timer_reg);
            end if;
        end if;
    end process timer_proc;

    timer_value <= timer_reg;

    -- Input synchronization and capture
    capture_gen: for i in 0 to NUM_CHANNELS-1 generate
        capture_proc: process(clk, reset)
            variable rising_edge, falling_edge : bit;
            variable do_capture : bit;
        begin
            if reset = '1' then
                input_sync(i) <= '0';
                input_prev(i) <= '0';
                captures(i) <= (others => '0');
                flags_reg(i) <= '0';
            elsif clk'event and clk = '1' then
                input_sync(i) <= capture_inputs(i);
                input_prev(i) <= input_sync(i);

                rising_edge := input_sync(i) and (not input_prev(i));
                falling_edge := (not input_sync(i)) and input_prev(i);

                do_capture := (rising_edge and capture_rising(i)) or
                              (falling_edge and capture_falling(i));

                -- Clear flag if requested
                if clear_flags(i) = '1' then
                    flags_reg(i) <= '0';
                end if;

                -- Capture on edge
                if do_capture = '1' then
                    captures(i) <= timer_reg;
                    flags_reg(i) <= '1';
                end if;
            end if;
        end process capture_proc;
    end generate capture_gen;

    -- Output assignments
    capture_ch0 <= captures(0);
    capture_ch1 <= captures(1);
    capture_ch2 <= captures(2);
    capture_ch3 <= captures(3);
    capture_flags <= flags_reg;

end Behavioral;

-- ============================================================================
-- Period Measurement
-- ============================================================================
entity PeriodMeasure is
    generic (
        TIMER_WIDTH : integer := 32
    );
    port (
        clk             : in  bit;
        reset           : in  bit;
        enable          : in  bit;
        signal_in       : in  bit;
        -- Configuration
        measure_rising  : in  bit;  -- Measure period between rising edges
        -- Outputs
        period          : out bit_vector(TIMER_WIDTH-1 downto 0);
        period_valid    : out bit;
        -- For frequency calculation
        overflow        : out bit
    );
end PeriodMeasure;

architecture Behavioral of PeriodMeasure is
    signal counter : bit_vector(TIMER_WIDTH-1 downto 0);
    signal last_capture : bit_vector(TIMER_WIDTH-1 downto 0);
    signal first_edge : bit;
    signal signal_sync, signal_prev : bit;
    signal period_reg : bit_vector(TIMER_WIDTH-1 downto 0);
    signal valid_reg : bit;

    function increment(val : bit_vector) return bit_vector is
        variable result : bit_vector(val'range);
        variable carry : bit := '1';
    begin
        for i in val'low to val'high loop
            result(i) := val(i) xor carry;
            carry := val(i) and carry;
        end loop;
        return result;
    end function;

    function subtract(a, b : bit_vector) return bit_vector is
        variable result : bit_vector(a'range);
        variable borrow : bit := '0';
        variable b_inv : bit;
    begin
        for i in a'low to a'high loop
            b_inv := not b(i);
            result(i) := a(i) xor b_inv xor borrow;
            borrow := (b_inv and borrow) or (a(i) and borrow) or (a(i) and b_inv);
        end loop;
        return result;
    end function;

    function is_all_ones(val : bit_vector) return bit is
        variable result : bit := '1';
    begin
        for i in val'range loop
            result := result and val(i);
        end loop;
        return result;
    end function;

begin
    process(clk, reset)
        variable edge_detect : bit;
    begin
        if reset = '1' then
            counter <= (others => '0');
            last_capture <= (others => '0');
            first_edge <= '0';
            signal_sync <= '0';
            signal_prev <= '0';
            period_reg <= (others => '0');
            valid_reg <= '0';
            overflow <= '0';
        elsif clk'event and clk = '1' then
            signal_sync <= signal_in;
            signal_prev <= signal_sync;
            valid_reg <= '0';
            overflow <= '0';

            if enable = '1' then
                -- Increment counter
                if is_all_ones(counter) = '1' then
                    overflow <= '1';
                else
                    counter <= increment(counter);
                end if;

                -- Edge detection
                if measure_rising = '1' then
                    edge_detect := signal_sync and (not signal_prev);
                else
                    edge_detect := (not signal_sync) and signal_prev;
                end if;

                if edge_detect = '1' then
                    if first_edge = '0' then
                        -- First edge - just record
                        first_edge <= '1';
                        last_capture <= counter;
                    else
                        -- Subsequent edge - calculate period
                        period_reg <= subtract(counter, last_capture);
                        last_capture <= counter;
                        valid_reg <= '1';
                    end if;
                end if;
            end if;
        end if;
    end process;

    period <= period_reg;
    period_valid <= valid_reg;

end Behavioral;

-- ============================================================================
-- Pulse Width Measurement
-- ============================================================================
entity PulseWidthMeasure is
    generic (
        TIMER_WIDTH : integer := 24
    );
    port (
        clk             : in  bit;
        reset           : in  bit;
        enable          : in  bit;
        signal_in       : in  bit;
        -- Outputs
        high_width      : out bit_vector(TIMER_WIDTH-1 downto 0);  -- High pulse width
        low_width       : out bit_vector(TIMER_WIDTH-1 downto 0);  -- Low pulse width
        duty_cycle      : out bit_vector(7 downto 0);  -- 0-255 (0-100%)
        high_valid      : out bit;
        low_valid       : out bit
    );
end PulseWidthMeasure;

architecture Behavioral of PulseWidthMeasure is
    signal high_counter : bit_vector(TIMER_WIDTH-1 downto 0);
    signal low_counter : bit_vector(TIMER_WIDTH-1 downto 0);
    signal high_reg, low_reg : bit_vector(TIMER_WIDTH-1 downto 0);
    signal signal_sync, signal_prev : bit;

    function increment(val : bit_vector) return bit_vector is
        variable result : bit_vector(val'range);
        variable carry : bit := '1';
    begin
        for i in val'low to val'high loop
            result(i) := val(i) xor carry;
            carry := val(i) and carry;
        end loop;
        return result;
    end function;

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
        variable rising, falling : bit;
        variable total, high_int : integer;
        variable duty_temp : integer;
    begin
        if reset = '1' then
            high_counter <= (others => '0');
            low_counter <= (others => '0');
            high_reg <= (others => '0');
            low_reg <= (others => '0');
            signal_sync <= '0';
            signal_prev <= '0';
            high_valid <= '0';
            low_valid <= '0';
            duty_cycle <= (others => '0');
        elsif clk'event and clk = '1' then
            signal_sync <= signal_in;
            signal_prev <= signal_sync;
            high_valid <= '0';
            low_valid <= '0';

            rising := signal_sync and (not signal_prev);
            falling := (not signal_sync) and signal_prev;

            if enable = '1' then
                if signal_sync = '1' then
                    high_counter <= increment(high_counter);
                else
                    low_counter <= increment(low_counter);
                end if;

                -- On rising edge, capture low width
                if rising = '1' then
                    low_reg <= low_counter;
                    low_counter <= (others => '0');
                    low_valid <= '1';

                    -- Calculate duty cycle
                    high_int := to_integer(high_reg);
                    total := high_int + to_integer(low_reg);
                    if total > 0 then
                        duty_temp := (high_int * 256) / total;
                        if duty_temp > 255 then duty_temp := 255; end if;
                    else
                        duty_temp := 0;
                    end if;
                    for i in 0 to 7 loop
                        if (duty_temp mod 2) = 1 then
                            duty_cycle(i) <= '1';
                        else
                            duty_cycle(i) <= '0';
                        end if;
                        duty_temp := duty_temp / 2;
                    end loop;
                end if;

                -- On falling edge, capture high width
                if falling = '1' then
                    high_reg <= high_counter;
                    high_counter <= (others => '0');
                    high_valid <= '1';
                end if;
            end if;
        end if;
    end process;

    high_width <= high_reg;
    low_width <= low_reg;

end Behavioral;

-- ============================================================================
-- Time Interval Counter
-- ============================================================================
entity TimeIntervalCounter is
    generic (
        COUNTER_WIDTH : integer := 32
    );
    port (
        clk             : in  bit;
        reset           : in  bit;
        enable          : in  bit;
        -- Control
        start_signal    : in  bit;
        stop_signal     : in  bit;
        arm             : in  bit;  -- Arm for next measurement
        -- Outputs
        time_interval   : out bit_vector(COUNTER_WIDTH-1 downto 0);
        measurement_valid : out bit;
        armed           : out bit;
        measuring       : out bit
    );
end TimeIntervalCounter;

architecture Behavioral of TimeIntervalCounter is
    type state_type is (IDLE, ARMED, MEASURING, DONE);
    signal state : state_type;
    signal counter : bit_vector(COUNTER_WIDTH-1 downto 0);
    signal result_reg : bit_vector(COUNTER_WIDTH-1 downto 0);
    signal start_sync, start_prev : bit;
    signal stop_sync, stop_prev : bit;

    function increment(val : bit_vector) return bit_vector is
        variable result : bit_vector(val'range);
        variable carry : bit := '1';
    begin
        for i in val'low to val'high loop
            result(i) := val(i) xor carry;
            carry := val(i) and carry;
        end loop;
        return result;
    end function;

begin
    process(clk, reset)
        variable start_edge, stop_edge : bit;
    begin
        if reset = '1' then
            state <= IDLE;
            counter <= (others => '0');
            result_reg <= (others => '0');
            start_sync <= '0';
            start_prev <= '0';
            stop_sync <= '0';
            stop_prev <= '0';
            measurement_valid <= '0';
            armed <= '0';
            measuring <= '0';
        elsif clk'event and clk = '1' then
            start_sync <= start_signal;
            start_prev <= start_sync;
            stop_sync <= stop_signal;
            stop_prev <= stop_sync;

            start_edge := start_sync and (not start_prev);
            stop_edge := stop_sync and (not stop_prev);

            measurement_valid <= '0';
            armed <= '0';
            measuring <= '0';

            if enable = '1' then
                case state is
                    when IDLE =>
                        if arm = '1' then
                            state <= ARMED;
                            counter <= (others => '0');
                        end if;

                    when ARMED =>
                        armed <= '1';
                        if start_edge = '1' then
                            state <= MEASURING;
                            counter <= (others => '0');
                        end if;

                    when MEASURING =>
                        measuring <= '1';
                        counter <= increment(counter);
                        if stop_edge = '1' then
                            result_reg <= counter;
                            state <= DONE;
                            measurement_valid <= '1';
                        end if;

                    when DONE =>
                        measurement_valid <= '1';
                        if arm = '1' then
                            state <= ARMED;
                            counter <= (others => '0');
                        end if;
                end case;
            end if;
        end if;
    end process;

    time_interval <= result_reg;

end Behavioral;

-- ============================================================================
-- Testbench
-- ============================================================================
entity TimestampCapture_TB is
end TimestampCapture_TB;

architecture Test of TimestampCapture_TB is
    signal tb_clk, tb_reset, tb_enable : bit;
    signal tb_inputs : bit_vector(3 downto 0);
    signal tb_capture_rising, tb_capture_falling : bit_vector(3 downto 0);
    signal tb_timer : bit_vector(31 downto 0);
    signal tb_cap0, tb_cap1, tb_cap2, tb_cap3 : bit_vector(31 downto 0);
    signal tb_flags, tb_clear : bit_vector(3 downto 0);
begin
    UUT: entity work.TimestampCapture
        generic map (NUM_CHANNELS => 4, TIMER_WIDTH => 32)
        port map (
            clk             => tb_clk,
            reset           => tb_reset,
            enable          => tb_enable,
            capture_inputs  => tb_inputs,
            capture_rising  => tb_capture_rising,
            capture_falling => tb_capture_falling,
            timer_value     => tb_timer,
            capture_ch0     => tb_cap0,
            capture_ch1     => tb_cap1,
            capture_ch2     => tb_cap2,
            capture_ch3     => tb_cap3,
            capture_flags   => tb_flags,
            clear_flags     => tb_clear
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
        tb_inputs <= "0000";
        tb_capture_rising <= "1111";
        tb_capture_falling <= "1111";
        tb_clear <= "0000";
        wait for 30 ns;

        tb_reset <= '0';
        tb_enable <= '1';
        wait for 100 ns;

        -- Trigger capture on channel 0
        tb_inputs(0) <= '1';
        wait for 50 ns;
        tb_inputs(0) <= '0';
        wait for 50 ns;

        -- Trigger capture on channel 1
        tb_inputs(1) <= '1';
        wait for 30 ns;
        tb_inputs(1) <= '0';
        wait for 100 ns;

        -- Clear flags
        tb_clear <= "0011";
        wait for 10 ns;
        tb_clear <= "0000";

        wait for 200 ns;
        wait;
    end process;
end Test;
