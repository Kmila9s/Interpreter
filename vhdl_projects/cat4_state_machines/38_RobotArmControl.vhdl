-- ============================================================================
-- VHDL Project: RobotArmControl
-- Category: State Machines
-- Complexity: High
-- Description: 3-axis robotic arm controller with servo control
--              Pick and place operations, trajectory planning
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity RobotArmControl is
    generic (
        SERVO_BITS   : integer := 12;  -- PWM resolution
        POSITION_BITS: integer := 16
    );
    port (
        clk             : in  bit;
        reset           : in  bit;
        -- Commands
        command         : in  bit_vector(3 downto 0);
        -- 0000: Idle
        -- 0001: Home
        -- 0010: Move to position
        -- 0011: Pick
        -- 0100: Place
        -- 0101: Pick and place sequence
        -- 0110: Emergency stop
        execute         : in  bit;
        -- Target position (for move commands)
        target_x        : in  bit_vector(POSITION_BITS-1 downto 0);
        target_y        : in  bit_vector(POSITION_BITS-1 downto 0);
        target_z        : in  bit_vector(POSITION_BITS-1 downto 0);
        -- Gripper
        gripper_cmd     : in  bit;  -- '1' = close, '0' = open
        -- Sensors
        limit_x_min     : in  bit;
        limit_x_max     : in  bit;
        limit_y_min     : in  bit;
        limit_y_max     : in  bit;
        limit_z_min     : in  bit;
        limit_z_max     : in  bit;
        gripper_sensor  : in  bit;  -- Object detected
        -- Servo outputs (PWM values)
        servo_x         : out bit_vector(SERVO_BITS-1 downto 0);
        servo_y         : out bit_vector(SERVO_BITS-1 downto 0);
        servo_z         : out bit_vector(SERVO_BITS-1 downto 0);
        servo_gripper   : out bit_vector(SERVO_BITS-1 downto 0);
        -- Status
        busy            : out bit;
        done            : out bit;
        error           : out bit;
        current_x       : out bit_vector(POSITION_BITS-1 downto 0);
        current_y       : out bit_vector(POSITION_BITS-1 downto 0);
        current_z       : out bit_vector(POSITION_BITS-1 downto 0);
        state_out       : out bit_vector(3 downto 0)
    );
end RobotArmControl;

