-- ============================================================================
-- VHDL Project: EdgeDetector
-- Category: Timing & Synchronization
-- Complexity: Low
-- Description: Edge detection module for rising, falling, and both edges
--              Includes filtering/glitch suppression and pulse stretching
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity EdgeDetector is
    generic (
        FILTER_CYCLES : integer := 3   -- Number of stable cycles for filtering
    );
    port (
        clk             : in  bit;
        reset           : in  bit;
        -- Input signal
        signal_in       : in  bit;
        -- Edge outputs (single clock pulse)
        rising_edge_out : out bit;
        falling_edge_out: out bit;
        any_edge_out    : out bit;
        -- Filtered outputs
        filtered_out    : out bit;
        -- Stretched outputs (configurable width)
        stretch_width   : in  bit_vector(3 downto 0);
        rising_stretched: out bit;
        falling_stretched: out bit
    );
end EdgeDetector;

architecture Behavioral of EdgeDetector is
    -- Synchronization registers (2-stage for metastability)
    signal sync_reg1, sync_reg2 : bit;
    signal sync_signal : bit;

    -- Edge detection
    signal prev_signal : bit;
    signal rising_detected, falling_detected : bit;

    -- Filter
    signal filter_counter : integer range 0 to FILTER_CYCLES;
    signal filter_state : bit;
    signal filtered_signal : bit;

    -- Stretch counters
    signal rising_stretch_cnt : integer range 0 to 15;
    signal falling_stretch_cnt : integer range 0 to 15;
    signal rising_stretched_int : bit;
    signal falling_stretched_int : bit;

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
    -- Input synchronization (prevent metastability)
    sync_proc: process(clk, reset)
    begin
        if reset = '1' then
            sync_reg1 <= '0';
            sync_reg2 <= '0';
        elsif clk'event and clk = '1' then
            sync_reg1 <= signal_in;
            sync_reg2 <= sync_reg1;
        end if;
    end process sync_proc;

    sync_signal <= sync_reg2;

    -- Edge detection
    edge_proc: process(clk, reset)
    begin
        if reset = '1' then
            prev_signal <= '0';
            rising_detected <= '0';
            falling_detected <= '0';
        elsif clk'event and clk = '1' then
            prev_signal <= sync_signal;
            rising_detected <= sync_signal and (not prev_signal);
            falling_detected <= (not sync_signal) and prev_signal;
        end if;
    end process edge_proc;

    rising_edge_out <= rising_detected;
    falling_edge_out <= falling_detected;
    any_edge_out <= rising_detected or falling_detected;

    -- Digital filter (glitch suppression)
    filter_proc: process(clk, reset)
    begin
        if reset = '1' then
            filter_counter <= 0;
            filter_state <= '0';
            filtered_signal <= '0';
        elsif clk'event and clk = '1' then
            if sync_signal = filter_state then
                -- Input matches current state, reset counter
                filter_counter <= 0;
            else
                -- Input differs, increment counter
                if filter_counter >= FILTER_CYCLES - 1 then
                    -- Stable for enough cycles, change state
                    filter_state <= sync_signal;
                    filtered_signal <= sync_signal;
                    filter_counter <= 0;
                else
                    filter_counter <= filter_counter + 1;
                end if;
            end if;
        end if;
    end process filter_proc;

    filtered_out <= filtered_signal;

    -- Pulse stretching for rising edge
    rising_stretch_proc: process(clk, reset)
        variable stretch_val : integer;
    begin
        if reset = '1' then
            rising_stretch_cnt <= 0;
            rising_stretched_int <= '0';
        elsif clk'event and clk = '1' then
            stretch_val := to_integer(stretch_width);
            if stretch_val = 0 then stretch_val := 1; end if;

            if rising_detected = '1' then
                -- Start stretch
                rising_stretch_cnt <= stretch_val;
                rising_stretched_int <= '1';
            elsif rising_stretch_cnt > 0 then
                rising_stretch_cnt <= rising_stretch_cnt - 1;
                if rising_stretch_cnt = 1 then
                    rising_stretched_int <= '0';
                end if;
            end if;
        end if;
    end process rising_stretch_proc;

    rising_stretched <= rising_stretched_int;

    -- Pulse stretching for falling edge
    falling_stretch_proc: process(clk, reset)
        variable stretch_val : integer;
    begin
        if reset = '1' then
            falling_stretch_cnt <= 0;
            falling_stretched_int <= '0';
        elsif clk'event and clk = '1' then
            stretch_val := to_integer(stretch_width);
            if stretch_val = 0 then stretch_val := 1; end if;

            if falling_detected = '1' then
                falling_stretch_cnt <= stretch_val;
                falling_stretched_int <= '1';
            elsif falling_stretch_cnt > 0 then
                falling_stretch_cnt <= falling_stretch_cnt - 1;
                if falling_stretch_cnt = 1 then
                    falling_stretched_int <= '0';
                end if;
            end if;
        end if;
    end process falling_stretch_proc;

    falling_stretched <= falling_stretched_int;

