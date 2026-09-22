-- ============================================================================
-- VHDL Project: ElevatorSystem
-- Category: State Machines
-- Complexity: High
-- Description: Multi-floor elevator controller with call handling
--              Includes door control, floor sensors, direction logic
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity ElevatorSystem is
    generic (
        NUM_FLOORS : integer := 8;
        DOOR_TIME  : integer := 50;   -- Clock cycles for door operation
        MOVE_TIME  : integer := 100   -- Clock cycles between floors
    );
    port (
        clk             : in  bit;
        reset           : in  bit;
        -- Floor call buttons (external)
        call_up         : in  bit_vector(NUM_FLOORS-2 downto 0);  -- No up on top floor
        call_down       : in  bit_vector(NUM_FLOORS-1 downto 1);  -- No down on ground
        -- Cabin buttons (internal)
        floor_request   : in  bit_vector(NUM_FLOORS-1 downto 0);
        -- Emergency
        emergency_stop  : in  bit;
        door_obstruction: in  bit;
        -- Floor sensors
        floor_sensor    : in  bit_vector(NUM_FLOORS-1 downto 0);
        -- Motor control
        motor_up        : out bit;
        motor_down      : out bit;
        motor_brake     : out bit;
        -- Door control
        door_open       : out bit;
        door_close      : out bit;
        door_locked     : out bit;
        -- Indicators
        current_floor   : out bit_vector(3 downto 0);
        direction_up    : out bit;
        direction_down  : out bit;
        -- Status
        state_out       : out bit_vector(3 downto 0)
    );
end ElevatorSystem;

architecture Behavioral of ElevatorSystem is
    -- States
    type state_type is (
        IDLE,
        DOOR_OPENING,
        DOOR_OPEN,
        DOOR_CLOSING,
        MOVING_UP,
        MOVING_DOWN,
        STOPPING,
        EMERGENCY,
        MAINTENANCE
    );

    signal state : state_type;
    signal timer : integer range 0 to 1023;

    -- Position tracking
    signal position : integer range 0 to NUM_FLOORS-1;
    signal target_floor : integer range 0 to NUM_FLOORS-1;
    signal direction : bit;  -- '1' = up, '0' = down

    -- Call/request registers (latched)
    signal up_calls : bit_vector(NUM_FLOORS-2 downto 0);
    signal down_calls : bit_vector(NUM_FLOORS-1 downto 1);
    signal floor_reqs : bit_vector(NUM_FLOORS-1 downto 0);

    -- Door state
    signal door_is_open : bit;

    function to_bv4(val : integer) return bit_vector is
        variable result : bit_vector(3 downto 0);
        variable temp : integer := val;
    begin
        for i in 0 to 3 loop
            if (temp mod 2) = 1 then
                result(i) := '1';
            else
                result(i) := '0';
            end if;
            temp := temp / 2;
        end loop;
        return result;
    end function;

    function any_call_above(pos : integer; up_c, down_c, floor_r : bit_vector)
        return bit is
    begin
        for i in pos+1 to NUM_FLOORS-1 loop
            if floor_r(i) = '1' then return '1'; end if;
            if i < NUM_FLOORS-1 and up_c(i) = '1' then return '1'; end if;
            if i > 0 and down_c(i) = '1' then return '1'; end if;
        end loop;
        return '0';
    end function;

    function any_call_below(pos : integer; up_c, down_c, floor_r : bit_vector)
        return bit is
    begin
        for i in 0 to pos-1 loop
            if floor_r(i) = '1' then return '1'; end if;
            if i < NUM_FLOORS-1 and up_c(i) = '1' then return '1'; end if;
            if i > 0 and down_c(i) = '1' then return '1'; end if;
        end loop;
        return '0';
    end function;

    function should_stop_here(pos : integer; dir : bit;
                              up_c, down_c, floor_r : bit_vector)
        return bit is
    begin
        -- Always stop for floor requests
        if floor_r(pos) = '1' then
            return '1';
        end if;

        -- Stop for calls in same direction
        if dir = '1' then  -- Going up
            if pos < NUM_FLOORS-1 and up_c(pos) = '1' then
                return '1';
            end if;
            -- Stop for down call at top if no more up calls
            if pos > 0 and down_c(pos) = '1' then
                if any_call_above(pos, up_c, down_c, floor_r) = '0' then
                    return '1';
                end if;
            end if;
        else  -- Going down
            if pos > 0 and down_c(pos) = '1' then
                return '1';
            end if;
            -- Stop for up call at bottom if no more down calls
            if pos < NUM_FLOORS-1 and up_c(pos) = '1' then
                if any_call_below(pos, up_c, down_c, floor_r) = '0' then
                    return '1';
                end if;
            end if;
        end if;

        return '0';
    end function;

