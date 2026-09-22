-- ============================================================================
-- VHDL Project: BusArbiter
-- Category: Advanced Systems
-- Complexity: High
-- Description: Multi-master bus arbiter with priority and round-robin modes
--              Supports burst transfers and locked transactions
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity BusArbiter is
    generic (
        NUM_MASTERS  : integer := 4;
        ADDR_WIDTH   : integer := 32;
        DATA_WIDTH   : integer := 32
    );
    port (
        clk          : in  bit;
        reset        : in  bit;
        -- Configuration
        mode         : in  bit;  -- '0' = priority, '1' = round-robin
        -- Master interfaces
        m_req        : in  bit_vector(NUM_MASTERS-1 downto 0);
        m_lock       : in  bit_vector(NUM_MASTERS-1 downto 0);
        m_grant      : out bit_vector(NUM_MASTERS-1 downto 0);
        m_addr       : in  bit_vector(NUM_MASTERS*ADDR_WIDTH-1 downto 0);
        m_wdata      : in  bit_vector(NUM_MASTERS*DATA_WIDTH-1 downto 0);
        m_rdata      : out bit_vector(NUM_MASTERS*DATA_WIDTH-1 downto 0);
        m_we         : in  bit_vector(NUM_MASTERS-1 downto 0);
        m_burst      : in  bit_vector(NUM_MASTERS*4-1 downto 0);  -- Burst length
        -- Slave interface
        s_addr       : out bit_vector(ADDR_WIDTH-1 downto 0);
        s_wdata      : out bit_vector(DATA_WIDTH-1 downto 0);
        s_rdata      : in  bit_vector(DATA_WIDTH-1 downto 0);
        s_we         : out bit;
        s_re         : out bit;
        s_ready      : in  bit;
        -- Status
        active_master : out bit_vector(1 downto 0);
        bus_busy     : out bit
    );
end BusArbiter;

architecture Behavioral of BusArbiter is
    type state_type is (IDLE, GRANT, TRANSFER, BURST, LOCKED);
    signal state : state_type;

    signal current_master : integer range 0 to NUM_MASTERS-1;
    signal last_master : integer range 0 to NUM_MASTERS-1;
    signal burst_count : bit_vector(3 downto 0);
    signal locked : bit;

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

    function get_addr(m : integer; arr : bit_vector) return bit_vector is
        variable result : bit_vector(ADDR_WIDTH-1 downto 0);
    begin
        for i in 0 to ADDR_WIDTH-1 loop
            result(i) := arr(m * ADDR_WIDTH + i);
        end loop;
        return result;
    end function;

    function get_data(m : integer; arr : bit_vector) return bit_vector is
        variable result : bit_vector(DATA_WIDTH-1 downto 0);
    begin
        for i in 0 to DATA_WIDTH-1 loop
            result(i) := arr(m * DATA_WIDTH + i);
        end loop;
        return result;
    end function;

    function get_burst(m : integer; arr : bit_vector) return bit_vector is
        variable result : bit_vector(3 downto 0);
    begin
        for i in 0 to 3 loop
            result(i) := arr(m * 4 + i);
        end loop;
        return result;
    end function;

begin
    arbiter_proc: process(clk, reset)
        variable next_master : integer;
        variable found : bit;
        variable start_idx : integer;
    begin
        if reset = '1' then
            state <= IDLE;
            current_master <= 0;
            last_master <= 0;
            burst_count <= (others => '0');
            locked <= '0';
            m_grant <= (others => '0');
            s_addr <= (others => '0');
            s_wdata <= (others => '0');
            s_we <= '0';
            s_re <= '0';
            bus_busy <= '0';
        elsif clk'event and clk = '1' then
            s_we <= '0';
            s_re <= '0';

            case state is
                when IDLE =>
                    m_grant <= (others => '0');
                    bus_busy <= '0';
                    locked <= '0';

                    -- Arbitration
                    found := '0';

                    if mode = '0' then
                        -- Priority arbitration (lower index = higher priority)
                        for i in 0 to NUM_MASTERS-1 loop
                            if m_req(i) = '1' then
                                next_master := i;
                                found := '1';
                                exit;
                            end if;
                        end loop;
                    else
                        -- Round-robin arbitration
                        start_idx := (last_master + 1) mod NUM_MASTERS;
                        for i in 0 to NUM_MASTERS-1 loop
                            next_master := (start_idx + i) mod NUM_MASTERS;
                            if m_req(next_master) = '1' then
                                found := '1';
                                exit;
                            end if;
                        end loop;
                    end if;

                    if found = '1' then
                        current_master <= next_master;
                        m_grant(next_master) <= '1';
                        bus_busy <= '1';

                        -- Check for locked transaction
                        if m_lock(next_master) = '1' then
                            locked <= '1';
                            state <= LOCKED;
                        -- Check for burst transfer
                        elsif get_burst(next_master, m_burst) /= "0000" then
                            burst_count <= get_burst(next_master, m_burst);
                            state <= BURST;
                        else
                            state <= GRANT;
                        end if;
                    end if;

                when GRANT =>
                    -- Single transfer
                    s_addr <= get_addr(current_master, m_addr);
                    s_wdata <= get_data(current_master, m_wdata);
                    s_we <= m_we(current_master);
                    s_re <= not m_we(current_master);
                    state <= TRANSFER;

                when TRANSFER =>
                    if s_ready = '1' then
                        -- Transfer complete
                        last_master <= current_master;
                        m_grant(current_master) <= '0';
                        state <= IDLE;
                    end if;

                when BURST =>
                    s_addr <= get_addr(current_master, m_addr);
                    s_wdata <= get_data(current_master, m_wdata);
                    s_we <= m_we(current_master);
                    s_re <= not m_we(current_master);

                    if s_ready = '1' then
                        if burst_count = "0001" then
                            -- Burst complete
                            last_master <= current_master;
                            m_grant(current_master) <= '0';
                            state <= IDLE;
                        else
                            burst_count <= to_bv(to_int(burst_count) - 1, 4);
                        end if;
                    end if;

                when LOCKED =>
                    -- Locked transaction - maintain grant until lock released
                    s_addr <= get_addr(current_master, m_addr);
                    s_wdata <= get_data(current_master, m_wdata);

                    if m_req(current_master) = '1' then
                        s_we <= m_we(current_master);
                        s_re <= not m_we(current_master);
                    else
                        s_we <= '0';
                        s_re <= '0';
                    end if;

                    if m_lock(current_master) = '0' then
                        -- Lock released
                        last_master <= current_master;
                        m_grant(current_master) <= '0';
                        locked <= '0';
                        state <= IDLE;
                    end if;
            end case;
        end if;
    end process;

    -- Read data broadcast to all masters
    rdata_broadcast: process(s_rdata)
    begin
        for i in 0 to NUM_MASTERS-1 loop
            for j in 0 to DATA_WIDTH-1 loop
                m_rdata(i * DATA_WIDTH + j) <= s_rdata(j);
            end loop;
        end loop;
    end process;

    active_master <= to_bv(current_master, 2);
