-- ============================================================================
-- VHDL Project: WashingMachine
-- Category: State Machines
-- Complexity: High
-- Description: Full washing machine cycle controller
--              Multiple programs, water level, temperature, spin speed
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity WashingMachine is
    generic (
        CLK_FREQ : integer := 1000  -- Simplified for simulation
    );
    port (
        clk             : in  bit;
        reset           : in  bit;
        -- User interface
        start_button    : in  bit;
        pause_button    : in  bit;
        program_select  : in  bit_vector(2 downto 0);  -- 8 programs
        temp_select     : in  bit_vector(1 downto 0);  -- Cold, 30, 40, 60
        spin_select     : in  bit_vector(1 downto 0);  -- 400, 800, 1200, 1400 RPM
        -- Sensors
        door_closed     : in  bit;
        water_level     : in  bit_vector(2 downto 0);  -- 0-7 levels
        water_temp      : in  bit_vector(7 downto 0);  -- Temperature sensor
        drum_speed      : in  bit_vector(10 downto 0); -- RPM feedback
        load_imbalance  : in  bit;
        -- Actuators
        door_lock       : out bit;
        water_inlet     : out bit;
        water_drain     : out bit;
        heater          : out bit;
        motor_enable    : out bit;
        motor_direction : out bit;  -- '0' = CW, '1' = CCW
        motor_speed     : out bit_vector(10 downto 0);
        detergent_valve : out bit;
        softener_valve  : out bit;
        -- Display
        current_phase   : out bit_vector(3 downto 0);
        time_remaining  : out bit_vector(7 downto 0);
        error_code      : out bit_vector(3 downto 0);
        -- Status
        running         : out bit;
        finished        : out bit
    );
end WashingMachine;

architecture Behavioral of WashingMachine is
    -- Main states
    type state_type is (
        IDLE,
        LOCK_DOOR,
        FILL_DETERGENT,
        FILL_WATER,
        HEAT_WATER,
        WASH_CW,
        WASH_CCW,
        DRAIN_WASH,
        FILL_RINSE,
        RINSE_CW,
        RINSE_CCW,
        DRAIN_RINSE,
        ADD_SOFTENER,
        FINAL_RINSE,
        DRAIN_FINAL,
        SPIN,
        REDISTRIBUTE,
        FINISH,
        PAUSED,
        ERROR_STATE
    );

    signal state : state_type;
    signal return_state : state_type;  -- For pause/resume

    -- Timers and counters
    signal phase_timer : integer range 0 to 65535;
    signal cycle_counter : integer range 0 to 31;
    signal direction_timer : integer range 0 to 1023;

    -- Program parameters
    signal wash_cycles : integer range 0 to 15;
    signal rinse_cycles : integer range 0 to 7;
    signal target_temp : integer range 0 to 100;
    signal target_water_level : integer range 0 to 7;
    signal spin_rpm : integer range 0 to 1600;

    -- Timing constants (in clock cycles, scaled for simulation)
    constant FILL_TIME : integer := 100;
    constant WASH_TIME : integer := 200;
    constant DIRECTION_CHANGE : integer := 30;
    constant DRAIN_TIME : integer := 80;
    constant SPIN_TIME : integer := 300;

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

    function to_bv(val : integer; width : integer) return bit_vector is
        variable result : bit_vector(width-1 downto 0);
        variable temp : integer := val;
    begin
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

