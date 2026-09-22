-- ============================================================================
-- VHDL Project: WatchdogTimer
-- Category: Advanced Systems
-- Complexity: Medium
-- Description: Watchdog timer with window mode and early warning interrupt
--              Multiple reset modes, debug halt support
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity WatchdogTimer is
    generic (
        COUNTER_WIDTH : integer := 24;
        CLK_FREQ      : integer := 50000000
    );
    port (
        clk           : in  bit;
        reset         : in  bit;
        -- Configuration
        cfg_enable    : in  bit;
        cfg_period    : in  bit_vector(COUNTER_WIDTH-1 downto 0);
        cfg_window    : in  bit_vector(COUNTER_WIDTH-1 downto 0);  -- Window start
        cfg_warn_en   : in  bit;
        cfg_warn_time : in  bit_vector(COUNTER_WIDTH-1 downto 0);  -- Warning threshold
        -- Control
        kick          : in  bit;  -- Service the watchdog
        debug_halt    : in  bit;  -- Pause during debug
        -- Outputs
        reset_out     : out bit;
        warn_irq      : out bit;
        window_open   : out bit;
        counter_val   : out bit_vector(COUNTER_WIDTH-1 downto 0)
    );
end WatchdogTimer;

architecture Behavioral of WatchdogTimer is
    signal counter : bit_vector(COUNTER_WIDTH-1 downto 0);
    signal running : bit;
    signal reset_pending : bit;
    signal warn_pending : bit;
    signal in_window : bit;

    function to_int(bv : bit_vector) return integer is
        variable result : integer := 0;
        variable power : integer := 1;
    begin
        for i in bv'low to bv'high loop
            if bv(i) = '1' then result := result + power; end if;
            power := power * 2;
        end loop;
        return result;
    end function;

    function to_bv(val : integer; width : integer) return bit_vector is
        variable result : bit_vector(width-1 downto 0);
        variable temp : integer := val;
    begin
        for i in 0 to width-1 loop
            if (temp mod 2) = 1 then result(i) := '1';
            else result(i) := '0'; end if;
            temp := temp / 2;
        end loop;
        return result;
    end function;

begin
    watchdog_proc: process(clk, reset)
        variable cnt : integer;
        variable period : integer;
        variable window_start : integer;
        variable warn_threshold : integer;
    begin
        if reset = '1' then
            counter <= (others => '0');
            running <= '0';
            reset_pending <= '0';
            warn_pending <= '0';
            in_window <= '0';
            reset_out <= '0';
            warn_irq <= '0';
        elsif clk'event and clk = '1' then
            reset_out <= '0';
            warn_irq <= '0';

            cnt := to_int(counter);
            period := to_int(cfg_period);
            window_start := to_int(cfg_window);
            warn_threshold := to_int(cfg_warn_time);

            if cfg_enable = '1' then
                running <= '1';

                -- Don't count during debug halt
                if debug_halt = '0' then
                    -- Increment counter
                    counter <= to_bv(cnt + 1, COUNTER_WIDTH);
                    cnt := cnt + 1;

                    -- Check window status
                    if cnt >= window_start then
                        in_window <= '1';
                    else
                        in_window <= '0';
                    end if;

                    -- Check for early warning
                    if cfg_warn_en = '1' and cnt = warn_threshold then
                        warn_irq <= '1';
                        warn_pending <= '1';
                    end if;

                    -- Check for timeout
                    if cnt >= period then
                        reset_pending <= '1';
                    end if;
                end if;

                -- Handle kick (service)
                if kick = '1' then
                    if in_window = '1' then
                        -- Valid kick - reset counter
                        counter <= (others => '0');
                        reset_pending <= '0';
                        warn_pending <= '0';
                        in_window <= '0';
                    else
                        -- Invalid kick (too early) - trigger reset
                        reset_pending <= '1';
                    end if;
                end if;

                -- Generate reset
                if reset_pending = '1' then
                    reset_out <= '1';
                    counter <= (others => '0');
                    reset_pending <= '0';
                    warn_pending <= '0';
                end if;
            else
                -- Disabled
                running <= '0';
                counter <= (others => '0');
                reset_pending <= '0';
                warn_pending <= '0';
                in_window <= '0';
            end if;
        end if;
    end process;

    window_open <= in_window;
    counter_val <= counter;
end Behavioral;

-- ============================================================================
-- Power Management Unit
-- ============================================================================
entity PowerManagement is
    port (
        clk          : in  bit;
        reset        : in  bit;
        -- Sleep request from CPU
        sleep_req    : in  bit;
        sleep_deep   : in  bit;
        -- Wake sources
        wake_irq     : in  bit;
        wake_timer   : in  bit;
        wake_gpio    : in  bit;
        -- Power control outputs
        cpu_clk_en   : out bit;
        periph_clk_en : out bit;
        ram_retain   : out bit;
        pll_enable   : out bit;
        -- Status
        power_state  : out bit_vector(1 downto 0);
        wake_source  : out bit_vector(2 downto 0)
    );