end Behavioral;

-- ============================================================================
-- Multi-Channel Edge Detector
-- ============================================================================
entity MultiChannelEdgeDetector is
    generic (
        NUM_CHANNELS : integer := 8
    );
    port (
        clk             : in  bit;
        reset           : in  bit;
        signals_in      : in  bit_vector(NUM_CHANNELS-1 downto 0);
        rising_edges    : out bit_vector(NUM_CHANNELS-1 downto 0);
        falling_edges   : out bit_vector(NUM_CHANNELS-1 downto 0);
        any_edge        : out bit  -- OR of all edges
    );
end MultiChannelEdgeDetector;

architecture Behavioral of MultiChannelEdgeDetector is
    signal prev_signals : bit_vector(NUM_CHANNELS-1 downto 0);
    signal sync_signals : bit_vector(NUM_CHANNELS-1 downto 0);
    signal rising_internal : bit_vector(NUM_CHANNELS-1 downto 0);
    signal falling_internal : bit_vector(NUM_CHANNELS-1 downto 0);
begin
    -- Synchronization
    sync_proc: process(clk, reset)
    begin
        if reset = '1' then
            sync_signals <= (others => '0');
        elsif clk'event and clk = '1' then
            sync_signals <= signals_in;
        end if;
    end process sync_proc;

    -- Edge detection for all channels
    edge_proc: process(clk, reset)
    begin
        if reset = '1' then
            prev_signals <= (others => '0');
            rising_internal <= (others => '0');
            falling_internal <= (others => '0');
        elsif clk'event and clk = '1' then
            prev_signals <= sync_signals;
            for i in 0 to NUM_CHANNELS-1 loop
                rising_internal(i) <= sync_signals(i) and (not prev_signals(i));
                falling_internal(i) <= (not sync_signals(i)) and prev_signals(i);
            end loop;
        end if;
    end process edge_proc;

    rising_edges <= rising_internal;
    falling_edges <= falling_internal;

    -- Any edge detection
    any_edge_proc: process(rising_internal, falling_internal)
        variable any_r, any_f : bit;
    begin
        any_r := '0';
        any_f := '0';
        for i in 0 to NUM_CHANNELS-1 loop
            any_r := any_r or rising_internal(i);
            any_f := any_f or falling_internal(i);
        end loop;
        any_edge <= any_r or any_f;
    end process any_edge_proc;

end Behavioral;

-- ============================================================================
-- Edge Counter
-- ============================================================================
entity EdgeCounter is
    generic (
        COUNTER_WIDTH : integer := 16
    );
    port (
        clk         : in  bit;
        reset       : in  bit;
        signal_in   : in  bit;
        clear       : in  bit;
        -- Configuration
        count_rising : in  bit;
        count_falling: in  bit;
        -- Outputs
        edge_count  : out bit_vector(COUNTER_WIDTH-1 downto 0);
        overflow    : out bit
    );
end EdgeCounter;

architecture Behavioral of EdgeCounter is
    signal sync_reg, prev_reg : bit;
    signal rising, falling : bit;
    signal counter : bit_vector(COUNTER_WIDTH-1 downto 0);
    signal overflow_reg : bit;

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

    function is_max(val : bit_vector) return bit is
        variable result : bit := '1';
    begin
        for i in val'range loop
            result := result and val(i);
        end loop;
        return result;
    end function;

