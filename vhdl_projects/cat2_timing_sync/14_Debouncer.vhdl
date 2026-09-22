-- ============================================================================
-- VHDL Project: Debouncer
-- Category: Timing & Synchronization
-- Complexity: Medium
-- Description: Multi-channel debounce circuit with configurable time
--              Handles mechanical switch bounce, includes edge outputs
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity Debouncer is
    generic (
        NUM_INPUTS      : integer := 8;
        DEBOUNCE_CYCLES : integer := 50000  -- At 50MHz: 1ms debounce
    );
    port (
        clk             : in  bit;
        reset           : in  bit;
        -- Raw inputs
        buttons_in      : in  bit_vector(NUM_INPUTS-1 downto 0);
        -- Debounced outputs
        buttons_out     : out bit_vector(NUM_INPUTS-1 downto 0);
        -- Edge pulses (single clock cycle)
        buttons_pressed : out bit_vector(NUM_INPUTS-1 downto 0);  -- Rising edge
        buttons_released: out bit_vector(NUM_INPUTS-1 downto 0);  -- Falling edge
        -- Status
        any_pressed     : out bit;
        all_released    : out bit
    );
end Debouncer;

architecture Behavioral of Debouncer is
    -- Synchronization stages
    type sync_array is array (0 to NUM_INPUTS-1) of bit_vector(1 downto 0);
    signal sync_regs : sync_array;

    -- Debounce counters
    type counter_array is array (0 to NUM_INPUTS-1) of integer range 0 to DEBOUNCE_CYCLES;
    signal debounce_counters : counter_array;

    -- Debounced state
    signal debounced_state : bit_vector(NUM_INPUTS-1 downto 0);
    signal prev_state : bit_vector(NUM_INPUTS-1 downto 0);

begin
    -- Process each input channel
    debounce_gen: for i in 0 to NUM_INPUTS-1 generate
        process(clk, reset)
            variable synced_input : bit;
        begin
            if reset = '1' then
                sync_regs(i) <= "00";
                debounce_counters(i) <= 0;
                debounced_state(i) <= '0';
                prev_state(i) <= '0';
            elsif clk'event and clk = '1' then
                -- 2-stage synchronizer
                sync_regs(i)(0) <= buttons_in(i);
                sync_regs(i)(1) <= sync_regs(i)(0);
                synced_input := sync_regs(i)(1);

                -- Save previous state for edge detection
                prev_state(i) <= debounced_state(i);

                -- Debounce logic
                if synced_input /= debounced_state(i) then
                    -- Input differs from debounced state
                    if debounce_counters(i) >= DEBOUNCE_CYCLES - 1 then
                        -- Stable for long enough, change state
                        debounced_state(i) <= synced_input;
                        debounce_counters(i) <= 0;
                    else
                        debounce_counters(i) <= debounce_counters(i) + 1;
                    end if;
                else
                    -- Input matches debounced state, reset counter
                    debounce_counters(i) <= 0;
                end if;
            end if;
        end process;
    end generate debounce_gen;

    -- Output assignments
    buttons_out <= debounced_state;

    -- Edge detection
    edge_gen: for i in 0 to NUM_INPUTS-1 generate
        buttons_pressed(i) <= debounced_state(i) and (not prev_state(i));
        buttons_released(i) <= (not debounced_state(i)) and prev_state(i);
    end generate edge_gen;

    -- Status outputs
    status_proc: process(debounced_state)
        variable any_high, all_low : bit;
    begin
        any_high := '0';
        all_low := '1';
        for i in 0 to NUM_INPUTS-1 loop
            any_high := any_high or debounced_state(i);
            all_low := all_low and (not debounced_state(i));
        end loop;
        any_pressed <= any_high;
        all_released <= all_low;
    end process status_proc;

end Behavioral;