begin
    -- Set program parameters based on selection
    param_proc: process(program_select, temp_select, spin_select)
    begin
        -- Default values
        wash_cycles <= 4;
        rinse_cycles <= 2;
        target_water_level <= 5;

        -- Temperature based on selection
        case temp_select is
            when "00" => target_temp <= 20;   -- Cold
            when "01" => target_temp <= 30;
            when "10" => target_temp <= 40;
            when "11" => target_temp <= 60;
        end case;

        -- Spin speed
        case spin_select is
            when "00" => spin_rpm <= 400;
            when "01" => spin_rpm <= 800;
            when "10" => spin_rpm <= 1200;
            when "11" => spin_rpm <= 1400;
        end case;

        -- Program-specific overrides
        case program_select is
            when "000" =>  -- Cotton
                wash_cycles <= 6;
                rinse_cycles <= 3;
            when "001" =>  -- Synthetic
                wash_cycles <= 4;
                rinse_cycles <= 2;
            when "010" =>  -- Delicate
                wash_cycles <= 3;
                rinse_cycles <= 2;
                spin_rpm <= 400;
            when "011" =>  -- Wool
                wash_cycles <= 2;
                rinse_cycles <= 2;
                spin_rpm <= 400;
            when "100" =>  -- Quick wash
                wash_cycles <= 2;
                rinse_cycles <= 1;
            when "101" =>  -- Rinse only
                wash_cycles <= 0;
                rinse_cycles <= 2;
            when "110" =>  -- Spin only
                wash_cycles <= 0;
                rinse_cycles <= 0;
            when "111" =>  -- Heavy duty
                wash_cycles <= 8;
                rinse_cycles <= 4;
        end case;
    end process;

    -- Main FSM
    fsm_proc: process(clk, reset)
        variable current_temp : integer;
        variable current_level : integer;
    begin
        if reset = '1' then
            state <= IDLE;
            return_state <= IDLE;
            phase_timer <= 0;
            cycle_counter <= 0;
            direction_timer <= 0;
        elsif clk'event and clk = '1' then
            current_temp := to_integer(water_temp);
            current_level := to_integer(water_level);

            case state is
                when IDLE =>
                    phase_timer <= 0;
                    cycle_counter <= 0;
                    if start_button = '1' and door_closed = '1' then
                        state <= LOCK_DOOR;
                    end if;

                when LOCK_DOOR =>
                    phase_timer <= phase_timer + 1;
                    if phase_timer >= 10 then
                        if wash_cycles > 0 then
                            state <= FILL_DETERGENT;
                        elsif rinse_cycles > 0 then
                            state <= FILL_RINSE;
                        else
                            state <= SPIN;
                        end if;
                        phase_timer <= 0;
                    end if;

                when FILL_DETERGENT =>
                    phase_timer <= phase_timer + 1;
                    if phase_timer >= 20 then
                        state <= FILL_WATER;
                        phase_timer <= 0;
                    end if;

                when FILL_WATER =>
                    if current_level >= target_water_level then
                        if target_temp > 25 then
                            state <= HEAT_WATER;
                        else
                            state <= WASH_CW;
                            cycle_counter <= 0;
                        end if;
                        phase_timer <= 0;
                    else
                        phase_timer <= phase_timer + 1;
                        if phase_timer >= FILL_TIME * 10 then
                            -- Timeout error
                            state <= ERROR_STATE;
                        end if;
                    end if;

                when HEAT_WATER =>
                    if current_temp >= target_temp then
                        state <= WASH_CW;
                        cycle_counter <= 0;
                        phase_timer <= 0;
                    else
                        phase_timer <= phase_timer + 1;
                        if phase_timer >= FILL_TIME * 50 then
                            state <= ERROR_STATE;  -- Heater timeout
                        end if;
                    end if;

                when WASH_CW =>
                    direction_timer <= direction_timer + 1;
                    if direction_timer >= DIRECTION_CHANGE then
                        state <= WASH_CCW;
                        direction_timer <= 0;
                    end if;
                    phase_timer <= phase_timer + 1;
                    if phase_timer >= WASH_TIME then
                        cycle_counter <= cycle_counter + 1;
                        if cycle_counter >= wash_cycles then
                            state <= DRAIN_WASH;
                            phase_timer <= 0;
                        end if;
                    end if;

                when WASH_CCW =>
                    direction_timer <= direction_timer + 1;
                    if direction_timer >= DIRECTION_CHANGE then
                        state <= WASH_CW;
                        direction_timer <= 0;
                    end if;
                    phase_timer <= phase_timer + 1;
                    if phase_timer >= WASH_TIME then
                        cycle_counter <= cycle_counter + 1;
                        if cycle_counter >= wash_cycles then
                            state <= DRAIN_WASH;
                            phase_timer <= 0;
                        end if;
                    end if;

                when DRAIN_WASH =>
                    phase_timer <= phase_timer + 1;
                    if current_level = 0 or phase_timer >= DRAIN_TIME then
                        if rinse_cycles > 0 then
                            state <= FILL_RINSE;
                            cycle_counter <= 0;
                        else
                            state <= SPIN;
                        end if;
                        phase_timer <= 0;
                    end if;

                when FILL_RINSE =>
                    if current_level >= target_water_level then
                        state <= RINSE_CW;
                        phase_timer <= 0;
                    end if;

                when RINSE_CW =>
                    direction_timer <= direction_timer + 1;
                    if direction_timer >= DIRECTION_CHANGE then
                        state <= RINSE_CCW;
                        direction_timer <= 0;
                    end if;
                    phase_timer <= phase_timer + 1;
                    if phase_timer >= WASH_TIME / 2 then
                        state <= DRAIN_RINSE;
                        phase_timer <= 0;
                    end if;

                when RINSE_CCW =>
                    direction_timer <= direction_timer + 1;
                    if direction_timer >= DIRECTION_CHANGE then
                        state <= RINSE_CW;
                        direction_timer <= 0;
                    end if;
                    phase_timer <= phase_timer + 1;
                    if phase_timer >= WASH_TIME / 2 then
                        state <= DRAIN_RINSE;
                        phase_timer <= 0;
                    end if;

                when DRAIN_RINSE =>
                    phase_timer <= phase_timer + 1;
                    if current_level = 0 or phase_timer >= DRAIN_TIME then
                        cycle_counter <= cycle_counter + 1;
                        if cycle_counter >= rinse_cycles - 1 then
                            state <= ADD_SOFTENER;
                        else
                            state <= FILL_RINSE;
                        end if;
                        phase_timer <= 0;
                    end if;

                when ADD_SOFTENER =>
                    phase_timer <= phase_timer + 1;
                    if phase_timer >= 20 then
                        state <= FINAL_RINSE;
                        phase_timer <= 0;
                    end if;

                when FINAL_RINSE =>
                    if current_level >= target_water_level then
                        -- Brief final rinse
                        phase_timer <= phase_timer + 1;
                        if phase_timer >= WASH_TIME / 4 then
                            state <= DRAIN_FINAL;
                            phase_timer <= 0;
                        end if;
                    end if;

                when DRAIN_FINAL =>
                    phase_timer <= phase_timer + 1;
                    if current_level = 0 or phase_timer >= DRAIN_TIME then
                        state <= SPIN;
                        phase_timer <= 0;
                    end if;

                when SPIN =>
                    if load_imbalance = '1' then
                        state <= REDISTRIBUTE;
                        phase_timer <= 0;
                    else
                        phase_timer <= phase_timer + 1;
                        if phase_timer >= SPIN_TIME then
                            state <= FINISH;
                            phase_timer <= 0;
                        end if;
                    end if;

                when REDISTRIBUTE =>
                    -- Slow tumble to redistribute load
                    phase_timer <= phase_timer + 1;
                    if phase_timer >= 50 then
                        state <= SPIN;
                        phase_timer <= 0;
                    end if;

                when FINISH =>
                    phase_timer <= phase_timer + 1;
                    if phase_timer >= 30 then
                        state <= IDLE;
                    end if;

                when PAUSED =>
                    if start_button = '1' then
                        state <= return_state;
                    end if;

                when ERROR_STATE =>
                    if reset = '1' then
                        state <= IDLE;
                    end if;

            end case;

            -- Pause handling
            if pause_button = '1' and state /= IDLE and state /= FINISH and
               state /= ERROR_STATE and state /= PAUSED then
                return_state <= state;
                state <= PAUSED;
            end if;
        end if;
    end process;

    -- Output logic
    door_lock <= '0' when state = IDLE or state = FINISH else '1';

    water_inlet <= '1' when state = FILL_WATER or state = FILL_RINSE or
                           state = FINAL_RINSE else '0';

    water_drain <= '1' when state = DRAIN_WASH or state = DRAIN_RINSE or
                           state = DRAIN_FINAL or state = SPIN else '0';

    heater <= '1' when state = HEAT_WATER else '0';

    motor_enable <= '1' when state = WASH_CW or state = WASH_CCW or
                            state = RINSE_CW or state = RINSE_CCW or
                            state = SPIN or state = REDISTRIBUTE else '0';

    motor_direction <= '1' when state = WASH_CCW or state = RINSE_CCW else '0';

    motor_speed <= to_bv(50, 11) when state = WASH_CW or state = WASH_CCW or
                                      state = RINSE_CW or state = RINSE_CCW else
                   to_bv(spin_rpm, 11) when state = SPIN else
                   to_bv(20, 11) when state = REDISTRIBUTE else
                   (others => '0');

    detergent_valve <= '1' when state = FILL_DETERGENT else '0';
    softener_valve <= '1' when state = ADD_SOFTENER else '0';

    -- Phase display
    with state select current_phase <=
        "0000" when IDLE,
        "0001" when LOCK_DOOR | FILL_DETERGENT | FILL_WATER,
        "0010" when HEAT_WATER,
        "0011" when WASH_CW | WASH_CCW,
        "0100" when DRAIN_WASH,
        "0101" when FILL_RINSE | RINSE_CW | RINSE_CCW,
        "0110" when DRAIN_RINSE | ADD_SOFTENER | FINAL_RINSE | DRAIN_FINAL,
        "0111" when SPIN | REDISTRIBUTE,
        "1000" when FINISH,
        "1001" when PAUSED,
        "1111" when ERROR_STATE;

    time_remaining <= to_bv(phase_timer / 10, 8);
    error_code <= "0001" when state = ERROR_STATE else "0000";

    running <= '0' when state = IDLE or state = FINISH or state = PAUSED else '1';
    finished <= '1' when state = FINISH else '0';