begin
    process(clk, reset)
        variable do_count : bit;
    begin
        if reset = '1' then
            sync_reg <= '0';
            prev_reg <= '0';
            counter <= (others => '0');
            overflow_reg <= '0';
        elsif clk'event and clk = '1' then
            sync_reg <= signal_in;
            prev_reg <= sync_reg;

            rising <= sync_reg and (not prev_reg);
            falling <= (not sync_reg) and prev_reg;

            if clear = '1' then
                counter <= (others => '0');
                overflow_reg <= '0';
            else
                do_count := (rising and count_rising) or (falling and count_falling);
                if do_count = '1' then
                    if is_max(counter) = '1' then
                        overflow_reg <= '1';
                    else
                        counter <= increment(counter);
                    end if;
                end if;
            end if;
        end if;
    end process;

    edge_count <= counter;
    overflow <= overflow_reg;

end Behavioral;

-- ============================================================================
-- Frequency Edge Detector (detects signal presence)
-- ============================================================================
entity SignalPresenceDetector is
    generic (
        TIMEOUT_CYCLES : integer := 1000
    );
    port (
        clk         : in  bit;
        reset       : in  bit;
        signal_in   : in  bit;
        -- Outputs
        signal_present : out bit;  -- High if edges detected recently
        signal_lost    : out bit;  -- Pulse when signal is lost
        signal_found   : out bit   -- Pulse when signal is found
    );
end SignalPresenceDetector;

architecture Behavioral of SignalPresenceDetector is
    signal sync_reg, prev_reg : bit;
    signal any_edge : bit;
    signal timeout_counter : integer range 0 to TIMEOUT_CYCLES;
    signal present_reg : bit;
    signal present_prev : bit;
begin
    process(clk, reset)
    begin
        if reset = '1' then
            sync_reg <= '0';
            prev_reg <= '0';
            timeout_counter <= 0;
            present_reg <= '0';
            present_prev <= '0';
        elsif clk'event and clk = '1' then
            sync_reg <= signal_in;
            prev_reg <= sync_reg;
            present_prev <= present_reg;

            any_edge <= (sync_reg xor prev_reg);

            if any_edge = '1' then
                -- Edge detected, reset timeout
                timeout_counter <= 0;
                present_reg <= '1';
            else
                -- No edge, count towards timeout
                if timeout_counter >= TIMEOUT_CYCLES - 1 then
                    present_reg <= '0';
                else
                    timeout_counter <= timeout_counter + 1;
                end if;
            end if;
        end if;
    end process;

    signal_present <= present_reg;
    signal_lost <= present_prev and (not present_reg);
    signal_found <= (not present_prev) and present_reg;

end Behavioral;

-- ============================================================================
-- Testbench
-- ============================================================================
entity EdgeDetector_TB is
end EdgeDetector_TB;

architecture Test of EdgeDetector_TB is
    signal tb_clk, tb_reset : bit;
    signal tb_signal_in : bit;
    signal tb_rising, tb_falling, tb_any : bit;
    signal tb_filtered : bit;
    signal tb_stretch_width : bit_vector(3 downto 0);
    signal tb_rising_stretched, tb_falling_stretched : bit;
begin
    UUT: entity work.EdgeDetector
        generic map (FILTER_CYCLES => 3)
        port map (
            clk              => tb_clk,
            reset            => tb_reset,
            signal_in        => tb_signal_in,
            rising_edge_out  => tb_rising,
            falling_edge_out => tb_falling,
            any_edge_out     => tb_any,
            filtered_out     => tb_filtered,
            stretch_width    => tb_stretch_width,
            rising_stretched => tb_rising_stretched,
            falling_stretched => tb_falling_stretched
        );

    clk_gen: process
    begin
        tb_clk <= '0'; wait for 5 ns;
        tb_clk <= '1'; wait for 5 ns;
    end process;

    stimulus: process
    begin
        tb_reset <= '1';
        tb_signal_in <= '0';
        tb_stretch_width <= "0100";  -- Stretch by 4
        wait for 30 ns;

        tb_reset <= '0';
        wait for 20 ns;

        -- Rising edge
        tb_signal_in <= '1';
        wait for 50 ns;

        -- Falling edge
        tb_signal_in <= '0';
        wait for 50 ns;

        -- Glitch (should be filtered)
        tb_signal_in <= '1';
        wait for 10 ns;
        tb_signal_in <= '0';
        wait for 10 ns;
        tb_signal_in <= '1';
        wait for 50 ns;

        -- Clean falling edge
        tb_signal_in <= '0';
        wait for 100 ns;

        wait;
    end process;
end Test;
