-- ============================================================================
-- VHDL Project: PWMGenerator
-- Category: Timing & Synchronization
-- Complexity: Medium
-- Description: 8-channel PWM generator with phase offset capability
--              Supports variable resolution, complementary outputs, dead-time
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity PWMGenerator is
    generic (
        NUM_CHANNELS : integer := 8;
        RESOLUTION   : integer := 10  -- Bits of resolution (1024 levels)
    );
    port (
        clk             : in  bit;
        reset           : in  bit;
        enable          : in  bit;
        -- Global configuration
        period          : in  bit_vector(RESOLUTION-1 downto 0);
        -- Per-channel duty cycle
        duty_ch0        : in  bit_vector(RESOLUTION-1 downto 0);
        duty_ch1        : in  bit_vector(RESOLUTION-1 downto 0);
        duty_ch2        : in  bit_vector(RESOLUTION-1 downto 0);
        duty_ch3        : in  bit_vector(RESOLUTION-1 downto 0);
        duty_ch4        : in  bit_vector(RESOLUTION-1 downto 0);
        duty_ch5        : in  bit_vector(RESOLUTION-1 downto 0);
        duty_ch6        : in  bit_vector(RESOLUTION-1 downto 0);
        duty_ch7        : in  bit_vector(RESOLUTION-1 downto 0);
        -- Per-channel phase offset
        phase_ch0       : in  bit_vector(RESOLUTION-1 downto 0);
        phase_ch1       : in  bit_vector(RESOLUTION-1 downto 0);
        phase_ch2       : in  bit_vector(RESOLUTION-1 downto 0);
        phase_ch3       : in  bit_vector(RESOLUTION-1 downto 0);
        phase_ch4       : in  bit_vector(RESOLUTION-1 downto 0);
        phase_ch5       : in  bit_vector(RESOLUTION-1 downto 0);
        phase_ch6       : in  bit_vector(RESOLUTION-1 downto 0);
        phase_ch7       : in  bit_vector(RESOLUTION-1 downto 0);
        -- PWM outputs
        pwm_out         : out bit_vector(NUM_CHANNELS-1 downto 0);
        pwm_out_n       : out bit_vector(NUM_CHANNELS-1 downto 0);  -- Complementary
        -- Status
        cycle_start     : out bit;  -- Pulse at start of each period
        counter_value   : out bit_vector(RESOLUTION-1 downto 0)
    );
end PWMGenerator;

architecture Behavioral of PWMGenerator is
    constant MAX_COUNT : integer := 2**RESOLUTION - 1;

    -- Main counter
    signal counter : integer range 0 to MAX_COUNT;

    -- Duty cycle values
    type duty_array is array (0 to NUM_CHANNELS-1) of integer range 0 to MAX_COUNT;
    signal duty_values : duty_array;
    signal phase_values : duty_array;

    -- PWM output registers
    signal pwm_reg : bit_vector(NUM_CHANNELS-1 downto 0);

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

    function to_bitvector(val : integer; width : integer) return bit_vector is
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
    -- Convert inputs to integers
    duty_values(0) <= to_integer(duty_ch0);
    duty_values(1) <= to_integer(duty_ch1);
    duty_values(2) <= to_integer(duty_ch2);
    duty_values(3) <= to_integer(duty_ch3);
    duty_values(4) <= to_integer(duty_ch4);
    duty_values(5) <= to_integer(duty_ch5);
    duty_values(6) <= to_integer(duty_ch6);
    duty_values(7) <= to_integer(duty_ch7);

    phase_values(0) <= to_integer(phase_ch0);
    phase_values(1) <= to_integer(phase_ch1);
    phase_values(2) <= to_integer(phase_ch2);
    phase_values(3) <= to_integer(phase_ch3);
    phase_values(4) <= to_integer(phase_ch4);
    phase_values(5) <= to_integer(phase_ch5);
    phase_values(6) <= to_integer(phase_ch6);
    phase_values(7) <= to_integer(phase_ch7);

    -- Main counter
    counter_proc: process(clk, reset)
        variable period_val : integer;
    begin
        if reset = '1' then
            counter <= 0;
            cycle_start <= '0';
        elsif clk'event and clk = '1' then
            cycle_start <= '0';
            period_val := to_integer(period);
            if period_val = 0 then period_val := MAX_COUNT; end if;

            if enable = '1' then
                if counter >= period_val then
                    counter <= 0;
                    cycle_start <= '1';
                else
                    counter <= counter + 1;
                end if;
            end if;
        end if;
    end process counter_proc;

    counter_value <= to_bitvector(counter, RESOLUTION);

    -- PWM generation for each channel
    pwm_gen: for i in 0 to NUM_CHANNELS-1 generate
        pwm_proc: process(clk, reset)
            variable effective_counter : integer;
            variable period_val : integer;
        begin
            if reset = '1' then
                pwm_reg(i) <= '0';
            elsif clk'event and clk = '1' then
                period_val := to_integer(period);
                if period_val = 0 then period_val := MAX_COUNT; end if;

                -- Apply phase offset
                if counter >= phase_values(i) then
                    effective_counter := counter - phase_values(i);
                else
                    effective_counter := period_val - phase_values(i) + counter;
                end if;

                -- Compare with duty cycle
                if enable = '1' then
                    if effective_counter < duty_values(i) then
                        pwm_reg(i) <= '1';
                    else
                        pwm_reg(i) <= '0';
                    end if;
                else
                    pwm_reg(i) <= '0';
                end if;
            end if;
        end process pwm_proc;
    end generate pwm_gen;

    -- Outputs
    pwm_out <= pwm_reg;

    -- Complementary outputs
    comp_gen: for i in 0 to NUM_CHANNELS-1 generate
        pwm_out_n(i) <= not pwm_reg(i);
    end generate comp_gen;