architecture Behavioral of RobotArmControl is
    -- States
    type state_type is (
        IDLE,
        HOMING_X,
        HOMING_Y,
        HOMING_Z,
        MOVING_X,
        MOVING_Y,
        MOVING_Z,
        GRIPPER_OPENING,
        GRIPPER_CLOSING,
        PICKING,
        PLACING,
        SEQUENCE_PICK,
        SEQUENCE_MOVE,
        SEQUENCE_PLACE,
        COMPLETE,
        ERROR_STATE,
        EMERGENCY_STOP
    );

    signal state : state_type;
    signal next_seq_state : state_type;

    -- Current positions
    signal pos_x, pos_y, pos_z : bit_vector(POSITION_BITS-1 downto 0);
    signal dest_x, dest_y, dest_z : bit_vector(POSITION_BITS-1 downto 0);

    -- Gripper state
    signal gripper_closed : bit;
    signal gripper_pwm : bit_vector(SERVO_BITS-1 downto 0);

    -- Movement control
    signal move_timer : integer range 0 to 65535;
    signal step_counter : integer range 0 to 1023;

    -- Constants
    constant HOME_X : bit_vector(POSITION_BITS-1 downto 0) := (others => '0');
    constant HOME_Y : bit_vector(POSITION_BITS-1 downto 0) := (others => '0');
    constant HOME_Z : bit_vector(POSITION_BITS-1 downto 0) := (others => '0');

    constant GRIPPER_OPEN_PWM : bit_vector(SERVO_BITS-1 downto 0) := "000100000000";
    constant GRIPPER_CLOSE_PWM : bit_vector(SERVO_BITS-1 downto 0) := "001000000000";

    constant MOVE_SPEED : integer := 10;  -- Steps per clock
    constant GRIPPER_TIME : integer := 100;

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

    function move_toward(current, target : bit_vector; speed : integer)
        return bit_vector is
        variable curr_int, targ_int, new_int : integer;
    begin
        curr_int := to_integer(current);
        targ_int := to_integer(target);

        if curr_int < targ_int then
            if targ_int - curr_int > speed then
                new_int := curr_int + speed;
            else
                new_int := targ_int;
            end if;
        elsif curr_int > targ_int then
            if curr_int - targ_int > speed then
                new_int := curr_int - speed;
            else
                new_int := targ_int;
            end if;
        else
            new_int := curr_int;
        end if;

        return to_bv(new_int, current'length);
    end function;

    function position_reached(current, target : bit_vector) return bit is
    begin
        for i in current'range loop
            if current(i) /= target(i) then
                return '0';
            end if;
        end loop;
        return '1';
    end function;

    function position_to_servo(pos : bit_vector) return bit_vector is
        variable pos_int : integer;
        variable servo_val : integer;
    begin
        -- Map position to servo PWM value
        -- Assuming position 0-65535 maps to servo 1000-2000 (in 12-bit PWM)
        pos_int := to_integer(pos);
        servo_val := 1000 + (pos_int / 65);  -- Simplified mapping
        if servo_val > 2000 then servo_val := 2000; end if;
        return to_bv(servo_val, SERVO_BITS);
    end function;

begin
    -- Main FSM
    fsm_proc: process(clk, reset)
    begin
        if reset = '1' then
            state <= IDLE;
            pos_x <= (others => '0');
            pos_y <= (others => '0');
            pos_z <= (others => '0');
            dest_x <= (others => '0');
            dest_y <= (others => '0');
            dest_z <= (others => '0');
            gripper_closed <= '0';
            gripper_pwm <= GRIPPER_OPEN_PWM;
            move_timer <= 0;
            step_counter <= 0;
        elsif clk'event and clk = '1' then
            case state is
                when IDLE =>
                    move_timer <= 0;
                    if execute = '1' then
                        case command is
                            when "0001" =>  -- Home
                                dest_x <= HOME_X;
                                dest_y <= HOME_Y;
                                dest_z <= HOME_Z;
                                state <= HOMING_Z;

                            when "0010" =>  -- Move to position
                                dest_x <= target_x;
                                dest_y <= target_y;
                                dest_z <= target_z;
                                state <= MOVING_Z;  -- Z first (safety)

                            when "0011" =>  -- Pick
                                state <= GRIPPER_CLOSING;

                            when "0100" =>  -- Place
                                state <= GRIPPER_OPENING;

                            when "0101" =>  -- Pick and place sequence
                                dest_x <= target_x;
                                dest_y <= target_y;
                                dest_z <= target_z;
                                state <= SEQUENCE_PICK;

                            when "0110" =>  -- Emergency stop
                                state <= EMERGENCY_STOP;

                            when others =>
                                null;
                        end case;
                    end if;

                when HOMING_Z =>
                    -- Raise Z first (safety)
                    pos_z <= move_toward(pos_z, HOME_Z, MOVE_SPEED);
                    if position_reached(pos_z, HOME_Z) = '1' then
                        state <= HOMING_X;
                    end if;
                    -- Check limits
                    if limit_z_min = '1' then
                        pos_z <= (others => '0');
                        state <= HOMING_X;
                    end if;

                when HOMING_X =>
                    pos_x <= move_toward(pos_x, HOME_X, MOVE_SPEED);
                    if position_reached(pos_x, HOME_X) = '1' or limit_x_min = '1' then
                        pos_x <= (others => '0');
                        state <= HOMING_Y;
                    end if;

                when HOMING_Y =>
                    pos_y <= move_toward(pos_y, HOME_Y, MOVE_SPEED);
                    if position_reached(pos_y, HOME_Y) = '1' or limit_y_min = '1' then
                        pos_y <= (others => '0');
                        state <= COMPLETE;
                    end if;

                when MOVING_Z =>
                    -- Move Z first when going up, last when going down
                    if to_integer(dest_z) < to_integer(pos_z) then
                        -- Going up - move Z first
                        pos_z <= move_toward(pos_z, dest_z, MOVE_SPEED);
                        if position_reached(pos_z, dest_z) = '1' then
                            state <= MOVING_X;
                        end if;
                    else
                        -- Going down - move X,Y first
                        state <= MOVING_X;
                    end if;

                    -- Check limits
                    if limit_z_max = '1' and to_integer(dest_z) > to_integer(pos_z) then
                        state <= ERROR_STATE;
                    end if;
                    if limit_z_min = '1' and to_integer(dest_z) < to_integer(pos_z) then
                        state <= ERROR_STATE;
                    end if;

                when MOVING_X =>
                    pos_x <= move_toward(pos_x, dest_x, MOVE_SPEED);
                    if position_reached(pos_x, dest_x) = '1' then
                        state <= MOVING_Y;
                    end if;

                    if limit_x_max = '1' and to_integer(dest_x) > to_integer(pos_x) then
                        state <= ERROR_STATE;
                    end if;
                    if limit_x_min = '1' and to_integer(dest_x) < to_integer(pos_x) then
                        state <= ERROR_STATE;
                    end if;

                when MOVING_Y =>
                    pos_y <= move_toward(pos_y, dest_y, MOVE_SPEED);
                    if position_reached(pos_y, dest_y) = '1' then
                        -- If going down, now move Z
                        if to_integer(dest_z) > to_integer(pos_z) then
                            pos_z <= move_toward(pos_z, dest_z, MOVE_SPEED);
                            if position_reached(pos_z, dest_z) = '1' then
                                state <= COMPLETE;
                            end if;
                        else
                            state <= COMPLETE;
                        end if;
                    end if;

                    if limit_y_max = '1' and to_integer(dest_y) > to_integer(pos_y) then
                        state <= ERROR_STATE;
                    end if;
                    if limit_y_min = '1' and to_integer(dest_y) < to_integer(pos_y) then
                        state <= ERROR_STATE;
                    end if;

                when GRIPPER_OPENING =>
                    move_timer <= move_timer + 1;
                    gripper_pwm <= GRIPPER_OPEN_PWM;
                    if move_timer >= GRIPPER_TIME then
                        gripper_closed <= '0';
                        state <= COMPLETE;
                        move_timer <= 0;
                    end if;

                when GRIPPER_CLOSING =>
                    move_timer <= move_timer + 1;
                    gripper_pwm <= GRIPPER_CLOSE_PWM;
                    if move_timer >= GRIPPER_TIME then
                        gripper_closed <= '1';
                        state <= COMPLETE;
                        move_timer <= 0;
                    end if;

                when PICKING =>
                    -- Sequence: open gripper, lower Z, close gripper, raise Z
                    move_timer <= move_timer + 1;
                    -- Simplified - actual would be multi-step
                    if move_timer >= GRIPPER_TIME * 4 then
                        gripper_closed <= '1';
                        state <= COMPLETE;
                        move_timer <= 0;
                    end if;

                when PLACING =>
                    move_timer <= move_timer + 1;
                    if move_timer >= GRIPPER_TIME * 2 then
                        gripper_closed <= '0';
                        state <= COMPLETE;
                        move_timer <= 0;
                    end if;

                when SEQUENCE_PICK =>
                    -- Multi-step pick and place
                    state <= GRIPPER_OPENING;
                    next_seq_state <= SEQUENCE_MOVE;

                when SEQUENCE_MOVE =>
                    state <= MOVING_Z;
                    next_seq_state <= SEQUENCE_PLACE;

                when SEQUENCE_PLACE =>
                    state <= GRIPPER_CLOSING;
                    next_seq_state <= COMPLETE;

                when COMPLETE =>
                    move_timer <= move_timer + 1;
                    if move_timer >= 10 then
                        state <= IDLE;
                        move_timer <= 0;
                    end if;

                when ERROR_STATE =>
                    -- Stay in error until reset or explicit clear
                    if execute = '1' and command = "0000" then
                        state <= IDLE;
                    end if;

                when EMERGENCY_STOP =>
                    -- Immediate stop, wait for explicit resume
                    if execute = '1' and command = "0001" then
                        state <= HOMING_Z;  -- Re-home after emergency
                    end if;

            end case;
        end if;
    end process;

    -- Output assignments
    servo_x <= position_to_servo(pos_x);
    servo_y <= position_to_servo(pos_y);
    servo_z <= position_to_servo(pos_z);
    servo_gripper <= gripper_pwm;

    current_x <= pos_x;
    current_y <= pos_y;
    current_z <= pos_z;

    busy <= '0' when state = IDLE or state = COMPLETE else '1';
    done <= '1' when state = COMPLETE else '0';
    error <= '1' when state = ERROR_STATE else '0';

    with state select state_out <=
        "0000" when IDLE,
        "0001" when HOMING_X | HOMING_Y | HOMING_Z,
        "0010" when MOVING_X | MOVING_Y | MOVING_Z,
        "0011" when GRIPPER_OPENING,
        "0100" when GRIPPER_CLOSING,
        "0101" when PICKING,
        "0110" when PLACING,
        "0111" when SEQUENCE_PICK | SEQUENCE_MOVE | SEQUENCE_PLACE,
        "1000" when COMPLETE,
        "1110" when ERROR_STATE,
        "1111" when EMERGENCY_STOP;

end Behavioral;

-- ============================================================================
-- Testbench
-- ============================================================================
entity RobotArmControl_TB is
end RobotArmControl_TB;

architecture Test of RobotArmControl_TB is
    signal tb_clk, tb_reset : bit;
    signal tb_command : bit_vector(3 downto 0);
    signal tb_execute : bit;
    signal tb_target_x, tb_target_y, tb_target_z : bit_vector(15 downto 0);
    signal tb_gripper_cmd : bit;
    signal tb_limit_x_min, tb_limit_x_max : bit;
    signal tb_limit_y_min, tb_limit_y_max : bit;
    signal tb_limit_z_min, tb_limit_z_max : bit;
    signal tb_gripper_sensor : bit;
    signal tb_servo_x, tb_servo_y, tb_servo_z, tb_servo_grip : bit_vector(11 downto 0);
    signal tb_busy, tb_done, tb_error : bit;
    signal tb_curr_x, tb_curr_y, tb_curr_z : bit_vector(15 downto 0);
    signal tb_state : bit_vector(3 downto 0);
begin
    UUT: entity work.RobotArmControl
        port map (
            clk           => tb_clk,
            reset         => tb_reset,
            command       => tb_command,
            execute       => tb_execute,
            target_x      => tb_target_x,
            target_y      => tb_target_y,
            target_z      => tb_target_z,
            gripper_cmd   => tb_gripper_cmd,
            limit_x_min   => tb_limit_x_min,
            limit_x_max   => tb_limit_x_max,
            limit_y_min   => tb_limit_y_min,
            limit_y_max   => tb_limit_y_max,
            limit_z_min   => tb_limit_z_min,
            limit_z_max   => tb_limit_z_max,
            gripper_sensor => tb_gripper_sensor,
            servo_x       => tb_servo_x,
            servo_y       => tb_servo_y,
            servo_z       => tb_servo_z,
            servo_gripper => tb_servo_grip,
            busy          => tb_busy,
            done          => tb_done,
            error         => tb_error,
            current_x     => tb_curr_x,
            current_y     => tb_curr_y,
            current_z     => tb_curr_z,
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
        tb_command <= "0000";
        tb_execute <= '0';
        tb_target_x <= (others => '0');
        tb_target_y <= (others => '0');
        tb_target_z <= (others => '0');
        tb_gripper_cmd <= '0';
        tb_limit_x_min <= '0'; tb_limit_x_max <= '0';
        tb_limit_y_min <= '0'; tb_limit_y_max <= '0';
        tb_limit_z_min <= '0'; tb_limit_z_max <= '0';
        tb_gripper_sensor <= '0';
        wait for 30 ns;

        tb_reset <= '0';

        -- Home command
        tb_command <= "0001";
        tb_execute <= '1';
        wait for 10 ns;
        tb_execute <= '0';
        wait for 500 ns;

        -- Move to position
        tb_target_x <= "0000000100000000";  -- 1024
        tb_target_y <= "0000001000000000";  -- 2048
        tb_target_z <= "0000000010000000";  -- 512
        tb_command <= "0010";
        tb_execute <= '1';
        wait for 10 ns;
        tb_execute <= '0';
        wait for 2000 ns;

        -- Close gripper
        tb_command <= "0011";
        tb_execute <= '1';
        wait for 10 ns;
        tb_execute <= '0';
        wait for 500 ns;

        wait;
    end process;
end Test;
