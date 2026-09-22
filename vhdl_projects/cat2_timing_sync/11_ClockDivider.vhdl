-- ============================================================================
-- VHDL Project: ClockDivider
-- Category: Timing & Synchronization
-- Complexity: Low
-- Description: Programmable clock divider with fractional division support
--              Integer division, 50% duty cycle, and phase control
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity ClockDivider is
    generic (
        MAX_DIVISOR : integer := 65536  -- Maximum division ratio
    );
    port (
        clk_in      : in  bit;
        reset       : in  bit;
        enable      : in  bit;
        -- Division ratio (output frequency = input / divisor)
        divisor     : in  bit_vector(15 downto 0);
        -- Outputs
        clk_out     : out bit;       -- Divided clock
        clk_out_n   : out bit;       -- Inverted divided clock
        tick        : out bit;       -- Single-cycle pulse at divided rate
        phase_0     : out bit;       -- 0 degree phase
        phase_90    : out bit;       -- 90 degree phase
        phase_180   : out bit;       -- 180 degree phase
        phase_270   : out bit        -- 270 degree phase
    );
end ClockDivider;

architecture Behavioral of ClockDivider is
    -- Convert bit_vector to integer
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

    signal counter : integer range 0 to MAX_DIVISOR-1;
    signal div_value : integer range 1 to MAX_DIVISOR;
    signal half_period : integer range 1 to MAX_DIVISOR/2;
    signal quarter_period : integer range 1 to MAX_DIVISOR/4;
    signal clk_internal : bit;
    signal tick_internal : bit;

begin
    -- Convert divisor to integer (minimum 1)
    div_value <= to_integer(divisor) when to_integer(divisor) > 0 else 1;
    half_period <= div_value / 2 when div_value > 1 else 1;
    quarter_period <= div_value / 4 when div_value > 3 else 1;

    -- Main counter process
    counter_proc: process(clk_in, reset)
    begin
        if reset = '1' then
            counter <= 0;
            clk_internal <= '0';
            tick_internal <= '0';
        elsif clk_in'event and clk_in = '1' then
            tick_internal <= '0';

            if enable = '1' then
                if counter >= div_value - 1 then
                    counter <= 0;
                    tick_internal <= '1';
                else
                    counter <= counter + 1;
                end if;

                -- Generate 50% duty cycle clock
                if counter < half_period then
                    clk_internal <= '1';
                else
                    clk_internal <= '0';
                end if;
            end if;
        end if;
    end process counter_proc;

    -- Phase outputs
    phase_proc: process(counter, div_value, quarter_period, half_period)
    begin
        -- Phase 0: 0 to 50%
        if counter < half_period then
            phase_0 <= '1';
        else
            phase_0 <= '0';
        end if;

        -- Phase 90: 25% to 75%
        if counter >= quarter_period and counter < (half_period + quarter_period) then
            phase_90 <= '1';
        else
            phase_90 <= '0';
        end if;

        -- Phase 180: 50% to 100%
        if counter >= half_period then
            phase_180 <= '1';
        else
            phase_180 <= '0';
        end if;

        -- Phase 270: 75% to 25% (wrapping)
        if counter >= (half_period + quarter_period) or counter < quarter_period then
            phase_270 <= '1';
        else
            phase_270 <= '0';
        end if;
    end process phase_proc;

    -- Output assignments
    clk_out <= clk_internal;
    clk_out_n <= not clk_internal;
    tick <= tick_internal;

end Behavioral;

-- ============================================================================
-- Fractional Clock Divider (using accumulator)
-- ============================================================================
entity FractionalDivider is
    generic (
        ACC_WIDTH : integer := 24  -- Accumulator width for precision
    );
    port (
        clk_in      : in  bit;
        reset       : in  bit;
        enable      : in  bit;
        -- Fractional divisor (fixed point: integer.fraction)
        divisor_int : in  bit_vector(15 downto 0);  -- Integer part
        divisor_frac: in  bit_vector(7 downto 0);   -- Fractional part (1/256)
        -- Output
        clk_out     : out bit;
        tick        : out bit
    );
end FractionalDivider;

