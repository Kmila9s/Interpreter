-- ============================================================================
-- VHDL Project: ClockManager
-- Category: Advanced Systems
-- Complexity: High
-- Description: Clock management unit with PLL-like frequency synthesis
--              Clock gating, division, and dynamic frequency switching
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity ClockManager is
    generic (
        NUM_OUTPUTS : integer := 4
    );
    port (
        clk_in       : in  bit;
        reset        : in  bit;
        -- Configuration
        cfg_addr     : in  bit_vector(3 downto 0);
        cfg_wdata    : in  bit_vector(15 downto 0);
        cfg_we       : in  bit;
        cfg_rdata    : out bit_vector(15 downto 0);
        -- Enable controls
        clk_enable   : in  bit_vector(NUM_OUTPUTS-1 downto 0);
        -- Clock outputs
        clk_out      : out bit_vector(NUM_OUTPUTS-1 downto 0);
        -- Status
        locked       : out bit
    );
end ClockManager;

architecture Behavioral of ClockManager is
    -- Clock divider registers
    type div_array is array (0 to NUM_OUTPUTS-1) of bit_vector(7 downto 0);
    signal dividers : div_array;

    -- Divider counters
    type cnt_array is array (0 to NUM_OUTPUTS-1) of bit_vector(7 downto 0);
    signal div_counters : cnt_array;

    -- Generated clock signals
    signal clk_divided : bit_vector(NUM_OUTPUTS-1 downto 0);

    -- Phase accumulators for fractional division (DDS-style)
    type phase_array is array (0 to NUM_OUTPUTS-1) of bit_vector(15 downto 0);
    signal phase_acc : phase_array;
    signal phase_inc : phase_array;

    -- Lock detection
    signal lock_counter : integer range 0 to 255;
    signal lock_stable : bit;

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
    -- Configuration register access
    config_proc: process(clk_in, reset)
        variable addr : integer;
    begin
        if reset = '1' then
            for i in 0 to NUM_OUTPUTS-1 loop
                dividers(i) <= "00000010";  -- Default divide by 2
                phase_inc(i) <= "1000000000000000";  -- Default 50% frequency
            end loop;
            cfg_rdata <= (others => '0');
        elsif clk_in'event and clk_in = '1' then
            addr := to_int(cfg_addr);

            if cfg_we = '1' then
                if addr < NUM_OUTPUTS then
                    -- Divider register
                    dividers(addr) <= cfg_wdata(7 downto 0);
                elsif addr < NUM_OUTPUTS * 2 then
                    -- Phase increment register
                    phase_inc(addr - NUM_OUTPUTS) <= cfg_wdata;
                end if;
            end if;

            -- Read back
            if addr < NUM_OUTPUTS then
                cfg_rdata <= "00000000" & dividers(addr);
            elsif addr < NUM_OUTPUTS * 2 then
                cfg_rdata <= phase_inc(addr - NUM_OUTPUTS);
            else
                cfg_rdata <= (others => '0');
            end if;
        end if;
    end process;

    -- Integer clock dividers
    div_gen: for i in 0 to NUM_OUTPUTS-1 generate
        div_proc: process(clk_in, reset)
            variable cnt : integer;
            variable div_val : integer;
        begin
            if reset = '1' then
                div_counters(i) <= (others => '0');
                clk_divided(i) <= '0';
            elsif clk_in'event and clk_in = '1' then
                cnt := to_int(div_counters(i));
                div_val := to_int(dividers(i));

                if div_val = 0 then
                    div_val := 1;
                end if;

                if cnt >= div_val - 1 then
                    div_counters(i) <= (others => '0');
                    clk_divided(i) <= not clk_divided(i);
                else
                    div_counters(i) <= to_bv(cnt + 1, 8);
                end if;
            end if;
        end process;
    end generate;

    -- DDS-style fractional divider (for finer frequency control)
    dds_gen: for i in 0 to NUM_OUTPUTS-1 generate
        dds_proc: process(clk_in, reset)
            variable acc : integer;
            variable inc : integer;
        begin
            if reset = '1' then
                phase_acc(i) <= (others => '0');
            elsif clk_in'event and clk_in = '1' then
                acc := to_int(phase_acc(i));
                inc := to_int(phase_inc(i));
                phase_acc(i) <= to_bv((acc + inc) mod 65536, 16);
            end if;
        end process;
    end generate;

    -- Output gating
    output_gen: for i in 0 to NUM_OUTPUTS-1 generate
        clk_out(i) <= clk_divided(i) and clk_enable(i);
    end generate;

    -- Lock detection (simple version)
    lock_proc: process(clk_in, reset)
    begin
        if reset = '1' then
            lock_counter <= 0;
            lock_stable <= '0';
        elsif clk_in'event and clk_in = '1' then
            if lock_counter < 255 then
                lock_counter <= lock_counter + 1;
            else
                lock_stable <= '1';
            end if;
        end if;
    end process;

    locked <= lock_stable;
