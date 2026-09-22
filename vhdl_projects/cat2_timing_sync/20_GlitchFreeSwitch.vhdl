-- ============================================================================
-- VHDL Project: GlitchFreeSwitch
-- Category: Timing & Synchronization
-- Complexity: High
-- Description: Glitch-free clock multiplexer
--              Safe switching between multiple clock sources
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity GlitchFreeSwitch is
    port (
        -- Clock inputs
        clk_a       : in  bit;
        clk_b       : in  bit;
        -- Selection (active high for clk_b)
        sel         : in  bit;
        -- Asynchronous reset
        reset       : in  bit;
        -- Output clock
        clk_out     : out bit;
        -- Status
        active_clk  : out bit  -- '0' = clk_a active, '1' = clk_b active
    );
end GlitchFreeSwitch;

architecture Behavioral of GlitchFreeSwitch is
    -- Enable signals synchronized to each clock domain
    signal en_a_sync1, en_a_sync2, en_a_sync3 : bit;
    signal en_b_sync1, en_b_sync2, en_b_sync3 : bit;

    -- Gated enable signals
    signal en_a : bit;
    signal en_b : bit;

    -- Gated clocks
    signal clk_a_gated : bit;
    signal clk_b_gated : bit;

begin
    -- Synchronize and gate enable for clock A
    -- Enable A when sel='0' AND B is disabled
    sync_a: process(clk_a, reset)
    begin
        if reset = '1' then
            en_a_sync1 <= '1';  -- Start with A enabled
            en_a_sync2 <= '1';
            en_a_sync3 <= '1';
        elsif clk_a'event and clk_a = '0' then  -- Falling edge
            en_a_sync1 <= (not sel) and (not en_b_sync3);
            en_a_sync2 <= en_a_sync1;
            en_a_sync3 <= en_a_sync2;
        end if;
    end process sync_a;

    en_a <= en_a_sync3;

    -- Synchronize and gate enable for clock B
    -- Enable B when sel='1' AND A is disabled
    sync_b: process(clk_b, reset)
    begin
        if reset = '1' then
            en_b_sync1 <= '0';  -- Start with B disabled
            en_b_sync2 <= '0';
            en_b_sync3 <= '0';
        elsif clk_b'event and clk_b = '0' then  -- Falling edge
            en_b_sync1 <= sel and (not en_a_sync3);
            en_b_sync2 <= en_b_sync1;
            en_b_sync3 <= en_b_sync2;
        end if;
    end process sync_b;

    en_b <= en_b_sync3;

    -- Gate clocks with synchronized enables
    clk_a_gated <= clk_a and en_a;
    clk_b_gated <= clk_b and en_b;

    -- OR gated clocks for output
    clk_out <= clk_a_gated or clk_b_gated;

    -- Status output
    active_clk <= en_b;

end Behavioral;

-- ============================================================================
-- 4-Input Glitch-Free Clock Multiplexer
-- ============================================================================
entity GlitchFreeMux4 is
    port (
        clk_0       : in  bit;
        clk_1       : in  bit;
        clk_2       : in  bit;
        clk_3       : in  bit;
        sel         : in  bit_vector(1 downto 0);
        reset       : in  bit;
        clk_out     : out bit;
        active_sel  : out bit_vector(1 downto 0)
    );
end GlitchFreeMux4;

architecture Behavioral of GlitchFreeMux4 is
    type sync_array is array (0 to 3) of bit_vector(2 downto 0);
    signal en_sync : sync_array;
    signal en : bit_vector(3 downto 0);
    signal clk_gated : bit_vector(3 downto 0);

    signal sel_decoded : bit_vector(3 downto 0);
    signal other_disabled : bit_vector(3 downto 0);

    function to_integer(bv : bit_vector) return integer is
        variable result : integer := 0;
    begin
        if bv(0) = '1' then result := result + 1; end if;
        if bv(1) = '1' then result := result + 2; end if;
        return result;
    end function;