end Behavioral;

-- ============================================================================
-- PWM with Dead-Time Insertion
-- ============================================================================
entity PWMDeadTime is
    generic (
        RESOLUTION : integer := 10
    );
    port (
        clk         : in  bit;
        reset       : in  bit;
        enable      : in  bit;
        -- Configuration
        period      : in  bit_vector(RESOLUTION-1 downto 0);
        duty        : in  bit_vector(RESOLUTION-1 downto 0);
        dead_time   : in  bit_vector(7 downto 0);  -- Dead time cycles
        -- Outputs
        pwm_high    : out bit;  -- High-side drive
        pwm_low     : out bit;  -- Low-side drive (complementary with dead time)
        pwm_raw     : out bit   -- Raw PWM without dead time
    );
end PWMDeadTime;

architecture Behavioral of PWMDeadTime is
    signal counter : integer range 0 to 1023;
    signal pwm_internal : bit;
    signal pwm_delayed_rise : bit;
    signal pwm_delayed_fall : bit;
    signal dead_counter_rise : integer range 0 to 255;
    signal dead_counter_fall : integer range 0 to 255;
    signal pwm_prev : bit;

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
    -- Basic PWM generation
    pwm_proc: process(clk, reset)
        variable period_val, duty_val : integer;
    begin
        if reset = '1' then
            counter <= 0;
            pwm_internal <= '0';
        elsif clk'event and clk = '1' then
            period_val := to_integer(period);
            duty_val := to_integer(duty);
            if period_val = 0 then period_val := 1; end if;

            if enable = '1' then
                if counter >= period_val - 1 then
                    counter <= 0;
                else
                    counter <= counter + 1;
                end if;

                if counter < duty_val then
                    pwm_internal <= '1';
                else
                    pwm_internal <= '0';
                end if;
            else
                pwm_internal <= '0';
            end if;
        end if;
    end process pwm_proc;

    pwm_raw <= pwm_internal;

    -- Dead time insertion
    dead_time_proc: process(clk, reset)
        variable dead_val : integer;
        variable rising_edge, falling_edge : bit;
    begin
        if reset = '1' then
            pwm_prev <= '0';
            pwm_delayed_rise <= '0';
            pwm_delayed_fall <= '0';
            dead_counter_rise <= 0;
            dead_counter_fall <= 0;
        elsif clk'event and clk = '1' then
            pwm_prev <= pwm_internal;
            dead_val := to_integer(dead_time);
            if dead_val = 0 then dead_val := 1; end if;

            rising_edge := pwm_internal and (not pwm_prev);
            falling_edge := (not pwm_internal) and pwm_prev;

            -- Delay rising edge (for high-side)
            if rising_edge = '1' then
                dead_counter_rise <= dead_val;
            elsif dead_counter_rise > 0 then
                dead_counter_rise <= dead_counter_rise - 1;
                if dead_counter_rise = 1 then
                    pwm_delayed_rise <= '1';
                end if;
            end if;

            if pwm_internal = '0' then
                pwm_delayed_rise <= '0';
            end if;

            -- Delay falling edge (for low-side)
            if falling_edge = '1' then
                dead_counter_fall <= dead_val;
            elsif dead_counter_fall > 0 then
                dead_counter_fall <= dead_counter_fall - 1;
                if dead_counter_fall = 1 then
                    pwm_delayed_fall <= '1';
                end if;
            end if;

            if pwm_internal = '1' then
                pwm_delayed_fall <= '0';
            end if;
        end if;
    end process dead_time_proc;

    -- High-side: delayed turn-on
    pwm_high <= pwm_internal when to_integer(dead_time) = 0 else
                pwm_delayed_rise when pwm_internal = '1' else '0';

    -- Low-side: delayed turn-on after high-side turns off
    pwm_low <= (not pwm_internal) when to_integer(dead_time) = 0 else
               pwm_delayed_fall when pwm_internal = '0' else '0';

end Behavioral;

-- ============================================================================
-- Center-Aligned PWM
-- ============================================================================
entity PWMCenterAligned is
    generic (
        RESOLUTION : integer := 10
    );
    port (
        clk         : in  bit;
        reset       : in  bit;
        enable      : in  bit;
        period      : in  bit_vector(RESOLUTION-1 downto 0);
        duty        : in  bit_vector(RESOLUTION-1 downto 0);
        pwm_out     : out bit;
        counter_dir : out bit  -- '1' = counting up, '0' = counting down
    );
