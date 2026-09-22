-- ============================================================================
-- VHDL Project: TrafficLight
-- Category: State Machines
-- Complexity: Medium-High
-- Description: 4-way intersection traffic light controller
--              Includes pedestrian crossing, emergency vehicle detection
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity TrafficLight is
    generic (
        CLK_FREQ_HZ : integer := 50000000;  -- 50 MHz
        GREEN_TIME  : integer := 30;         -- seconds
        YELLOW_TIME : integer := 5;          -- seconds
        ALL_RED_TIME : integer := 2          -- seconds
    );
    port (
        clk             : in  bit;
        reset           : in  bit;
        -- Sensors
        ns_sensor       : in  bit;  -- North-South traffic sensor
        ew_sensor       : in  bit;  -- East-West traffic sensor
        ped_button_ns   : in  bit;  -- Pedestrian button N-S crossing
        ped_button_ew   : in  bit;  -- Pedestrian button E-W crossing
        emergency       : in  bit;  -- Emergency vehicle detected
        -- North-South lights
        ns_red          : out bit;
        ns_yellow       : out bit;
        ns_green        : out bit;
        ns_ped_walk     : out bit;
        ns_ped_stop     : out bit;
        -- East-West lights
        ew_red          : out bit;
        ew_yellow       : out bit;
        ew_green        : out bit;
        ew_ped_walk     : out bit;
        ew_ped_stop     : out bit;
        -- Status
        state_out       : out bit_vector(3 downto 0)
    );
end TrafficLight;

architecture Behavioral of TrafficLight is
    -- States
    type state_type is (
        NS_GREEN,       -- N-S green, E-W red
        NS_YELLOW,      -- N-S yellow, E-W red
        ALL_RED_1,      -- All red transition
        EW_GREEN,       -- E-W green, N-S red
        EW_YELLOW,      -- E-W yellow, N-S red
        ALL_RED_2,      -- All red transition
        NS_PED,         -- N-S pedestrian crossing
        NS_PED_WARN,    -- N-S pedestrian warning (flashing)
        EW_PED,         -- E-W pedestrian crossing
        EW_PED_WARN,    -- E-W pedestrian warning
        EMERGENCY_MODE  -- All red, prepare for emergency
    );

    signal state : state_type;
    signal timer : integer range 0 to CLK_FREQ_HZ * 60;
    signal second_tick : bit;
    signal second_counter : integer range 0 to CLK_FREQ_HZ;

    -- Timing values in clock cycles
    constant GREEN_CYCLES : integer := GREEN_TIME;
    constant YELLOW_CYCLES : integer := YELLOW_TIME;
    constant ALL_RED_CYCLES : integer := ALL_RED_TIME;
    constant PED_WALK_TIME : integer := 15;
    constant PED_WARN_TIME : integer := 5;

    -- Pedestrian request latches
    signal ped_ns_request : bit;
    signal ped_ew_request : bit;

    -- Flash counter for pedestrian warning
    signal flash_counter : integer range 0 to 7;
    signal flash_state : bit;