begin
    -- Latch call requests
    call_latch: process(clk, reset)
    begin
        if reset = '1' then
            up_calls <= (others => '0');
            down_calls <= (others => '0');
            floor_reqs <= (others => '0');
        elsif clk'event and clk = '1' then
            -- Latch new calls
            for i in 0 to NUM_FLOORS-2 loop
                if call_up(i) = '1' then
                    up_calls(i) <= '1';
                end if;
            end loop;

            for i in 1 to NUM_FLOORS-1 loop
                if call_down(i) = '1' then
                    down_calls(i) <= '1';
                end if;
            end loop;

            for i in 0 to NUM_FLOORS-1 loop
                if floor_request(i) = '1' then
                    floor_reqs(i) <= '1';
                end if;
            end loop;

            -- Clear serviced calls when door opens
            if state = DOOR_OPENING and timer = 1 then
                floor_reqs(position) <= '0';
                if position < NUM_FLOORS-1 then
                    up_calls(position) <= '0';
                end if;
                if position > 0 then
                    down_calls(position) <= '0';
                end if;
            end if;
        end if;
    end process;

    -- Main FSM
    fsm_proc: process(clk, reset)
        variable next_target : integer;
        variable should_stop : bit;
    begin
        if reset = '1' then
            state <= IDLE;
            timer <= 0;
            position <= 0;
            target_floor <= 0;
            direction <= '1';
            door_is_open <= '0';
        elsif clk'event and clk = '1' then
            -- Emergency override
            if emergency_stop = '1' then
                state <= EMERGENCY;
            else
                case state is
                    when IDLE =>
                        door_is_open <= '0';
                        -- Check for calls
                        if any_call_above(position, up_calls, down_calls, floor_reqs) = '1' then
                            direction <= '1';
                            state <= MOVING_UP;
                            timer <= 0;
                        elsif any_call_below(position, up_calls, down_calls, floor_reqs) = '1' then
                            direction <= '0';
                            state <= MOVING_DOWN;
                            timer <= 0;
                        elsif floor_reqs(position) = '1' or
                              (position < NUM_FLOORS-1 and up_calls(position) = '1') or
                              (position > 0 and down_calls(position) = '1') then
                            state <= DOOR_OPENING;
                            timer <= 0;
                        end if;

                    when DOOR_OPENING =>
                        timer <= timer + 1;
                        if timer >= DOOR_TIME then
                            state <= DOOR_OPEN;
                            timer <= 0;
                            door_is_open <= '1';
                        end if;

                    when DOOR_OPEN =>
                        timer <= timer + 1;
                        if timer >= DOOR_TIME * 2 then
                            state <= DOOR_CLOSING;
                            timer <= 0;
                        end if;

                    when DOOR_CLOSING =>
                        if door_obstruction = '1' then
                            state <= DOOR_OPENING;
                            timer <= 0;
                        else
                            timer <= timer + 1;
                            if timer >= DOOR_TIME then
                                door_is_open <= '0';
                                state <= IDLE;
                                timer <= 0;
                            end if;
                        end if;

                    when MOVING_UP =>
                        timer <= timer + 1;
                        if timer >= MOVE_TIME then
                            if position < NUM_FLOORS-1 then
                                position <= position + 1;
                            end if;
                            timer <= 0;

                            -- Check if should stop
                            should_stop := should_stop_here(position + 1, direction,
                                                           up_calls, down_calls, floor_reqs);
                            if should_stop = '1' or position >= NUM_FLOORS-2 then
                                state <= STOPPING;
                            end if;
                        end if;

                    when MOVING_DOWN =>
                        timer <= timer + 1;
                        if timer >= MOVE_TIME then
                            if position > 0 then
                                position <= position - 1;
                            end if;
                            timer <= 0;

                            should_stop := should_stop_here(position - 1, direction,
                                                           up_calls, down_calls, floor_reqs);
                            if should_stop = '1' or position <= 1 then
                                state <= STOPPING;
                            end if;
                        end if;

                    when STOPPING =>
                        timer <= timer + 1;
                        if timer >= DOOR_TIME / 2 then
                            state <= DOOR_OPENING;
                            timer <= 0;
                        end if;

                    when EMERGENCY =>
                        -- Stop everything
                        door_is_open <= '0';
                        if emergency_stop = '0' then
                            state <= IDLE;
                        end if;

                    when MAINTENANCE =>
                        -- Manual control mode
                        null;

                end case;
            end if;
        end if;
    end process;

    -- Output logic
    motor_up <= '1' when state = MOVING_UP else '0';
    motor_down <= '1' when state = MOVING_DOWN else '0';
    motor_brake <= '1' when state = STOPPING or state = EMERGENCY else '0';

    door_open <= '1' when state = DOOR_OPENING or state = DOOR_OPEN else '0';
    door_close <= '1' when state = DOOR_CLOSING else '0';
    door_locked <= '1' when state = MOVING_UP or state = MOVING_DOWN or
                           state = STOPPING else '0';

    current_floor <= to_bv4(position);
    direction_up <= direction;
    direction_down <= not direction;

    with state select state_out <=
        "0000" when IDLE,
        "0001" when DOOR_OPENING,
        "0010" when DOOR_OPEN,
        "0011" when DOOR_CLOSING,
        "0100" when MOVING_UP,
        "0101" when MOVING_DOWN,
        "0110" when STOPPING,
        "1110" when EMERGENCY,
        "1111" when MAINTENANCE;