-- ============================================================================
-- Advanced Debouncer with Configurable Parameters
-- ============================================================================
entity AdvancedDebouncer is
    generic (
        NUM_INPUTS : integer := 4
    );
    port (
        clk             : in  bit;
        reset           : in  bit;
        -- Configuration (runtime adjustable)
        debounce_time   : in  bit_vector(15 downto 0);  -- Debounce cycles
        active_level    : in  bit;  -- '0' = active low buttons, '1' = active high
        -- Inputs
        raw_inputs      : in  bit_vector(NUM_INPUTS-1 downto 0);
        -- Outputs
        debounced       : out bit_vector(NUM_INPUTS-1 downto 0);
        pressed         : out bit_vector(NUM_INPUTS-1 downto 0);
        released        : out bit_vector(NUM_INPUTS-1 downto 0);
        held            : out bit_vector(NUM_INPUTS-1 downto 0);  -- Pressed and stable
        -- Hold detection
        hold_time       : in  bit_vector(15 downto 0);  -- Cycles for "held"
        long_press      : out bit_vector(NUM_INPUTS-1 downto 0)  -- Long press detected
    );
end AdvancedDebouncer;

architecture Behavioral of AdvancedDebouncer is
    type counter_array is array (0 to NUM_INPUTS-1) of integer range 0 to 65535;
    signal deb_counters : counter_array;
    signal hold_counters : counter_array;

    signal sync_stage1, sync_stage2 : bit_vector(NUM_INPUTS-1 downto 0);
    signal synced_inputs : bit_vector(NUM_INPUTS-1 downto 0);
    signal adjusted_inputs : bit_vector(NUM_INPUTS-1 downto 0);
    signal deb_state : bit_vector(NUM_INPUTS-1 downto 0);
    signal prev_deb_state : bit_vector(NUM_INPUTS-1 downto 0);

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
    -- Synchronization
    sync_proc: process(clk, reset)
    begin
        if reset = '1' then
            sync_stage1 <= (others => '0');
            sync_stage2 <= (others => '0');
        elsif clk'event and clk = '1' then
            sync_stage1 <= raw_inputs;
            sync_stage2 <= sync_stage1;
        end if;
    end process sync_proc;

    synced_inputs <= sync_stage2;

    -- Adjust for active level
    adjust_gen: for i in 0 to NUM_INPUTS-1 generate
        adjusted_inputs(i) <= synced_inputs(i) xor (not active_level);
    end generate adjust_gen;

    -- Main debounce and hold detection
    main_proc: process(clk, reset)
        variable deb_time_val, hold_time_val : integer;
    begin
        if reset = '1' then
            deb_state <= (others => '0');
            prev_deb_state <= (others => '0');
            for i in 0 to NUM_INPUTS-1 loop
                deb_counters(i) <= 0;
                hold_counters(i) <= 0;
            end loop;
            held <= (others => '0');
            long_press <= (others => '0');
        elsif clk'event and clk = '1' then
            deb_time_val := to_integer(debounce_time);
            hold_time_val := to_integer(hold_time);
            if deb_time_val = 0 then deb_time_val := 1; end if;
            if hold_time_val = 0 then hold_time_val := 1; end if;

            prev_deb_state <= deb_state;

            for i in 0 to NUM_INPUTS-1 loop
                -- Debounce logic
                if adjusted_inputs(i) /= deb_state(i) then
                    if deb_counters(i) >= deb_time_val - 1 then
                        deb_state(i) <= adjusted_inputs(i);
                        deb_counters(i) <= 0;
                        hold_counters(i) <= 0;  -- Reset hold on state change
                    else
                        deb_counters(i) <= deb_counters(i) + 1;
                    end if;
                else
                    deb_counters(i) <= 0;
                end if;

                -- Hold detection
                long_press(i) <= '0';  -- Default: no long press pulse
                if deb_state(i) = '1' then
                    held(i) <= '1';
                    if hold_counters(i) >= hold_time_val - 1 then
                        if hold_counters(i) = hold_time_val - 1 then
                            long_press(i) <= '1';  -- Single pulse
                        end if;
                        -- Keep counter at max to prevent re-trigger
                        hold_counters(i) <= hold_time_val;
                    else
                        hold_counters(i) <= hold_counters(i) + 1;
                    end if;
                else
                    held(i) <= '0';
                    hold_counters(i) <= 0;
                end if;
            end loop;
        end if;
    end process main_proc;

    -- Output assignments
    debounced <= deb_state;

    -- Edge detection
    edge_gen: for i in 0 to NUM_INPUTS-1 generate
        pressed(i) <= deb_state(i) and (not prev_deb_state(i));
        released(i) <= (not deb_state(i)) and prev_deb_state(i);
    end generate edge_gen;