begin
    -- Generate 1-second tick
    tick_gen: process(clk, reset)
    begin
        if reset = '1' then
            second_counter <= 0;
            second_tick <= '0';
        elsif clk'event and clk = '1' then
            if second_counter >= CLK_FREQ_HZ - 1 then
                second_counter <= 0;
                second_tick <= '1';
            else
                second_counter <= second_counter + 1;
                second_tick <= '0';
            end if;
        end if;
    end process;

    -- Flash generator (for pedestrian warning)
    flash_gen: process(clk, reset)
    begin
        if reset = '1' then
            flash_counter <= 0;
            flash_state <= '0';
        elsif clk'event and clk = '1' then
            if second_tick = '1' then
                if flash_counter >= 1 then
                    flash_counter <= 0;
                    flash_state <= not flash_state;
                else
                    flash_counter <= flash_counter + 1;
                end if;
            end if;
        end if;
    end process;

    -- Latch pedestrian requests
    ped_latch: process(clk, reset)
    begin
        if reset = '1' then
            ped_ns_request <= '0';
            ped_ew_request <= '0';
        elsif clk'event and clk = '1' then
            -- Latch requests
            if ped_button_ns = '1' then
                ped_ns_request <= '1';
            end if;
            if ped_button_ew = '1' then
                ped_ew_request <= '1';
            end if;

            -- Clear requests after serviced
            if state = NS_PED or state = NS_PED_WARN then
                ped_ns_request <= '0';
            end if;
            if state = EW_PED or state = EW_PED_WARN then
                ped_ew_request <= '0';
            end if;
        end if;
    end process;

    -- Main FSM
    fsm_proc: process(clk, reset)
    begin
        if reset = '1' then
            state <= ALL_RED_1;
            timer <= 0;
        elsif clk'event and clk = '1' then
            -- Emergency override
            if emergency = '1' then
                if state /= EMERGENCY_MODE then
                    state <= EMERGENCY_MODE;
                    timer <= 0;
                end if;
            else
                if second_tick = '1' then
                    case state is
                        when EMERGENCY_MODE =>
                            if timer >= ALL_RED_CYCLES then
                                state <= NS_GREEN;
                                timer <= 0;
                            else
                                timer <= timer + 1;
                            end if;

                        when NS_GREEN =>
                            if timer >= GREEN_CYCLES then
                                -- Check for pedestrian request
                                if ped_ns_request = '1' then
                                    state <= NS_PED;
                                    timer <= 0;
                                else
                                    state <= NS_YELLOW;
                                    timer <= 0;
                                end if;
                            else
                                timer <= timer + 1;
                            end if;

                        when NS_PED =>
                            if timer >= PED_WALK_TIME then
                                state <= NS_PED_WARN;
                                timer <= 0;
                            else
                                timer <= timer + 1;
                            end if;

                        when NS_PED_WARN =>
                            if timer >= PED_WARN_TIME then
                                state <= NS_YELLOW;
                                timer <= 0;
                            else
                                timer <= timer + 1;
                            end if;

                        when NS_YELLOW =>
                            if timer >= YELLOW_CYCLES then
                                state <= ALL_RED_1;
                                timer <= 0;
                            else
                                timer <= timer + 1;
                            end if;

                        when ALL_RED_1 =>
                            if timer >= ALL_RED_CYCLES then
                                state <= EW_GREEN;
                                timer <= 0;
                            else
                                timer <= timer + 1;
                            end if;

                        when EW_GREEN =>
                            if timer >= GREEN_CYCLES then
                                if ped_ew_request = '1' then
                                    state <= EW_PED;
                                    timer <= 0;
                                else
                                    state <= EW_YELLOW;
                                    timer <= 0;
                                end if;
                            else
                                timer <= timer + 1;
                            end if;

                        when EW_PED =>
                            if timer >= PED_WALK_TIME then
                                state <= EW_PED_WARN;
                                timer <= 0;
                            else
                                timer <= timer + 1;
                            end if;

                        when EW_PED_WARN =>
                            if timer >= PED_WARN_TIME then
                                state <= EW_YELLOW;
                                timer <= 0;
                            else
                                timer <= timer + 1;
                            end if;

                        when EW_YELLOW =>
                            if timer >= YELLOW_CYCLES then
                                state <= ALL_RED_2;
                                timer <= 0;
                            else
                                timer <= timer + 1;
                            end if;

                        when ALL_RED_2 =>
                            if timer >= ALL_RED_CYCLES then
                                state <= NS_GREEN;
                                timer <= 0;
                            else
                                timer <= timer + 1;
                            end if;
                    end case;
                end if;
            end if;
        end if;
    end process;

    -- Output logic
    output_proc: process(state, flash_state)
    begin
        -- Default all off
        ns_red <= '0'; ns_yellow <= '0'; ns_green <= '0';
        ew_red <= '0'; ew_yellow <= '0'; ew_green <= '0';
        ns_ped_walk <= '0'; ns_ped_stop <= '1';
        ew_ped_walk <= '0'; ew_ped_stop <= '1';

        case state is
            when NS_GREEN | NS_PED | NS_PED_WARN =>
                ns_green <= '1';
                ew_red <= '1';
                if state = NS_PED then
                    ns_ped_walk <= '1';
                    ns_ped_stop <= '0';
                elsif state = NS_PED_WARN then
                    ns_ped_walk <= flash_state;
                    ns_ped_stop <= not flash_state;
                end if;

            when NS_YELLOW =>
                ns_yellow <= '1';
                ew_red <= '1';

            when EW_GREEN | EW_PED | EW_PED_WARN =>
                ew_green <= '1';
                ns_red <= '1';
                if state = EW_PED then
                    ew_ped_walk <= '1';
                    ew_ped_stop <= '0';
                elsif state = EW_PED_WARN then
                    ew_ped_walk <= flash_state;
                    ew_ped_stop <= not flash_state;
                end if;

            when EW_YELLOW =>
                ew_yellow <= '1';
                ns_red <= '1';

            when ALL_RED_1 | ALL_RED_2 | EMERGENCY_MODE =>
                ns_red <= '1';
                ew_red <= '1';
        end case;
    end process;

    -- State encoding output
    with state select state_out <=
        "0000" when NS_GREEN,
        "0001" when NS_YELLOW,
        "0010" when ALL_RED_1,
        "0011" when EW_GREEN,
        "0100" when EW_YELLOW,
        "0101" when ALL_RED_2,
        "0110" when NS_PED,
        "0111" when NS_PED_WARN,
        "1000" when EW_PED,
        "1001" when EW_PED_WARN,
        "1111" when EMERGENCY_MODE;

end Behavioral;