begin
    -- Decode selection
    sel_decoded(0) <= '1' when sel = "00" else '0';
    sel_decoded(1) <= '1' when sel = "01" else '0';
    sel_decoded(2) <= '1' when sel = "10" else '0';
    sel_decoded(3) <= '1' when sel = "11" else '0';

    -- Check if other clocks are disabled
    other_disabled(0) <= (not en(1)) and (not en(2)) and (not en(3));
    other_disabled(1) <= (not en(0)) and (not en(2)) and (not en(3));
    other_disabled(2) <= (not en(0)) and (not en(1)) and (not en(3));
    other_disabled(3) <= (not en(0)) and (not en(1)) and (not en(2));

    -- Clock 0 synchronizer
    sync_0: process(clk_0, reset)
    begin
        if reset = '1' then
            en_sync(0) <= "100";  -- Start with clk_0 enabled
        elsif clk_0'event and clk_0 = '0' then
            en_sync(0)(0) <= sel_decoded(0) and other_disabled(0);
            en_sync(0)(1) <= en_sync(0)(0);
            en_sync(0)(2) <= en_sync(0)(1);
        end if;
    end process sync_0;

    -- Clock 1 synchronizer
    sync_1: process(clk_1, reset)
    begin
        if reset = '1' then
            en_sync(1) <= "000";
        elsif clk_1'event and clk_1 = '0' then
            en_sync(1)(0) <= sel_decoded(1) and other_disabled(1);
            en_sync(1)(1) <= en_sync(1)(0);
            en_sync(1)(2) <= en_sync(1)(1);
        end if;
    end process sync_1;

    -- Clock 2 synchronizer
    sync_2: process(clk_2, reset)
    begin
        if reset = '1' then
            en_sync(2) <= "000";
        elsif clk_2'event and clk_2 = '0' then
            en_sync(2)(0) <= sel_decoded(2) and other_disabled(2);
            en_sync(2)(1) <= en_sync(2)(0);
            en_sync(2)(2) <= en_sync(2)(1);
        end if;
    end process sync_2;

    -- Clock 3 synchronizer
    sync_3: process(clk_3, reset)
    begin
        if reset = '1' then
            en_sync(3) <= "000";
        elsif clk_3'event and clk_3 = '0' then
            en_sync(3)(0) <= sel_decoded(3) and other_disabled(3);
            en_sync(3)(1) <= en_sync(3)(0);
            en_sync(3)(2) <= en_sync(3)(1);
        end if;
    end process sync_3;

    -- Extract final enables
    en(0) <= en_sync(0)(2);
    en(1) <= en_sync(1)(2);
    en(2) <= en_sync(2)(2);
    en(3) <= en_sync(3)(2);

    -- Gate clocks
    clk_gated(0) <= clk_0 and en(0);
    clk_gated(1) <= clk_1 and en(1);
    clk_gated(2) <= clk_2 and en(2);
    clk_gated(3) <= clk_3 and en(3);

    -- OR all gated clocks
    clk_out <= clk_gated(0) or clk_gated(1) or clk_gated(2) or clk_gated(3);

    -- Active selection output
    active_sel(0) <= en(1) or en(3);
    active_sel(1) <= en(2) or en(3);

end Behavioral;

-- ============================================================================
-- Clock Presence Detector
-- ============================================================================
entity ClockPresenceDetector is
    generic (
        TIMEOUT_CYCLES : integer := 100  -- Cycles of reference clock
    );
    port (
        ref_clk     : in  bit;  -- Reference clock (always running)
        test_clk    : in  bit;  -- Clock to test
        reset       : in  bit;
        present     : out bit;  -- '1' if test_clk is running
        lost        : out bit;  -- Pulse when clock is lost
        found       : out bit   -- Pulse when clock is found
    );
end ClockPresenceDetector;

architecture Behavioral of ClockPresenceDetector is
    -- Synchronize test_clk to ref_clk domain
    signal test_sync1, test_sync2, test_sync3 : bit;
    signal test_edge : bit;

    -- Timeout counter
    signal timeout_counter : integer range 0 to TIMEOUT_CYCLES;
    signal present_reg : bit;
    signal present_prev : bit;

begin
    -- Synchronize test clock to reference domain
    sync_proc: process(ref_clk, reset)
    begin
        if reset = '1' then
            test_sync1 <= '0';
            test_sync2 <= '0';
            test_sync3 <= '0';
        elsif ref_clk'event and ref_clk = '1' then
            test_sync1 <= test_clk;
            test_sync2 <= test_sync1;
            test_sync3 <= test_sync2;
        end if;
    end process sync_proc;

    -- Edge detection
    test_edge <= test_sync2 xor test_sync3;

    -- Presence detection
    detect_proc: process(ref_clk, reset)
    begin
        if reset = '1' then
            timeout_counter <= 0;
            present_reg <= '0';
            present_prev <= '0';
        elsif ref_clk'event and ref_clk = '1' then
            present_prev <= present_reg;

            if test_edge = '1' then
                -- Activity detected
                timeout_counter <= 0;
                present_reg <= '1';
            else
                -- No activity
                if timeout_counter >= TIMEOUT_CYCLES - 1 then
                    present_reg <= '0';
                else
                    timeout_counter <= timeout_counter + 1;
                end if;
            end if;
        end if;
    end process detect_proc;

    present <= present_reg;
    lost <= present_prev and (not present_reg);
    found <= (not present_prev) and present_reg;

