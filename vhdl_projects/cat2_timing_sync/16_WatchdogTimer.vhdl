-- ============================================================================
-- VHDL Project: WatchdogTimer
-- Category: Timing & Synchronization
-- Complexity: Medium
-- Description: Watchdog timer with window mode and early warning
--              Prevents system hangs by requiring periodic refresh
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity WatchdogTimer is
    generic (
        COUNTER_WIDTH : integer := 24  -- Up to 16M cycles
    );
    port (
        clk             : in  bit;
        reset           : in  bit;
        enable          : in  bit;
        -- Kick/refresh the watchdog
        kick            : in  bit;
        -- Configuration
        timeout_value   : in  bit_vector(COUNTER_WIDTH-1 downto 0);
        warning_value   : in  bit_vector(COUNTER_WIDTH-1 downto 0);  -- Early warning threshold
        window_enable   : in  bit;  -- Enable window mode
        window_open     : in  bit_vector(COUNTER_WIDTH-1 downto 0);  -- Window open time
        -- Outputs
        timeout         : out bit;  -- Watchdog timeout occurred
        warning         : out bit;  -- Early warning (counter approaching timeout)
        window_violation: out bit;  -- Kicked too early (window mode)
        counter_value   : out bit_vector(COUNTER_WIDTH-1 downto 0);
        -- Status
        running         : out bit
    );
end WatchdogTimer;

architecture Behavioral of WatchdogTimer is
    signal counter : bit_vector(COUNTER_WIDTH-1 downto 0);
    signal timeout_reg : bit;
    signal warning_reg : bit;
    signal violation_reg : bit;
    signal kick_prev : bit;
    signal kick_edge : bit;

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

    function compare_ge(a, b : bit_vector) return bit is
    begin
        for i in a'high downto a'low loop
            if a(i) = '1' and b(i) = '0' then return '1'; end if;
            if a(i) = '0' and b(i) = '1' then return '0'; end if;
        end loop;
        return '1';  -- Equal
    end function;

    function compare_lt(a, b : bit_vector) return bit is
    begin
        for i in a'high downto a'low loop
            if a(i) = '0' and b(i) = '1' then return '1'; end if;
            if a(i) = '1' and b(i) = '0' then return '0'; end if;
        end loop;
        return '0';  -- Equal means not less than
    end function;

begin
    -- Edge detection for kick
    kick_edge <= kick and (not kick_prev);

    main_proc: process(clk, reset)
    begin
        if reset = '1' then
            counter <= (others => '0');
            timeout_reg <= '0';
            warning_reg <= '0';
            violation_reg <= '0';
            kick_prev <= '0';
            running <= '0';
        elsif clk'event and clk = '1' then
            kick_prev <= kick;

            if enable = '1' then
                running <= '1';

                -- Check for timeout
                if compare_ge(counter, timeout_value) = '1' then
                    timeout_reg <= '1';
                    -- Keep timeout latched until reset
                end if;

                -- Check for warning
                if compare_ge(counter, warning_value) = '1' then
                    warning_reg <= '1';
                else
                    warning_reg <= '0';
                end if;

                -- Handle kick
                if kick_edge = '1' then
                    -- Window mode check
                    if window_enable = '1' then
                        if compare_lt(counter, window_open) = '1' then
                            -- Kicked too early - violation
                            violation_reg <= '1';
                        else
                            -- Valid kick in window
                            counter <= (others => '0');
                            warning_reg <= '0';
                        end if;
                    else
                        -- No window mode - any kick is valid
                        counter <= (others => '0');
                        warning_reg <= '0';
                    end if;
                elsif timeout_reg = '0' then
                    -- Increment counter if not timed out
                    counter <= increment(counter);
                end if;
            else
                running <= '0';
            end if;
        end if;
    end process main_proc;

    timeout <= timeout_reg;
    warning <= warning_reg;
    window_violation <= violation_reg;
    counter_value <= counter;

end Behavioral;

-- ============================================================================
-- Multi-Stage Watchdog (cascade of watchdogs)
-- ============================================================================
entity MultiStageWatchdog is
    generic (
        COUNTER_WIDTH : integer := 16
    );
    port (
        clk             : in  bit;
        reset           : in  bit;
        enable          : in  bit;
        kick            : in  bit;
        -- Stage timeouts
        stage1_timeout  : in  bit_vector(COUNTER_WIDTH-1 downto 0);
        stage2_timeout  : in  bit_vector(COUNTER_WIDTH-1 downto 0);
        stage3_timeout  : in  bit_vector(COUNTER_WIDTH-1 downto 0);
        -- Outputs
        stage1_warn     : out bit;  -- First warning
        stage2_warn     : out bit;  -- Second warning (more serious)
        stage3_reset    : out bit   -- Final timeout - system reset
    );
end MultiStageWatchdog;