end PowerManagement;

architecture Behavioral of PowerManagement is
    type state_type is (RUN, IDLE, SLEEP, DEEP_SLEEP, WAKING);
    signal state : state_type;
    signal wake_reason : bit_vector(2 downto 0);
begin
    pmu_proc: process(clk, reset)
    begin
        if reset = '1' then
            state <= RUN;
            cpu_clk_en <= '1';
            periph_clk_en <= '1';
            ram_retain <= '1';
            pll_enable <= '1';
            wake_reason <= "000";
        elsif clk'event and clk = '1' then
            case state is
                when RUN =>
                    cpu_clk_en <= '1';
                    periph_clk_en <= '1';
                    ram_retain <= '1';
                    pll_enable <= '1';
                    power_state <= "00";

                    if sleep_req = '1' then
                        if sleep_deep = '1' then
                            state <= DEEP_SLEEP;
                        else
                            state <= SLEEP;
                        end if;
                    end if;

                when IDLE =>
                    -- CPU clock gated, peripherals running
                    cpu_clk_en <= '0';
                    periph_clk_en <= '1';
                    ram_retain <= '1';
                    pll_enable <= '1';
                    power_state <= "01";

                    if wake_irq = '1' then
                        wake_reason <= "001";
                        state <= WAKING;
                    end if;

                when SLEEP =>
                    -- CPU and most peripherals off
                    cpu_clk_en <= '0';
                    periph_clk_en <= '0';
                    ram_retain <= '1';
                    pll_enable <= '0';
                    power_state <= "10";

                    if wake_irq = '1' then
                        wake_reason <= "001";
                        state <= WAKING;
                    elsif wake_timer = '1' then
                        wake_reason <= "010";
                        state <= WAKING;
                    elsif wake_gpio = '1' then
                        wake_reason <= "100";
                        state <= WAKING;
                    end if;

                when DEEP_SLEEP =>
                    -- Everything off except wake logic
                    cpu_clk_en <= '0';
                    periph_clk_en <= '0';
                    ram_retain <= '0';
                    pll_enable <= '0';
                    power_state <= "11";

                    if wake_irq = '1' or wake_timer = '1' or wake_gpio = '1' then
                        if wake_irq = '1' then wake_reason <= "001";
                        elsif wake_timer = '1' then wake_reason <= "010";
                        else wake_reason <= "100";
                        end if;
                        state <= WAKING;
                    end if;

                when WAKING =>
                    -- Restore power gradually
                    pll_enable <= '1';
                    ram_retain <= '1';
                    periph_clk_en <= '1';
                    cpu_clk_en <= '1';
                    state <= RUN;
            end case;
        end if;
    end process;

    wake_source <= wake_reason;
end Behavioral;

-- ============================================================================
-- Reset Controller
-- ============================================================================
entity ResetController is
    generic (
        RESET_CYCLES : integer := 16;
        POR_CYCLES   : integer := 1000
    );
    port (
        clk          : in  bit;
        por_n        : in  bit;  -- Power-on reset (active low)
        ext_reset_n  : in  bit;  -- External reset (active low)
        wdt_reset    : in  bit;  -- Watchdog reset
        sw_reset     : in  bit;  -- Software reset request
        -- Reset outputs
        sys_reset    : out bit;
        periph_reset : out bit;
        -- Reset cause
        reset_cause  : out bit_vector(3 downto 0)
    );
end ResetController;

architecture Behavioral of ResetController is
    signal reset_counter : integer range 0 to POR_CYCLES;
    signal in_reset : bit;
    signal cause_reg : bit_vector(3 downto 0);
begin
    reset_proc: process(clk, por_n)
    begin
        if por_n = '0' then
            reset_counter <= POR_CYCLES;
            in_reset <= '1';
            cause_reg <= "0001";  -- POR
            sys_reset <= '1';
            periph_reset <= '1';
        elsif clk'event and clk = '1' then
            -- Check for reset sources
            if ext_reset_n = '0' then
                reset_counter <= RESET_CYCLES;
                in_reset <= '1';
                cause_reg <= "0010";  -- External
            elsif wdt_reset = '1' then
                reset_counter <= RESET_CYCLES;
                in_reset <= '1';
                cause_reg <= "0100";  -- Watchdog
            elsif sw_reset = '1' then
                reset_counter <= RESET_CYCLES;
                in_reset <= '1';
                cause_reg <= "1000";  -- Software
            end if;

            -- Count down reset duration
            if in_reset = '1' then
                if reset_counter > 0 then
                    reset_counter <= reset_counter - 1;
                    sys_reset <= '1';
                    periph_reset <= '1';
                else
                    in_reset <= '0';
                    sys_reset <= '0';
                    periph_reset <= '0';
                end if;
            end if;
        end if;
    end process;

    reset_cause <= cause_reg;
end Behavioral;