end Behavioral;

-- ============================================================================
-- Auto-Switching Clock Multiplexer (with failover)
-- ============================================================================
entity AutoSwitchClockMux is
    generic (
        TIMEOUT_CYCLES : integer := 100
    );
    port (
        ref_clk     : in  bit;  -- Reference for presence detection
        clk_primary : in  bit;  -- Primary clock
        clk_backup  : in  bit;  -- Backup clock
        reset       : in  bit;
        force_backup: in  bit;  -- Force backup clock
        clk_out     : out bit;
        using_backup: out bit;
        primary_ok  : out bit;
        backup_ok   : out bit
    );
end AutoSwitchClockMux;

architecture Behavioral of AutoSwitchClockMux is
    signal primary_present, backup_present : bit;
    signal sel_backup : bit;

    -- Glitch-free mux signals
    signal en_primary_sync : bit_vector(2 downto 0);
    signal en_backup_sync : bit_vector(2 downto 0);
    signal en_primary, en_backup : bit;

begin
    -- Primary clock presence detector
    primary_detect: entity work.ClockPresenceDetector
        generic map (TIMEOUT_CYCLES => TIMEOUT_CYCLES)
        port map (
            ref_clk  => ref_clk,
            test_clk => clk_primary,
            reset    => reset,
            present  => primary_present,
            lost     => open,
            found    => open
        );

    -- Backup clock presence detector
    backup_detect: entity work.ClockPresenceDetector
        generic map (TIMEOUT_CYCLES => TIMEOUT_CYCLES)
        port map (
            ref_clk  => ref_clk,
            test_clk => clk_backup,
            reset    => reset,
            present  => backup_present,
            lost     => open,
            found    => open
        );

    -- Selection logic
    sel_backup <= force_backup or (not primary_present);

    -- Synchronize primary enable
    sync_primary: process(clk_primary, reset)
    begin
        if reset = '1' then
            en_primary_sync <= "111";
        elsif clk_primary'event and clk_primary = '0' then
            en_primary_sync(0) <= (not sel_backup) and (not en_backup);
            en_primary_sync(1) <= en_primary_sync(0);
            en_primary_sync(2) <= en_primary_sync(1);
        end if;
    end process sync_primary;

    en_primary <= en_primary_sync(2);

    -- Synchronize backup enable
    sync_backup: process(clk_backup, reset)
    begin
        if reset = '1' then
            en_backup_sync <= "000";
        elsif clk_backup'event and clk_backup = '0' then
            en_backup_sync(0) <= sel_backup and (not en_primary);
            en_backup_sync(1) <= en_backup_sync(0);
            en_backup_sync(2) <= en_backup_sync(1);
        end if;
    end process sync_backup;

    en_backup <= en_backup_sync(2);

    -- Glitch-free output
    clk_out <= (clk_primary and en_primary) or (clk_backup and en_backup);

    -- Status outputs
    using_backup <= en_backup;
    primary_ok <= primary_present;
    backup_ok <= backup_present;

end Behavioral;

-- ============================================================================
-- Testbench
-- ============================================================================
entity GlitchFreeSwitch_TB is
end GlitchFreeSwitch_TB;

architecture Test of GlitchFreeSwitch_TB is
    signal tb_clk_a, tb_clk_b : bit;
    signal tb_sel, tb_reset : bit;
    signal tb_clk_out, tb_active_clk : bit;
begin
    UUT: entity work.GlitchFreeSwitch
        port map (
            clk_a      => tb_clk_a,
            clk_b      => tb_clk_b,
            sel        => tb_sel,
            reset      => tb_reset,
            clk_out    => tb_clk_out,
            active_clk => tb_active_clk
        );

    -- Clock A: 100 MHz
    clk_a_gen: process
    begin
        tb_clk_a <= '0'; wait for 5 ns;
        tb_clk_a <= '1'; wait for 5 ns;
    end process;

    -- Clock B: 80 MHz
    clk_b_gen: process
    begin
        tb_clk_b <= '0'; wait for 6.25 ns;
        tb_clk_b <= '1'; wait for 6.25 ns;
    end process;

    stimulus: process
    begin
        tb_reset <= '1';
        tb_sel <= '0';
        wait for 50 ns;

        tb_reset <= '0';
        wait for 200 ns;

        -- Switch to clock B
        tb_sel <= '1';
        wait for 200 ns;

        -- Switch back to clock A
        tb_sel <= '0';
        wait for 200 ns;

        -- Rapid switching
        tb_sel <= '1'; wait for 50 ns;
        tb_sel <= '0'; wait for 50 ns;
        tb_sel <= '1'; wait for 100 ns;

        wait;
    end process;
end Test;