end PWMCenterAligned;

architecture Behavioral of PWMCenterAligned is
    signal counter : integer range 0 to 1023;
    signal direction : bit;  -- '1' = up, '0' = down

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
    counter_proc: process(clk, reset)
        variable period_val, duty_val : integer;
    begin
        if reset = '1' then
            counter <= 0;
            direction <= '1';
            pwm_out <= '0';
        elsif clk'event and clk = '1' then
            period_val := to_integer(period);
            duty_val := to_integer(duty);
            if period_val = 0 then period_val := 1; end if;

            if enable = '1' then
                -- Up/down counter
                if direction = '1' then
                    -- Counting up
                    if counter >= period_val - 1 then
                        direction <= '0';
                    else
                        counter <= counter + 1;
                    end if;
                else
                    -- Counting down
                    if counter = 0 then
                        direction <= '1';
                    else
                        counter <= counter - 1;
                    end if;
                end if;

                -- PWM comparison (symmetric around center)
                if counter < duty_val then
                    pwm_out <= '1';
                else
                    pwm_out <= '0';
                end if;
            else
                pwm_out <= '0';
            end if;
        end if;
    end process counter_proc;

    counter_dir <= direction;

end Behavioral;

-- ============================================================================
-- Sigma-Delta Modulator (1-bit DAC)
-- ============================================================================
entity SigmaDeltaPWM is
    generic (
        INPUT_WIDTH : integer := 12
    );
    port (
        clk         : in  bit;
        reset       : in  bit;
        enable      : in  bit;
        data_in     : in  bit_vector(INPUT_WIDTH-1 downto 0);
        pwm_out     : out bit
    );
end SigmaDeltaPWM;

architecture Behavioral of SigmaDeltaPWM is
    signal accumulator : integer range 0 to 2**(INPUT_WIDTH+1)-1;
    signal feedback : integer;

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
    sd_proc: process(clk, reset)
        variable input_val : integer;
        variable sum : integer;
    begin
        if reset = '1' then
            accumulator <= 0;
            pwm_out <= '0';
        elsif clk'event and clk = '1' then
            if enable = '1' then
                input_val := to_integer(data_in);

                -- Add input to accumulator
                sum := accumulator + input_val;

                -- Check for overflow (output 1 and subtract full scale)
                if sum >= 2**INPUT_WIDTH then
                    pwm_out <= '1';
                    accumulator <= sum - 2**INPUT_WIDTH;
                else
                    pwm_out <= '0';
                    accumulator <= sum;
                end if;
            end if;
        end if;
    end process sd_proc;

end Behavioral;

-- ============================================================================
-- Testbench
-- ============================================================================
entity PWMGenerator_TB is
end PWMGenerator_TB;

architecture Test of PWMGenerator_TB is
    signal tb_clk, tb_reset, tb_enable : bit;
    signal tb_period : bit_vector(9 downto 0);
    signal tb_duty_ch0 : bit_vector(9 downto 0);
    signal tb_phase_ch0 : bit_vector(9 downto 0);
    signal tb_pwm_out : bit_vector(7 downto 0);
    signal tb_cycle_start : bit;
begin
    UUT: entity work.PWMGenerator
        generic map (NUM_CHANNELS => 8, RESOLUTION => 10)
        port map (
            clk           => tb_clk,
            reset         => tb_reset,
            enable        => tb_enable,
            period        => tb_period,
            duty_ch0      => tb_duty_ch0,
            duty_ch1      => "0000000000",
            duty_ch2      => "0000000000",
            duty_ch3      => "0000000000",
            duty_ch4      => "0000000000",
            duty_ch5      => "0000000000",
            duty_ch6      => "0000000000",
            duty_ch7      => "0000000000",
            phase_ch0     => tb_phase_ch0,
            phase_ch1     => "0000000000",
            phase_ch2     => "0000000000",
            phase_ch3     => "0000000000",
            phase_ch4     => "0000000000",
            phase_ch5     => "0000000000",
            phase_ch6     => "0000000000",
            phase_ch7     => "0000000000",
            pwm_out       => tb_pwm_out,
            pwm_out_n     => open,
            cycle_start   => tb_cycle_start,
            counter_value => open
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
        tb_period <= "0000100000";    -- Period = 32
        tb_duty_ch0 <= "0000010000";  -- 50% duty (16)
        tb_phase_ch0 <= "0000000000"; -- No phase offset
        wait for 30 ns;

        tb_reset <= '0';
        tb_enable <= '1';
        wait for 2000 ns;

        -- Change duty cycle
        tb_duty_ch0 <= "0000011000";  -- 75% duty (24)
        wait for 2000 ns;

        -- Add phase offset
        tb_phase_ch0 <= "0000001000";  -- Phase offset = 8
        wait for 2000 ns;

        wait;
    end process;
end Test;