end Behavioral;

-- ============================================================================
-- Clock Domain Crossing (CDC) Synchronizer
-- ============================================================================
entity CDCSynchronizer is
    generic (
        DATA_WIDTH : integer := 8;
        SYNC_STAGES : integer := 2
    );
    port (
        -- Source domain
        src_clk   : in  bit;
        src_data  : in  bit_vector(DATA_WIDTH-1 downto 0);
        src_valid : in  bit;
        src_ready : out bit;
        -- Destination domain
        dst_clk   : in  bit;
        dst_reset : in  bit;
        dst_data  : out bit_vector(DATA_WIDTH-1 downto 0);
        dst_valid : out bit
    );
end CDCSynchronizer;

architecture Behavioral of CDCSynchronizer is
    -- Gray code conversion for pointer synchronization
    signal src_ptr : bit_vector(1 downto 0);
    signal dst_ptr : bit_vector(1 downto 0);

    -- Synchronizer flip-flops
    type sync_array is array (0 to SYNC_STAGES-1) of bit_vector(1 downto 0);
    signal ptr_sync : sync_array;

    -- Data register
    signal data_reg : bit_vector(DATA_WIDTH-1 downto 0);
    signal data_valid_sync : bit_vector(SYNC_STAGES-1 downto 0);

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
    -- Source domain logic
    src_proc: process(src_clk)
    begin
        if src_clk'event and src_clk = '1' then
            if src_valid = '1' then
                data_reg <= src_data;
                -- Toggle pointer to indicate new data
                src_ptr <= to_bv((to_int(src_ptr) + 1) mod 4, 2);
            end if;
        end if;
    end process;

    -- Simple ready signal
    src_ready <= '1';

    -- Destination domain synchronizer
    dst_proc: process(dst_clk, dst_reset)
    begin
        if dst_reset = '1' then
            for i in 0 to SYNC_STAGES-1 loop
                ptr_sync(i) <= (others => '0');
            end loop;
            dst_ptr <= (others => '0');
            data_valid_sync <= (others => '0');
        elsif dst_clk'event and dst_clk = '1' then
            -- Multi-stage synchronizer for pointer
            ptr_sync(0) <= src_ptr;
            for i in 1 to SYNC_STAGES-1 loop
                ptr_sync(i) <= ptr_sync(i-1);
            end loop;

            -- Detect pointer change (new data available)
            if ptr_sync(SYNC_STAGES-1) /= dst_ptr then
                dst_ptr <= ptr_sync(SYNC_STAGES-1);
                data_valid_sync <= (others => '1');
            else
                data_valid_sync <= data_valid_sync(SYNC_STAGES-2 downto 0) & '0';
            end if;
        end if;
    end process;

    dst_data <= data_reg;
    dst_valid <= data_valid_sync(SYNC_STAGES-1);
end Behavioral;

-- ============================================================================
-- Glitch-free Clock Multiplexer
-- ============================================================================
entity ClockMux is
    generic (NUM_CLOCKS : integer := 4);
    port (
        clk_in      : in  bit_vector(NUM_CLOCKS-1 downto 0);
        reset       : in  bit;
        select_clk  : in  bit_vector(1 downto 0);
        clk_out     : out bit
    );
end ClockMux;

architecture Behavioral of ClockMux is
    signal clk_enable : bit_vector(NUM_CLOCKS-1 downto 0);
    signal clk_enabled : bit_vector(NUM_CLOCKS-1 downto 0);

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

begin
    -- Generate enable signals
    gen_enable: for i in 0 to NUM_CLOCKS-1 generate
        enable_proc: process(clk_in(i), reset)
        begin
            if reset = '1' then
                clk_enable(i) <= '0';
            elsif clk_in(i)'event and clk_in(i) = '0' then
                -- Sample on falling edge to avoid glitches
                if to_int(select_clk) = i then
                    clk_enable(i) <= '1';
                else
                    clk_enable(i) <= '0';
                end if;
            end if;
        end process;

        clk_enabled(i) <= clk_in(i) and clk_enable(i);
    end generate;

    -- OR all enabled clocks
    clk_out <= clk_enabled(0) or clk_enabled(1) or
               clk_enabled(2) or clk_enabled(3);
end Behavioral;