end Behavioral;

-- ============================================================================
-- Testbench
-- ============================================================================
entity ElevatorSystem_TB is
end ElevatorSystem_TB;

architecture Test of ElevatorSystem_TB is
    constant NUM_FLOORS : integer := 8;
    signal tb_clk, tb_reset : bit;
    signal tb_call_up : bit_vector(NUM_FLOORS-2 downto 0);
    signal tb_call_down : bit_vector(NUM_FLOORS-1 downto 1);
    signal tb_floor_req : bit_vector(NUM_FLOORS-1 downto 0);
    signal tb_emergency, tb_obstruction : bit;
    signal tb_floor_sensor : bit_vector(NUM_FLOORS-1 downto 0);
    signal tb_motor_up, tb_motor_down, tb_motor_brake : bit;
    signal tb_door_open, tb_door_close, tb_door_locked : bit;
    signal tb_current_floor : bit_vector(3 downto 0);
    signal tb_dir_up, tb_dir_down : bit;
    signal tb_state : bit_vector(3 downto 0);
begin
    UUT: entity work.ElevatorSystem
        generic map (NUM_FLOORS => NUM_FLOORS, DOOR_TIME => 5, MOVE_TIME => 10)
        port map (
            clk              => tb_clk,
            reset            => tb_reset,
            call_up          => tb_call_up,
            call_down        => tb_call_down,
            floor_request    => tb_floor_req,
            emergency_stop   => tb_emergency,
            door_obstruction => tb_obstruction,
            floor_sensor     => tb_floor_sensor,
            motor_up         => tb_motor_up,
            motor_down       => tb_motor_down,
            motor_brake      => tb_motor_brake,
            door_open        => tb_door_open,
            door_close       => tb_door_close,
            door_locked      => tb_door_locked,
            current_floor    => tb_current_floor,
            direction_up     => tb_dir_up,
            direction_down   => tb_dir_down,
            state_out        => tb_state
        );

    clk_gen: process
    begin
        tb_clk <= '0'; wait for 5 ns;
        tb_clk <= '1'; wait for 5 ns;
    end process;

    stimulus: process
    begin
        tb_reset <= '1';
        tb_call_up <= (others => '0');
        tb_call_down <= (others => '0');
        tb_floor_req <= (others => '0');
        tb_emergency <= '0';
        tb_obstruction <= '0';
        tb_floor_sensor <= "00000001";
        wait for 50 ns;

        tb_reset <= '0';

        -- Call from floor 3 (up)
        tb_call_up(3) <= '1';
        wait for 20 ns;
        tb_call_up(3) <= '0';

        -- Wait for elevator to reach floor 3
        wait for 500 ns;

        -- Request floor 5 from inside
        tb_floor_req(5) <= '1';
        wait for 20 ns;
        tb_floor_req(5) <= '0';

        wait for 500 ns;

        -- Call from floor 1 (down)
        tb_call_down(1) <= '1';
        wait for 20 ns;
        tb_call_down(1) <= '0';

        wait for 500 ns;
        wait;
    end process;
end Test;