architecture Behavioral of MultiStageWatchdog is
    signal counter : bit_vector(COUNTER_WIDTH-1 downto 0);
    signal kick_prev : bit;

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

    function compare_ge(a, b : bit_vector) return bit is
    begin
        for i in a'high downto a'low loop
            if a(i) = '1' and b(i) = '0' then return '1'; end if;
            if a(i) = '0' and b(i) = '1' then return '0'; end if;
        end loop;
        return '1';
    end function;

begin
    process(clk, reset)
        variable kick_edge : bit;
    begin
        if reset = '1' then
            counter <= (others => '0');
            kick_prev <= '0';
            stage1_warn <= '0';
            stage2_warn <= '0';
            stage3_reset <= '0';
        elsif clk'event and clk = '1' then
            kick_prev <= kick;
            kick_edge := kick and (not kick_prev);

            if enable = '1' then
                if kick_edge = '1' then
                    -- Reset on kick
                    counter <= (others => '0');
                    stage1_warn <= '0';
                    stage2_warn <= '0';
                    stage3_reset <= '0';
                else
                    counter <= increment(counter);

                    -- Stage checks
                    if compare_ge(counter, stage1_timeout) = '1' then
                        stage1_warn <= '1';
                    end if;

                    if compare_ge(counter, stage2_timeout) = '1' then
                        stage2_warn <= '1';
                    end if;

                    if compare_ge(counter, stage3_timeout) = '1' then
                        stage3_reset <= '1';
                    end if;
                end if;
            end if;
        end if;
    end process;

end Behavioral;

-- ============================================================================
-- Independent Watchdog (runs on separate clock)
-- ============================================================================
entity IndependentWatchdog is
    generic (
        TIMEOUT_CYCLES : integer := 4096
    );
    port (
        wdt_clk         : in  bit;  -- Independent low-speed clock
        sys_clk         : in  bit;  -- System clock
        reset           : in  bit;
        enable          : in  bit;
        kick            : in  bit;  -- On system clock domain
        timeout         : out bit;
        counter_value   : out bit_vector(15 downto 0)
    );
end IndependentWatchdog;

architecture Behavioral of IndependentWatchdog is
    -- Synchronization for kick signal (sys_clk -> wdt_clk)
    signal kick_sync1, kick_sync2 : bit;
    signal kick_synced : bit;
    signal kick_prev : bit;

    signal counter : integer range 0 to TIMEOUT_CYCLES;
    signal timeout_reg : bit;

begin
    -- Synchronize kick to WDT clock domain
    sync_proc: process(wdt_clk, reset)
    begin
        if reset = '1' then
            kick_sync1 <= '0';
            kick_sync2 <= '0';
        elsif wdt_clk'event and wdt_clk = '1' then
            kick_sync1 <= kick;
            kick_sync2 <= kick_sync1;
        end if;
    end process sync_proc;

    kick_synced <= kick_sync2;

    -- Watchdog counter on independent clock
    wdt_proc: process(wdt_clk, reset)
        variable kick_edge : bit;
    begin
        if reset = '1' then
            counter <= 0;
            timeout_reg <= '0';
            kick_prev <= '0';
        elsif wdt_clk'event and wdt_clk = '1' then
            kick_prev <= kick_synced;
            kick_edge := kick_synced and (not kick_prev);

            if enable = '1' then
                if kick_edge = '1' then
                    counter <= 0;
                elsif counter >= TIMEOUT_CYCLES then
                    timeout_reg <= '1';
                else
                    counter <= counter + 1;
                end if;
            end if;
        end if;
    end process wdt_proc;

    timeout <= timeout_reg;

    -- Convert counter to bit_vector
    conv_proc: process(counter)
        variable temp : integer;
    begin
        temp := counter;
        for i in 0 to 15 loop
            if (temp mod 2) = 1 then
                counter_value(i) <= '1';
            else
                counter_value(i) <= '0';
            end if;
            temp := temp / 2;
        end loop;
    end process conv_proc;

end Behavioral;

-- ============================================================================
-- Task Watchdog (monitors multiple tasks)
-- ============================================================================
entity TaskWatchdog is
    generic (
        NUM_TASKS : integer := 4;
        COUNTER_WIDTH : integer := 16
    );
    port (
        clk             : in  bit;
        reset           : in  bit;
        enable          : in  bit;
        -- Per-task kick signals
        task_kicks      : in  bit_vector(NUM_TASKS-1 downto 0);
        -- Per-task timeout values
        task0_timeout   : in  bit_vector(COUNTER_WIDTH-1 downto 0);
        task1_timeout   : in  bit_vector(COUNTER_WIDTH-1 downto 0);
        task2_timeout   : in  bit_vector(COUNTER_WIDTH-1 downto 0);
        task3_timeout   : in  bit_vector(COUNTER_WIDTH-1 downto 0);
        -- Per-task timeout outputs
        task_timeouts   : out bit_vector(NUM_TASKS-1 downto 0);
        -- Global status
        any_timeout     : out bit;
        all_healthy     : out bit
    );