end Behavioral;

-- ============================================================================
-- Testbench
-- ============================================================================
entity WashingMachine_TB is
end WashingMachine_TB;

architecture Test of WashingMachine_TB is
    signal tb_clk, tb_reset : bit;
    signal tb_start, tb_pause : bit;
    signal tb_program : bit_vector(2 downto 0);
    signal tb_temp, tb_spin : bit_vector(1 downto 0);
    signal tb_door_closed : bit;
    signal tb_water_level : bit_vector(2 downto 0);
    signal tb_water_temp : bit_vector(7 downto 0);
    signal tb_drum_speed : bit_vector(10 downto 0);
    signal tb_imbalance : bit;
    signal tb_door_lock, tb_water_inlet, tb_water_drain : bit;
    signal tb_heater, tb_motor_en, tb_motor_dir : bit;
    signal tb_motor_speed : bit_vector(10 downto 0);
    signal tb_detergent, tb_softener : bit;
    signal tb_phase : bit_vector(3 downto 0);
    signal tb_time_rem : bit_vector(7 downto 0);
    signal tb_error : bit_vector(3 downto 0);
    signal tb_running, tb_finished : bit;
begin
    UUT: entity work.WashingMachine
        generic map (CLK_FREQ => 100)
        port map (
            clk             => tb_clk,
            reset           => tb_reset,
            start_button    => tb_start,
            pause_button    => tb_pause,
            program_select  => tb_program,
            temp_select     => tb_temp,
            spin_select     => tb_spin,
            door_closed     => tb_door_closed,
            water_level     => tb_water_level,
            water_temp      => tb_water_temp,
            drum_speed      => tb_drum_speed,
            load_imbalance  => tb_imbalance,
            door_lock       => tb_door_lock,
            water_inlet     => tb_water_inlet,
            water_drain     => tb_water_drain,
            heater          => tb_heater,
            motor_enable    => tb_motor_en,
            motor_direction => tb_motor_dir,
            motor_speed     => tb_motor_speed,
            detergent_valve => tb_detergent,
            softener_valve  => tb_softener,
            current_phase   => tb_phase,
            time_remaining  => tb_time_rem,
            error_code      => tb_error,
            running         => tb_running,
            finished        => tb_finished
        );

    clk_gen: process
    begin
        tb_clk <= '0'; wait for 5 ns;
        tb_clk <= '1'; wait for 5 ns;
    end process;

    -- Simulate water filling
    water_sim: process
    begin
        tb_water_level <= "000";
        wait until tb_water_inlet = '1';
        wait for 50 ns;
        tb_water_level <= "011";
        wait for 50 ns;
        tb_water_level <= "101";
        wait;
    end process;

    stimulus: process
    begin
        tb_reset <= '1';
        tb_start <= '0';
        tb_pause <= '0';
        tb_program <= "100";  -- Quick wash
        tb_temp <= "00";      -- Cold
        tb_spin <= "01";      -- 800 RPM
        tb_door_closed <= '1';
        tb_water_temp <= "00010100";  -- 20C
        tb_drum_speed <= (others => '0');
        tb_imbalance <= '0';
        wait for 30 ns;

        tb_reset <= '0';

        -- Start wash cycle
        tb_start <= '1';
        wait for 20 ns;
        tb_start <= '0';

        -- Let it run
        wait for 5000 ns;

        wait;
    end process;
end Test;