end Behavioral;

-- ============================================================================
-- Rotary Encoder Debouncer
-- ============================================================================
entity RotaryEncoderDebouncer is
    generic (
        DEBOUNCE_CYCLES : integer := 5000
    );
    port (
        clk         : in  bit;
        reset       : in  bit;
        -- Encoder inputs
        enc_a       : in  bit;
        enc_b       : in  bit;
        enc_sw      : in  bit;  -- Push button
        -- Outputs
        position    : out bit_vector(15 downto 0);  -- Accumulated position
        direction   : out bit;  -- '1' = CW, '0' = CCW
        step        : out bit;  -- Pulse on each step
        button      : out bit;  -- Debounced button
        button_press: out bit   -- Button press edge
    );
end RotaryEncoderDebouncer;

architecture Behavioral of RotaryEncoderDebouncer is
    -- Synchronization
    signal sync_a, sync_b : bit_vector(1 downto 0);
    signal a_stable, b_stable : bit;
    signal a_prev, b_prev : bit;

    -- Debounce counters
    signal a_counter, b_counter, sw_counter : integer range 0 to DEBOUNCE_CYCLES;
    signal a_deb, b_deb, sw_deb, sw_prev : bit;

    -- Position tracking
    signal pos_counter : integer range -32768 to 32767;
    signal dir_reg : bit;
    signal step_pulse : bit;

    -- State machine for quadrature decoding
    signal state : bit_vector(1 downto 0);
    signal prev_state : bit_vector(1 downto 0);

begin
    -- Synchronization
    sync_proc: process(clk, reset)
    begin
        if reset = '1' then
            sync_a <= "00";
            sync_b <= "00";
        elsif clk'event and clk = '1' then
            sync_a <= sync_a(0) & enc_a;
            sync_b <= sync_b(0) & enc_b;
        end if;
    end process sync_proc;

    a_stable <= sync_a(1);
    b_stable <= sync_b(1);

    -- Debounce A and B
    debounce_proc: process(clk, reset)
    begin
        if reset = '1' then
            a_counter <= 0;
            b_counter <= 0;
            sw_counter <= 0;
            a_deb <= '0';
            b_deb <= '0';
            sw_deb <= '0';
        elsif clk'event and clk = '1' then
            -- Debounce A
            if a_stable /= a_deb then
                if a_counter >= DEBOUNCE_CYCLES - 1 then
                    a_deb <= a_stable;
                    a_counter <= 0;
                else
                    a_counter <= a_counter + 1;
                end if;
            else
                a_counter <= 0;
            end if;

            -- Debounce B
            if b_stable /= b_deb then
                if b_counter >= DEBOUNCE_CYCLES - 1 then
                    b_deb <= b_stable;
                    b_counter <= 0;
                else
                    b_counter <= b_counter + 1;
                end if;
            else
                b_counter <= 0;
            end if;

            -- Debounce switch
            if enc_sw /= sw_deb then
                if sw_counter >= DEBOUNCE_CYCLES - 1 then
                    sw_deb <= enc_sw;
                    sw_counter <= 0;
                else
                    sw_counter <= sw_counter + 1;
                end if;
            else
                sw_counter <= 0;
            end if;
        end if;
    end process debounce_proc;

    -- Quadrature decoding
    state <= a_deb & b_deb;

    decode_proc: process(clk, reset)
    begin
        if reset = '1' then
            prev_state <= "00";
            pos_counter <= 0;
            dir_reg <= '0';
            step_pulse <= '0';
            a_prev <= '0';
            b_prev <= '0';
            sw_prev <= '0';
        elsif clk'event and clk = '1' then
            prev_state <= state;
            step_pulse <= '0';
            a_prev <= a_deb;
            b_prev <= b_deb;
            sw_prev <= sw_deb;

            -- Detect valid transitions
            -- CW: 00->01->11->10->00
            -- CCW: 00->10->11->01->00

            if state /= prev_state then
                case prev_state is
                    when "00" =>
                        if state = "01" then
                            -- CW
                            pos_counter <= pos_counter + 1;
                            dir_reg <= '1';
                            step_pulse <= '1';
                        elsif state = "10" then
                            -- CCW
                            pos_counter <= pos_counter - 1;
                            dir_reg <= '0';
                            step_pulse <= '1';
                        end if;
                    when "01" =>
                        if state = "11" then
                            pos_counter <= pos_counter + 1;
                            dir_reg <= '1';
                            step_pulse <= '1';
                        elsif state = "00" then
                            pos_counter <= pos_counter - 1;
                            dir_reg <= '0';
                            step_pulse <= '1';
                        end if;
                    when "11" =>
                        if state = "10" then
                            pos_counter <= pos_counter + 1;
                            dir_reg <= '1';
                            step_pulse <= '1';
                        elsif state = "01" then
                            pos_counter <= pos_counter - 1;
                            dir_reg <= '0';
                            step_pulse <= '1';
                        end if;
                    when "10" =>
                        if state = "00" then
                            pos_counter <= pos_counter + 1;
                            dir_reg <= '1';
                            step_pulse <= '1';
                        elsif state = "11" then
                            pos_counter <= pos_counter - 1;
                            dir_reg <= '0';
                            step_pulse <= '1';
                        end if;
                    when others => null;
                end case;
            end if;
        end if;
    end process decode_proc;

    -- Convert position to bit vector
    pos_conv: process(pos_counter)
        variable temp : integer;
    begin
        temp := pos_counter;
        if temp < 0 then temp := temp + 65536; end if;
        for i in 0 to 15 loop
            if (temp mod 2) = 1 then
                position(i) <= '1';
            else
                position(i) <= '0';
            end if;
            temp := temp / 2;
        end loop;
    end process pos_conv;

    direction <= dir_reg;
    step <= step_pulse;
    button <= sw_deb;
    button_press <= sw_deb and (not sw_prev);