end Behavioral;

-- ============================================================================
-- Crossbar Switch (for multi-slave)
-- ============================================================================
entity CrossbarSwitch is
    generic (
        NUM_MASTERS : integer := 4;
        NUM_SLAVES  : integer := 4;
        ADDR_WIDTH  : integer := 32;
        DATA_WIDTH  : integer := 32
    );
    port (
        clk         : in  bit;
        reset       : in  bit;
        -- Master ports
        m_addr      : in  bit_vector(NUM_MASTERS*ADDR_WIDTH-1 downto 0);
        m_wdata     : in  bit_vector(NUM_MASTERS*DATA_WIDTH-1 downto 0);
        m_rdata     : out bit_vector(NUM_MASTERS*DATA_WIDTH-1 downto 0);
        m_we        : in  bit_vector(NUM_MASTERS-1 downto 0);
        m_re        : in  bit_vector(NUM_MASTERS-1 downto 0);
        m_ready     : out bit_vector(NUM_MASTERS-1 downto 0);
        -- Slave ports
        s_addr      : out bit_vector(NUM_SLAVES*ADDR_WIDTH-1 downto 0);
        s_wdata     : out bit_vector(NUM_SLAVES*DATA_WIDTH-1 downto 0);
        s_rdata     : in  bit_vector(NUM_SLAVES*DATA_WIDTH-1 downto 0);
        s_we        : out bit_vector(NUM_SLAVES-1 downto 0);
        s_re        : out bit_vector(NUM_SLAVES-1 downto 0);
        s_ready     : in  bit_vector(NUM_SLAVES-1 downto 0)
    );
end CrossbarSwitch;

architecture Behavioral of CrossbarSwitch is
    -- Connection matrix: which master is connected to which slave
    type conn_matrix is array (0 to NUM_SLAVES-1) of integer;
    signal slave_master : conn_matrix;
    signal slave_busy : bit_vector(NUM_SLAVES-1 downto 0);

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

    function get_slave_select(addr : bit_vector) return integer is
    begin
        -- Simple address decoding: top 2 bits select slave
        return to_int(addr(ADDR_WIDTH-1 downto ADDR_WIDTH-2)) mod NUM_SLAVES;
    end function;

begin
    crossbar_proc: process(clk, reset)
        variable target_slave : integer;
    begin
        if reset = '1' then
            slave_busy <= (others => '0');
            m_ready <= (others => '0');
            s_we <= (others => '0');
            s_re <= (others => '0');
            for i in 0 to NUM_SLAVES-1 loop
                slave_master(i) <= 0;
            end loop;
        elsif clk'event and clk = '1' then
            m_ready <= (others => '0');
            s_we <= (others => '0');
            s_re <= (others => '0');

            -- Route each master to appropriate slave
            for m in 0 to NUM_MASTERS-1 loop
                if m_we(m) = '1' or m_re(m) = '1' then
                    -- Decode target slave from address
                    for i in 0 to ADDR_WIDTH-1 loop
                        target_slave := get_slave_select(
                            m_addr((m+1)*ADDR_WIDTH-1 downto m*ADDR_WIDTH));
                    end loop;

                    if slave_busy(target_slave) = '0' then
                        -- Connect master to slave
                        slave_master(target_slave) <= m;
                        slave_busy(target_slave) <= '1';

                        -- Forward address and data
                        for i in 0 to ADDR_WIDTH-1 loop
                            s_addr(target_slave*ADDR_WIDTH + i) <=
                                m_addr(m*ADDR_WIDTH + i);
                        end loop;
                        for i in 0 to DATA_WIDTH-1 loop
                            s_wdata(target_slave*DATA_WIDTH + i) <=
                                m_wdata(m*DATA_WIDTH + i);
                        end loop;

                        s_we(target_slave) <= m_we(m);
                        s_re(target_slave) <= m_re(m);
                    end if;
                end if;
            end loop;

            -- Handle responses
            for s in 0 to NUM_SLAVES-1 loop
                if slave_busy(s) = '1' and s_ready(s) = '1' then
                    -- Forward read data to master
                    for i in 0 to DATA_WIDTH-1 loop
                        m_rdata(slave_master(s)*DATA_WIDTH + i) <=
                            s_rdata(s*DATA_WIDTH + i);
                    end loop;
                    m_ready(slave_master(s)) <= '1';
                    slave_busy(s) <= '0';
                end if;
            end loop;
        end if;
    end process;
end Behavioral;