end TaskWatchdog;

architecture Behavioral of TaskWatchdog is
    type counter_array is array (0 to NUM_TASKS-1) of bit_vector(COUNTER_WIDTH-1 downto 0);
    type timeout_array is array (0 to NUM_TASKS-1) of bit_vector(COUNTER_WIDTH-1 downto 0);

    signal counters : counter_array;
    signal kick_prev : bit_vector(NUM_TASKS-1 downto 0);
    signal timeout_regs : bit_vector(NUM_TASKS-1 downto 0);

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

    function compare_ge(a, b : bit_vector) return bit is
    begin
        for i in a'high downto a'low loop
            if a(i) = '1' and b(i) = '0' then return '1'; end if;
            if a(i) = '0' and b(i) = '1' then return '0'; end if;
        end loop;
        return '1';
    end function;

begin
    -- Generate watchdog for each task
    task_gen: for i in 0 to NUM_TASKS-1 generate
        process(clk, reset)
            variable kick_edge : bit;
            variable timeout_val : bit_vector(COUNTER_WIDTH-1 downto 0);
        begin
            if reset = '1' then
                counters(i) <= (others => '0');
                timeout_regs(i) <= '0';
                kick_prev(i) <= '0';
            elsif clk'event and clk = '1' then
                kick_prev(i) <= task_kicks(i);
                kick_edge := task_kicks(i) and (not kick_prev(i));

                -- Select timeout value
                case i is
                    when 0 => timeout_val := task0_timeout;
                    when 1 => timeout_val := task1_timeout;
                    when 2 => timeout_val := task2_timeout;
                    when 3 => timeout_val := task3_timeout;
                    when others => timeout_val := (others => '1');
                end case;

                if enable = '1' then
                    if kick_edge = '1' then
                        counters(i) <= (others => '0');
                        timeout_regs(i) <= '0';
                    elsif compare_ge(counters(i), timeout_val) = '1' then
                        timeout_regs(i) <= '1';
                    else
                        counters(i) <= increment(counters(i));
                    end if;
                end if;
            end if;
        end process;
    end generate task_gen;

    task_timeouts <= timeout_regs;

    -- Status outputs
    status_proc: process(timeout_regs)
        variable any_to, all_ok : bit;
    begin
        any_to := '0';
        all_ok := '1';
        for i in 0 to NUM_TASKS-1 loop
            any_to := any_to or timeout_regs(i);
            all_ok := all_ok and (not timeout_regs(i));
        end loop;
        any_timeout <= any_to;
        all_healthy <= all_ok;
    end process status_proc;

end Behavioral;

-- ============================================================================
-- Testbench
-- ============================================================================
entity WatchdogTimer_TB is
end WatchdogTimer_TB;

architecture Test of WatchdogTimer_TB is
    signal tb_clk, tb_reset, tb_enable, tb_kick : bit;
    signal tb_timeout_value, tb_warning_value, tb_window_open : bit_vector(23 downto 0);
    signal tb_window_enable : bit;
    signal tb_timeout, tb_warning, tb_violation : bit;
    signal tb_counter : bit_vector(23 downto 0);
begin
    UUT: entity work.WatchdogTimer
        generic map (COUNTER_WIDTH => 24)
        port map (
            clk              => tb_clk,
            reset            => tb_reset,
            enable           => tb_enable,
            kick             => tb_kick,
            timeout_value    => tb_timeout_value,
            warning_value    => tb_warning_value,
            window_enable    => tb_window_enable,
            window_open      => tb_window_open,
            timeout          => tb_timeout,
            warning          => tb_warning,
            window_violation => tb_violation,
            counter_value    => tb_counter,
            running          => open
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
        tb_kick <= '0';
        tb_timeout_value <= "000000000000000000100000";  -- Timeout at 32
        tb_warning_value <= "000000000000000000010000";  -- Warning at 16
        tb_window_enable <= '0';
        tb_window_open <= "000000000000000000001000";    -- Window opens at 8
        wait for 30 ns;

        tb_reset <= '0';
        tb_enable <= '1';

        -- Let it run towards warning
        wait for 200 ns;

        -- Kick before timeout
        tb_kick <= '1'; wait for 10 ns;
        tb_kick <= '0'; wait for 200 ns;

        -- Let it timeout
        wait for 500 ns;

        -- Test window mode
        tb_reset <= '1'; wait for 20 ns;
        tb_reset <= '0';
        tb_window_enable <= '1';

        -- Kick too early (violation)
        wait for 50 ns;
        tb_kick <= '1'; wait for 10 ns;
        tb_kick <= '0';

        wait for 200 ns;
        wait;
    end process;
end Test;