end Behavioral;

-- ============================================================================
-- Testbench
-- ============================================================================
entity Debouncer_TB is
end Debouncer_TB;

architecture Test of Debouncer_TB is
    signal tb_clk, tb_reset : bit;
    signal tb_buttons_in : bit_vector(7 downto 0);
    signal tb_buttons_out : bit_vector(7 downto 0);
    signal tb_pressed, tb_released : bit_vector(7 downto 0);
begin
    UUT: entity work.Debouncer
        generic map (
            NUM_INPUTS => 8,
            DEBOUNCE_CYCLES => 10  -- Short for testing
        )
        port map (
            clk              => tb_clk,
            reset            => tb_reset,
            buttons_in       => tb_buttons_in,
            buttons_out      => tb_buttons_out,
            buttons_pressed  => tb_pressed,
            buttons_released => tb_released,
            any_pressed      => open,
            all_released     => open
        );

    clk_gen: process
    begin
        tb_clk <= '0'; wait for 5 ns;
        tb_clk <= '1'; wait for 5 ns;
    end process;

    stimulus: process
    begin
        tb_reset <= '1';
        tb_buttons_in <= "00000000";
        wait for 30 ns;

        tb_reset <= '0';
        wait for 20 ns;

        -- Button 0 press with bounce
        tb_buttons_in(0) <= '1'; wait for 10 ns;
        tb_buttons_in(0) <= '0'; wait for 10 ns;
        tb_buttons_in(0) <= '1'; wait for 10 ns;
        tb_buttons_in(0) <= '0'; wait for 10 ns;
        tb_buttons_in(0) <= '1'; wait for 200 ns;  -- Stable

        -- Button 0 release with bounce
        tb_buttons_in(0) <= '0'; wait for 10 ns;
        tb_buttons_in(0) <= '1'; wait for 10 ns;
        tb_buttons_in(0) <= '0'; wait for 200 ns;  -- Stable

        -- Multiple buttons
        tb_buttons_in <= "10100101";
        wait for 200 ns;

        tb_buttons_in <= "00000000";
        wait for 200 ns;

        wait;
    end process;
end Test;