architecture Behavioral of FractionalDivider is
    signal accumulator : bit_vector(ACC_WIDTH-1 downto 0);
    signal increment : bit_vector(ACC_WIDTH-1 downto 0);
    signal overflow : bit;
    signal clk_reg : bit;

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

    function add_vectors(a, b : bit_vector) return bit_vector is
        variable result : bit_vector(a'range);
        variable carry : bit := '0';
    begin
        for i in a'low to a'high loop
            result(i) := a(i) xor b(i) xor carry;
            carry := (a(i) and b(i)) or (a(i) and carry) or (b(i) and carry);
        end loop;
        return result;
    end function;

    function compare_ge(a, b : bit_vector) return bit is
    begin
        for i in a'high downto a'low loop
            if a(i) = '1' and b(i) = '0' then return '1'; end if;
            if a(i) = '0' and b(i) = '1' then return '0'; end if;
        end loop;
        return '1';  -- Equal
    end function;

begin
    -- Calculate increment from divisor
    -- increment = 2^ACC_WIDTH / divisor
    -- For simplicity, we use divisor directly (inverted logic)
    increment_calc: process(divisor_int, divisor_frac)
        variable div_full : integer;
    begin
        div_full := to_integer(divisor_int) * 256 + to_integer(divisor_frac);
        if div_full = 0 then div_full := 256; end if;

        -- Set increment (simplified calculation)
        for i in 0 to ACC_WIDTH-1 loop
            if i < 8 then
                increment(i) <= divisor_frac(i);
            elsif i < 24 then
                increment(i) <= divisor_int(i-8) when (i-8) < 16 else '0';
            else
                increment(i) <= '0';
            end if;
        end loop;
    end process increment_calc;

    -- Accumulator process
    acc_proc: process(clk_in, reset)
        variable sum : bit_vector(ACC_WIDTH-1 downto 0);
        variable half_max : bit_vector(ACC_WIDTH-1 downto 0);
    begin
        if reset = '1' then
            accumulator <= (others => '0');
            overflow <= '0';
            clk_reg <= '0';
        elsif clk_in'event and clk_in = '1' then
            overflow <= '0';
            if enable = '1' then
                sum := add_vectors(accumulator, increment);

                -- Check for overflow (wrap around)
                if compare_ge(sum, accumulator) = '0' then
                    overflow <= '1';
                    clk_reg <= not clk_reg;
                end if;

                accumulator <= sum;
            end if;
        end if;
    end process acc_proc;

    clk_out <= clk_reg;
    tick <= overflow;

end Behavioral;

-- ============================================================================
-- Duty Cycle Controller
-- ============================================================================
entity DutyCycleController is
    port (
        clk_in      : in  bit;
        reset       : in  bit;
        enable      : in  bit;
        -- Period and duty cycle
        period      : in  bit_vector(15 downto 0);
        duty_cycle  : in  bit_vector(15 downto 0);  -- High time
        -- Output
        pwm_out     : out bit
    );
end DutyCycleController;

architecture Behavioral of DutyCycleController is
    signal counter : integer range 0 to 65535;

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
    process(clk_in, reset)
        variable period_int, duty_int : integer;
    begin
        if reset = '1' then
            counter <= 0;
            pwm_out <= '0';
        elsif clk_in'event and clk_in = '1' then
            period_int := to_integer(period);
            duty_int := to_integer(duty_cycle);

            if period_int = 0 then period_int := 1; end if;

            if enable = '1' then
                if counter >= period_int - 1 then
                    counter <= 0;
                else
                    counter <= counter + 1;
                end if;

                if counter < duty_int then
                    pwm_out <= '1';
                else
                    pwm_out <= '0';
                end if;
            else
                pwm_out <= '0';
            end if;
        end if;
    end process;
end Behavioral;

-- ============================================================================
-- Clock Divider with Dynamic Update
-- ============================================================================
entity DynamicClockDivider is
    port (
        clk_in      : in  bit;
        reset       : in  bit;
        enable      : in  bit;
        divisor     : in  bit_vector(15 downto 0);
        update      : in  bit;  -- Pulse to update divisor
        clk_out     : out bit;
        tick        : out bit;
        -- Status
        busy        : out bit   -- High during divisor change
    );
end DynamicClockDivider;

architecture Behavioral of DynamicClockDivider is
    signal current_divisor : bit_vector(15 downto 0);
    signal next_divisor : bit_vector(15 downto 0);
    signal pending_update : bit;
    signal counter : integer range 0 to 65535;
    signal clk_reg : bit;

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
    process(clk_in, reset)
        variable div_int : integer;
    begin
        if reset = '1' then
            counter <= 0;
            clk_reg <= '0';
            current_divisor <= "0000000000000010";  -- Default: divide by 2
            next_divisor <= "0000000000000010";
            pending_update <= '0';
            tick <= '0';
            busy <= '0';
        elsif clk_in'event and clk_in = '1' then
            tick <= '0';
            busy <= pending_update;

            -- Capture new divisor request
            if update = '1' then
                next_divisor <= divisor;
                pending_update <= '1';
            end if;

            if enable = '1' then
                div_int := to_integer(current_divisor);
                if div_int = 0 then div_int := 1; end if;

                if counter >= div_int - 1 then
                    counter <= 0;
                    clk_reg <= not clk_reg;
                    tick <= clk_reg;  -- Tick on falling edge of output

                    -- Apply pending update at cycle boundary
                    if pending_update = '1' then
                        current_divisor <= next_divisor;
                        pending_update <= '0';
                    end if;
                else
                    counter <= counter + 1;
                end if;
            end if;
        end if;
    end process;

    clk_out <= clk_reg;
end Behavioral;

-- ============================================================================
-- Testbench
-- ============================================================================
entity ClockDivider_TB is
end ClockDivider_TB;

architecture Test of ClockDivider_TB is
    signal tb_clk_in, tb_reset, tb_enable : bit;
    signal tb_divisor : bit_vector(15 downto 0);
    signal tb_clk_out, tb_clk_out_n, tb_tick : bit;
    signal tb_phase_0, tb_phase_90, tb_phase_180, tb_phase_270 : bit;
begin
    UUT: entity work.ClockDivider
        generic map (MAX_DIVISOR => 1024)
        port map (
            clk_in    => tb_clk_in,
            reset     => tb_reset,
            enable    => tb_enable,
            divisor   => tb_divisor,
            clk_out   => tb_clk_out,
            clk_out_n => tb_clk_out_n,
            tick      => tb_tick,
            phase_0   => tb_phase_0,
            phase_90  => tb_phase_90,
            phase_180 => tb_phase_180,
            phase_270 => tb_phase_270
        );

    -- Clock generation (100 MHz)
    clk_gen: process
    begin
        tb_clk_in <= '0'; wait for 5 ns;
        tb_clk_in <= '1'; wait for 5 ns;
    end process;

    stimulus: process
    begin
        tb_reset <= '1';
        tb_enable <= '0';
        tb_divisor <= "0000000000001000";  -- Divide by 8
        wait for 50 ns;

        tb_reset <= '0';
        tb_enable <= '1';
        wait for 500 ns;

        -- Change divisor
        tb_divisor <= "0000000000000100";  -- Divide by 4
        wait for 300 ns;

        -- Divide by 2
        tb_divisor <= "0000000000000010";
        wait for 200 ns;

        wait;
    end process;
end Test;