-- ============================================================================
-- Simple 2-Phase Traffic Light (for smaller intersections)
-- ============================================================================
entity SimpleTrafficLight is
    port (
        clk         : in  bit;
        reset       : in  bit;
        sensor      : in  bit;  -- Cross traffic sensor
        main_red    : out bit;
        main_yellow : out bit;
        main_green  : out bit;
        side_red    : out bit;
        side_yellow : out bit;
        side_green  : out bit
    );
end SimpleTrafficLight;

architecture Behavioral of SimpleTrafficLight is
    type state_type is (MAIN_GREEN, MAIN_YELLOW, SIDE_GREEN, SIDE_YELLOW);
    signal state : state_type;
    signal timer : integer range 0 to 63;
begin
    process(clk, reset)
    begin
        if reset = '1' then
            state <= MAIN_GREEN;
            timer <= 0;
        elsif clk'event and clk = '1' then
            timer <= timer + 1;

            case state is
                when MAIN_GREEN =>
                    if timer >= 30 and sensor = '1' then
                        state <= MAIN_YELLOW;
                        timer <= 0;
                    elsif timer >= 60 then
                        state <= MAIN_YELLOW;
                        timer <= 0;
                    end if;

                when MAIN_YELLOW =>
                    if timer >= 5 then
                        state <= SIDE_GREEN;
                        timer <= 0;
                    end if;

                when SIDE_GREEN =>
                    if timer >= 20 then
                        state <= SIDE_YELLOW;
                        timer <= 0;
                    end if;

                when SIDE_YELLOW =>
                    if timer >= 5 then
                        state <= MAIN_GREEN;
                        timer <= 0;
                    end if;
            end case;
        end if;
    end process;

    main_green <= '1' when state = MAIN_GREEN else '0';
    main_yellow <= '1' when state = MAIN_YELLOW else '0';
    main_red <= '1' when state = SIDE_GREEN or state = SIDE_YELLOW else '0';

    side_green <= '1' when state = SIDE_GREEN else '0';
    side_yellow <= '1' when state = SIDE_YELLOW else '0';
    side_red <= '1' when state = MAIN_GREEN or state = MAIN_YELLOW else '0';

end Behavioral;

-- ============================================================================
-- Testbench
-- ============================================================================
entity TrafficLight_TB is
end TrafficLight_TB;

architecture Test of TrafficLight_TB is
    signal tb_clk, tb_reset : bit;
    signal tb_ns_sensor, tb_ew_sensor : bit;
    signal tb_ped_ns, tb_ped_ew : bit;
    signal tb_emergency : bit;
    signal tb_ns_r, tb_ns_y, tb_ns_g : bit;
    signal tb_ew_r, tb_ew_y, tb_ew_g : bit;
    signal tb_ns_walk, tb_ns_stop : bit;
    signal tb_ew_walk, tb_ew_stop : bit;
    signal tb_state : bit_vector(3 downto 0);
begin
    UUT: entity work.TrafficLight
        generic map (
            CLK_FREQ_HZ => 10,  -- Fast simulation
            GREEN_TIME  => 3,
            YELLOW_TIME => 1,
            ALL_RED_TIME => 1
        )
        port map (
            clk           => tb_clk,
            reset         => tb_reset,
            ns_sensor     => tb_ns_sensor,
            ew_sensor     => tb_ew_sensor,
            ped_button_ns => tb_ped_ns,
            ped_button_ew => tb_ped_ew,
            emergency     => tb_emergency,
            ns_red        => tb_ns_r,
            ns_yellow     => tb_ns_y,
            ns_green      => tb_ns_g,
            ns_ped_walk   => tb_ns_walk,
            ns_ped_stop   => tb_ns_stop,
            ew_red        => tb_ew_r,
            ew_yellow     => tb_ew_y,
            ew_green      => tb_ew_g,
            ew_ped_walk   => tb_ew_walk,
            ew_ped_stop   => tb_ew_stop,
            state_out     => tb_state
        );

    clk_gen: process
    begin
        tb_clk <= '0'; wait for 5 ns;
        tb_clk <= '1'; wait for 5 ns;
    end process;

    stimulus: process
    begin
        tb_reset <= '1';
        tb_ns_sensor <= '0';
        tb_ew_sensor <= '0';
        tb_ped_ns <= '0';
        tb_ped_ew <= '0';
        tb_emergency <= '0';
        wait for 50 ns;

        tb_reset <= '0';

        -- Let it run through normal cycle
        wait for 500 ns;

        -- Request pedestrian crossing
        tb_ped_ns <= '1';
        wait for 20 ns;
        tb_ped_ns <= '0';

        wait for 500 ns;

        -- Emergency vehicle
        tb_emergency <= '1';
        wait for 100 ns;
        tb_emergency <= '0';

        wait for 500 ns;
        wait;
    end process;
end Test;
